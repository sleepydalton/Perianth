using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perianth.Core.Content;
using Perianth.Core.Io;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Formats.Sdf;
using Perianth.Gltf;

namespace Perianth.Gui;

/// <summary>
/// Bringing a model back after it has been reshaped in Blender.
/// </summary>
/// <remarks>
/// <para>
/// The window's half of the <c>geometry</c> verb. The command line is where this
/// was built and proven; the window is where it is used, and the shape of the
/// pane follows from what a person actually has in front of them — a model
/// already chosen on the left, and one edited file on disk.
/// </para>
/// <para>
/// It is deliberately three steps and no options. Everything the verb asks for
/// besides the edited GLB is already known here: which model, where its files
/// live, and where the user keeps their work. Asking again would be a form to
/// fill in for a question already answered.
/// </para>
/// </remarks>
public sealed class ShapeViewModel : ViewModelBase
{
    /// <summary>Files this pane wrote into a preview folder last time.</summary>
    /// <remarks>
    /// The same guard the costume pane needs, for the same reason: a preview
    /// folder outlives the session that filled it, so a reshape from yesterday
    /// would still be sitting there and would quietly apply to today's export.
    /// Recorded rather than inferred, so withdrawing removes what this pane put
    /// there and nothing else.
    /// </remarks>
    private const string Ledger = "perianth-shape-files.txt";

    private string? _archiveRoot;
    private string? _previewRoot;
    private string? _contentRoot;
    private CharacterAssets? _assets;
    private GeometryImportResult? _edit;
    private byte[]? _written;
    private string _status = "Choose a model on the left.";
    private string _summary = string.Empty;
    private string _chosen = string.Empty;
    private string _saved = string.Empty;
    private bool _busy;
    private bool _ownUv0;

    public ShapeViewModel()
    {
        ChooseCommand = new RelayCommand(() => ChooseRequested?.Invoke());
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
    }

    /// <summary>Asks the window for the edited GLB.</summary>
    public event Action? ChooseRequested;

    /// <summary>Asks the window where to put the mod.</summary>
    public event Action? SaveRequested;

    /// <summary>Raised when a reshape is taken up or dropped, so a preview can be rebuilt.</summary>
    public event Action? Changed;

    /// <summary>
    /// Whatever else is staged for this model, to go into the same mod.
    /// </summary>
    /// <remarks>
    /// A reshape and a repaint are one piece of work. Writing the geometry alone
    /// leaves a mod that installs and draws the model with its original art,
    /// which reads as the texture edits having been lost. The window supplies
    /// this; the pane does not know which other pane it comes from.
    /// </remarks>
    public Func<ImmutableArray<ModFile>>? AlsoStaged { get; set; }

    public RelayCommand ChooseCommand { get; }

    public RelayCommand SaveCommand { get; }

    /// <summary>
    /// Whether a redrawn part should store the texture layout the mesh brought.
    /// </summary>
    /// <remarks>
    /// Off by default. Most parts work their layout out from where their points
    /// sit, which is right for a flat shape and wrong for a solid one — and a
    /// part that brought a layout and was left working its own out is said
    /// afterwards, so this is never decided in silence.
    /// </remarks>
    public bool OwnUv0
    {
        get => _ownUv0;
        set => Set(ref _ownUv0, value);
    }

    /// <summary>What the pane is saying.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>What the last reshape would change, in words.</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>What the last save wrote, and where. Empty until one happens.</summary>
    public string Saved
    {
        get => _saved;
        private set => Set(ref _saved, value);
    }

    /// <summary>The edited file's name, so it is visible which one is loaded.</summary>
    public string Chosen
    {
        get => _chosen;
        private set => Set(ref _chosen, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>Whether this model can be reshaped at all.</summary>
    public bool Applies => _assets?.Cameldata is not null;

    /// <summary>Whether there is a reshape to save.</summary>
    public bool HasEdit => _edit is not null;

    /// <summary>
    /// Where previews are written, so a reshape can be withdrawn the moment it
    /// stops applying rather than at the next export.
    /// </summary>
    public void UseWorkingFolder(string? root) => _previewRoot = root;

    /// <summary>The archives to read the model's own files from.</summary>
    public void UseArchives(string root, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = root;
        _ = paths;
    }

    /// <summary>A folder of already-extracted files, used in preference to the archives.</summary>
    public void UseFolder(string root, ImmutableArray<SdfPathEntry> paths)
    {
        _contentRoot = root;
        _ = paths;
    }

    /// <summary>Shows a model, dropping any reshape held for the previous one.</summary>
    public void Show(CharacterAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        // A reshape belongs to the model it was made for, so choosing another
        // ends it -- and ends it on disk, not only in memory. Waiting for the
        // next export to withdraw it leaves the previous character reshaped in
        // a folder the export reads, which is indistinguishable from the tool
        // remembering edits it was never asked to keep.
        bool changed = _assets is null ||
            !string.Equals(_assets.Model, assets.Model, StringComparison.Ordinal);

        _assets = assets;
        if (changed)
        {
            Discard();
        }
        else
        {
            Drop();
        }

        Status = assets.Cameldata is null
            ? "This model has no cameldata, and a part's positions live in one, so it cannot be edited."
            : "Export this model, edit its parts in Blender, then load the edited file here.";

        Raise(nameof(Applies));
    }

    /// <summary>Reads an edited GLB and works out what it would change.</summary>
    public async Task LoadAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_assets is null || _assets.Cameldata is null)
        {
            return;
        }

        Busy = true;
        Chosen = Path.GetFileName(path);

        try
        {
            Result<GeometryImportResult> edit = await Task.Run(() => Apply(path)).ConfigureAwait(true);
            if (!edit.TryGetValue(out GeometryImportResult? result, out Refusal? refusal))
            {
                Drop();
                Status = refusal.Message;
                return;
            }

            _edit = result;
            Result<byte[]> bytes = CameldataWriter.Write(result.Cameldata);
            if (!bytes.TryGetValue(out byte[]? written, out Refusal? writeRefusal))
            {
                Drop();
                Status = writeRefusal.Message;
                return;
            }

            _written = written;
            Summary = Describe(result);

            Status = "Tick 'Include my changes' in the Export panel on the right, then export, to see it in Blender before installing it.";
        }
        finally
        {
            Busy = false;
            Raise(nameof(HasEdit));
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// What the edit changed, in the terms its two halves differ in.
    /// </summary>
    /// <remarks>
    /// A redrawn part has no old positions to have moved from, so it is reported
    /// by what it now draws. Saying which parts were which is worth a line: an
    /// author who moved one part and finds forty redrawn has a mesh that was
    /// re-welded on the way out, and the count is where that shows.
    /// </remarks>
    private static string Describe(GeometryImportResult edit)
    {
        List<string> said = [];

        if (edit.Added > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Added} new parts were added, bound to '{edit.AddedBinding}' — the node the model's last part uses. If the animation hides that node they will not draw"));
        }

        if (edit.Reshaped > 0)
        {
            string depths = edit.Depths > 0
                ? string.Create(CultureInfo.InvariantCulture, $", and {edit.Depths} depths changed")
                : string.Empty;
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Slots} vertex positions moved across {edit.Reshaped} parts{depths}"));
        }

        if (edit.Rebuilt > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Rebuilt} parts were redrawn, and now draw {edit.Triangles} triangles"));
        }

        if (edit.Converted > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Converted} parts now store the texture layout from your file"));
        }

        // Said whether or not it is wanted. Nothing afterwards shows whether a
        // part was painted as its author drew it or by a projection.
        if (edit.LayoutIgnored > edit.LayoutUnconvertible)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.LayoutIgnored - edit.LayoutUnconvertible} parts work their texture layout out from position, so the one in your file was not used — fine for flat shapes, wrong for solid ones. Tick the box below and load it again to store yours instead"));
        }

        // Ticking the box again would change nothing here, so the advice above
        // would be wrong. Only a redrawn part can change which rule it uses.
        if (edit.LayoutUnconvertible > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.LayoutUnconvertible} parts kept their arrangement, so they were moved rather than redrawn and could not be switched to your layout — change their triangles as well as their points"));
        }

        if (edit.Uv0Slots > 0)
        {
            said.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Uv0Slots} texture coordinates moved with the points that carry them"));
        }

        return string.Join("; ", said) + ".";
    }

    /// <summary>Forgets the loaded reshape and removes what it wrote.</summary>
    public void Discard()
    {
        Drop();

        if (_previewRoot is string root && Directory.Exists(root))
        {
            _ = OverlayLedger.Withdraw(root, Ledger);
        }
    }

    /// <summary>Forgets the loaded reshape.</summary>
    public void Drop()
    {
        _edit = null;
        _written = null;
        Chosen = string.Empty;
        Summary = string.Empty;
        Saved = string.Empty;
        Raise(nameof(HasEdit));
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts the reshaped cameldata into a preview folder.
    /// </summary>
    /// <remarks>
    /// The cameldata always, and the model only when a part was redrawn — that is
    /// the one case where the MMB differs from the archived one, and a preview
    /// reading the old payloads beside the new pools would show neither edit.
    /// Where nothing was redrawn the model is left out rather than copied
    /// unchanged, so the folder holds what this pane actually altered.
    /// </remarks>
    public Result<int> OverlayInto(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        Result<int> cleared = OverlayLedger.Withdraw(root, Ledger);
        if (cleared.IsRefused)
        {
            return cleared.Refusal;
        }

        if (_written is null || _edit is null || _assets?.Cameldata is not string virtualPath)
        {
            return Result.Ok(0);
        }

        List<(string Path, byte[] Bytes)> files = [(virtualPath, _written)];
        if (_edit.Rebuilt > 0)
        {
            files.Add((_assets.Model, _edit.Model));
        }

        try
        {
            foreach ((string path, byte[] bytes) in files)
            {
                string destination = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Result<int> published = AtomicFile.Publish(destination, bytes);
                if (published.IsRefused)
                {
                    return published.Refusal;
                }
            }

            _ = OverlayLedger.Record(root, Ledger, [.. files.Select(f => f.Path)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"The edited model could not be written into '{root}': {ex.Message}");
        }

        return Result.Ok(files.Count);
    }

    /// <summary>Writes the reshape as a mod, with the model beside it.</summary>
    public Result<ModOutcome> Save(string destination, string name, string author)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (_written is null || _edit is null || _assets is null || _assets.Cameldata is not string cameldataPath)
        {
            return Refusal.Unsupported("There is no edit to save.");
        }

        // Both files. They are a matched pair, and the mod is where they leave
        // the archives behind -- so the model travels even when it is the
        // original byte for byte, which is what it is unless a part was redrawn.
        //
        // Anything the texture pane has staged for this model joins it too, so
        // one folder carries the whole change rather than the geometry half.
        List<ModFile> files =
        [
            new ModFile(cameldataPath, _written),
            new ModFile(_assets.Model, _edit.Model),
            .. AlsoStaged?.Invoke() ?? [],
        ];

        return TextureMod.Write(
            destination,
            new ModDetails(name, author, "1.0.0", name, PreloadCustomAssets: false),
            files);
    }

    /// <summary>
    /// Writes the mod into <paramref name="folder"/> and says what happened.
    /// </summary>
    /// <remarks>
    /// The mod is named after the model rather than asked for. A name is the
    /// folder it lands in and the line the loader shows, and neither is worth a
    /// dialogue when the model is already chosen; it can be renamed afterwards
    /// like any folder.
    /// </remarks>
    public void SaveInto(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string name = _assets is null
            ? "reshape"
            : Path.GetFileNameWithoutExtension(_assets.Model) + " reshaped";

        Result<ModOutcome> mod = Save(folder, name, "unknown");
        if (!mod.TryGetValue(out ModOutcome? outcome, out Refusal? refusal))
        {
            Saved = string.Empty;
            Status = refusal.Message;
            return;
        }

        // Said where it can be seen rather than in the status line at the top of
        // the pane, which is where a message goes to be missed. Naming the files
        // is the point: a reshape that quietly wrote two when the author expected
        // their texture edits as well is the failure this reports its way out of.
        // Named, not counted. "3 staged files" left an author looking in the
        // folder for a texture that was never going to be there: a colour chosen
        // from the palette repoints a part at art the game already ships, so what
        // gets written is the material list rather than an image. Listing the
        // files says that without having to explain it.
        string listed = string.Join(Environment.NewLine, outcome.Files.Select(f => "    " + f));

        Saved = string.Create(
            CultureInfo.InvariantCulture,
            $"Saved to {outcome.Folder}{Environment.NewLine}{listed}{Environment.NewLine}Copy that folder into FractureLoader/Mods/ to use it.");
        Status = "Saved.";
    }

    /// <summary>Asks the window for somewhere to save.</summary>
    private Task SaveAsync()
    {
        SaveRequested?.Invoke();
        return Task.CompletedTask;
    }

    private Result<GeometryImportResult> Apply(string glbPath)
    {
        if (_assets?.Cameldata is not string cameldataPath)
        {
            return Refusal.Unsupported("This model has no cameldata to edit.");
        }

        using ContentSources content = new(_contentRoot, _archiveRoot);

        // The file itself, not only its records: redrawing a part writes a
        // payload back into the bytes it was read from.
        Result<byte[]?> found = content.Read(_assets.Model);
        if (!found.TryGetValue(out byte[]? modelBytes, out Refusal? readRefusal))
        {
            return readRefusal;
        }

        if (modelBytes is null)
        {
            return Refusal.Resource($"'{_assets.Model}' is not in the archives or the folder being used.");
        }

        SourceFile modelSource = SourceFile.FromMemory(_assets.Model, modelBytes);
        Result<MmbModel> model = MmbReader.Read(modelSource);
        if (!model.TryGetValue(out MmbModel? mmb, out Refusal? modelRefusal))
        {
            return modelRefusal;
        }

        Result<CameldataFile> cameldata = ReadFromArchives<CameldataFile>(content, cameldataPath, CameldataReader.Read);
        if (!cameldata.TryGetValue(out CameldataFile? camel, out Refusal? cameldataRefusal))
        {
            return cameldataRefusal;
        }

        byte[] glb;
        try
        {
            glb = File.ReadAllBytes(glbPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{Path.GetFileName(glbPath)}' could not be read: {ex.Message}");
        }

        Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb);
        if (!meshes.TryGetValue(out ImmutableArray<GlbMesh> read, out Refusal? glbRefusal))
        {
            return glbRefusal;
        }

        Result<GeometryImportResult> edit = GeometryImport.Apply(
            modelSource,
            mmb,
            camel,
            [.. read.Select(m => new EditedPart(m.Name, m.Positions, m.PoolSlots, m.Indices, m.Uv0))],
            _ownUv0);
        if (edit.IsRefused)
        {
            return edit.Refusal;
        }

        if (!edit.Value.Moved)
        {
            // Almost always one thing: the parts were moved in Object Mode, which
            // moves the object rather than its vertices, and only the vertices
            // are read back. The message names the cause, because "nothing
            // changed" sends someone to look for a fault in what they did rather
            // than in which mode they did it in.
            return Refusal.Unsupported(
                "Nothing moved. The usual cause is editing in Object Mode, which moves the whole object "
                + "and leaves its vertices where they were. Select the part, press Tab for Edit Mode, "
                + "press A to select its vertices, then move or scale those.");
        }

        return edit;
    }

    private static Result<T> ReadFromArchives<T>(
        ContentSources content, string virtualPath, Func<SourceFile, Result<T>> read)
    {
        Result<byte[]?> bytes = content.Read(virtualPath);
        if (!bytes.TryGetValue(out byte[]? found, out Refusal? refusal))
        {
            return refusal;
        }

        if (found is null)
        {
            return Refusal.Resource($"'{virtualPath}' is not in the archives or the folder being used.");
        }

        return read(SourceFile.FromMemory(virtualPath, found));
    }
}
