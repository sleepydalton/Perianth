using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Linq;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Core.Imaging;
using Perianth.Core.Io;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Formats.Sdf;

namespace Perianth.Gui;

/// <summary>One texture as the grid shows it.</summary>
/// <param name="Name">The file's own name, without its folder.</param>
/// <param name="Path">The archive path, for the tooltip.</param>
/// <param name="Detail">Channel, size and how many materials bind it.</param>
/// <param name="Image">The decoded picture, or null when it could not be read.</param>
/// <param name="Note">Why there is no picture, when there is not.</param>
public sealed record TextureThumbnail(
    string Name, string Path, string Detail, Bitmap? Image, string Note);

/// <summary>One file staged to go into a mod, and what it is for.</summary>
/// <param name="Path">The archive path it will be written to.</param>
/// <param name="Purpose">
/// Plain English: which parts it was added for, or that it replaces a texture
/// the game ships, or that it is this model's material list. A path alone does
/// not distinguish two custom textures added for two different parts, and
/// telling those apart is the whole reason somebody reads this list.
/// </param>
public sealed record StagedFile(string Path, string Purpose);

/// <summary>
/// The textures a model's materials bind, decoded and shown.
/// </summary>
/// <remarks>
/// <para>
/// Drawn as RGBA over a checkerboard, exactly as decoded — the same decoder the
/// export runs, at the size the file is. Nothing here resizes an image: the
/// thumbnail is the full picture drawn small, so the bake's rule that no
/// resampler is reachable holds by there being no resampler in the path at all.
/// </para>
/// <para>
/// That presentation matters more than it sounds. A character's own art is
/// often carried in alpha with black RGB — Cartman's face and body are two such
/// masks — so a viewer that ignored alpha would show two black squares for the
/// only two textures that say who this is.
/// </para>
/// </remarks>
public sealed class TextureViewModel : ViewModelBase
{
    /// <summary>How many thumbnails the grid shows before it is asked for more.</summary>
    /// <remarks>
    /// A character binds tens of textures — 80 for Cartman, 89 for his mother,
    /// most of them one mouth shape each. Decoding all of them costs about a
    /// second and 70 MB of bitmaps, which is a poor thing to spend on a pane
    /// nobody has opened.
    /// </remarks>
    private const int FirstScreenful = 24;

    private readonly List<TextureReference> _listed = [];
    private ObservableCollection<TextureThumbnail> _thumbnails = [];
    private readonly Dictionary<string, byte[]> _replacements = new(StringComparer.Ordinal);

    /// <summary>
    /// The last path this pane put into the box itself, so a proposal can be
    /// told from something the user typed.
    /// </summary>
    /// <remarks>
    /// The box is filled in after a successful addition, deliberately, so that
    /// what was used is visible. That is also what made a second addition
    /// overwrite the first: the box outlived the selection it was proposed for,
    /// and the next image went to the same path. Knowing which value is ours is
    /// what lets a typed path still win while a stale proposal is replaced.
    /// </remarks>
    private string _proposed = string.Empty;

    /// <summary>
    /// What each added path was added *for* — the texture and parts it aimed at.
    /// </summary>
    /// <remarks>
    /// Kept across writing a mod, because it describes the model's state rather
    /// than the session's, exactly as the accumulated edits in
    /// <c>_editordata</c> do. Discarding is what clears it.
    /// </remarks>
    private readonly Dictionary<string, string> _addedFor = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pending;
    private string? _archiveRoot;
    private string? _contentRoot;
    private ImmutableArray<SdfPathEntry> _paths;
    private ImmutableArray<PaperSwatch> _palette;
    private SwatchChoice? _selectedSwatch;
    private string? _workingFolder;
    private CharacterAssets? _assets;
    private EditordataFile? _editordata;
    private EditordataFile? _pristine;
    private string? _editordataPath;
    private TextureThumbnail? _selected;
    private string _repointTo = string.Empty;
    private string _retintTo = string.Empty;
    private string _parts = string.Empty;
    private string _status = "Choose a file on the left.";
    private string _modName = string.Empty;
    private string _modAuthor = string.Empty;
    private string _modVersion = "1.0.0";
    private string _modDescription = string.Empty;
    private bool _preloadCustomAssets;
    private bool _all;
    private bool _busy;
    private bool _loaded;

    public TextureViewModel()
    {
        ShowAllCommand = new RelayCommand(ShowAll, () => More > 0);
        SaveAsPngCommand = new RelayCommand(() => SaveRequested?.Invoke(), () => _selected?.Image is not null);
        ReplaceCommand = new RelayCommand(() => ReplaceRequested?.Invoke(), () => _selected is not null);
        WriteModCommand = new RelayCommand(() => WriteRequested?.Invoke(), () => _replacements.Count > 0);
        SavePatchesCommand = new RelayCommand(() => PatchRequested?.Invoke(), () => _replacements.Count > 0);
        ForgetCommand = new RelayCommand(Forget, () => _replacements.Count > 0);
        RepointCommand = new RelayCommand(
            Repoint, () => _selected is not null && _editordata is not null && _repointTo.Length > 0);
        RetintCommand = new RelayCommand(
            Retint, () => _selected is not null && _editordata is not null && _retintTo.Length > 0);
        // A selection or named parts, not both. Naming the part already says
        // which binding to change, so making the grid answer it again is a step
        // with no question behind it.
        AddCommand = new RelayCommand(
            () => AddRequested?.Invoke(),
            () => _editordata is not null && (_selected is not null || ParsedParts() is { Count: > 0 }));
    }

    /// <summary>The decoded textures, in the order the grid lists them.</summary>
    /// <remarks>
    /// Replaced wholesale rather than emptied — see <see cref="ResetThumbnails"/>
    /// — so the reference a caller holds is only good until the model changes.
    /// </remarks>
    public ObservableCollection<TextureThumbnail> Thumbnails => _thumbnails;

    /// <summary>The files waiting to go into a mod, in order.</summary>
    /// <remarks>
    /// Each carries what it is <em>for</em> as well as where it goes. A list of
    /// paths alone was legible only to whoever had just typed them: two custom
    /// textures for two parts differ by a few characters near the end of a
    /// long path, which is exactly the pair somebody needs to tell apart.
    /// </remarks>
    public ObservableCollection<StagedFile> Replacing { get; } = [];

    /// <summary>Shows the ones held back by the cap.</summary>
    public RelayCommand ShowAllCommand { get; }

    /// <summary>Writes the selected texture out as a PNG to edit.</summary>
    public RelayCommand SaveAsPngCommand { get; }

    /// <summary>Takes an edited PNG back in against the selected texture.</summary>
    public RelayCommand ReplaceCommand { get; }

    /// <summary>Writes everything gathered so far as one mod.</summary>
    public RelayCommand WriteModCommand { get; }

    /// <summary>Writes everything gathered as patches, to share.</summary>
    public RelayCommand SavePatchesCommand { get; }

    /// <summary>Drops what has been gathered without writing it.</summary>
    public RelayCommand ForgetCommand { get; }

    /// <summary>Binds another texture wherever the selected one is bound.</summary>
    public RelayCommand RepointCommand { get; }

    /// <summary>Recolours the parts the selected texture is bound to.</summary>
    public RelayCommand RetintCommand { get; }

    /// <summary>Adds an image as a new texture, and points this model at it.</summary>
    public RelayCommand AddCommand { get; }

    /// <summary>Raised when a file dialog is needed, which only the window can raise.</summary>
    public event Action? SaveRequested;

    public event Action? ReplaceRequested;

    public event Action? AddRequested;

    public event Action? WriteRequested;

    public event Action? PatchRequested;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>
    /// Whether every channel is shown, rather than the pictures.
    /// </summary>
    /// <remarks>
    /// Off, the grid shows the model's own textures and every DiffuseColor: the
    /// art, and the mouth and eye sheets. On, it adds the masks and the 16-pixel
    /// constants bound to NormalMap and SpecularColor, which are worth seeing
    /// when something looks wrong and are noise otherwise.
    /// </remarks>
    public bool ShowEveryChannel
    {
        get => _all;
        set
        {
            if (Set(ref _all, value))
            {
                _ = ReloadAsync();
            }
        }
    }

    /// <summary>How many textures the cap is holding back.</summary>
    public int More => Math.Max(0, Filtered().Count - Thumbnails.Count);

    public bool HasMore => More > 0;

    /// <summary>Names the button, since the number is the useful part of it.</summary>
    public string ShowAllText => string.Create(CultureInfo.InvariantCulture, $"Show {More} more");

    /// <summary>
    /// Which texture the buttons act on.
    /// </summary>
    /// <remarks>
    /// The grid knows each texture's archive path already, so replacing one
    /// here needs no provenance lookup — unlike the command line, where the
    /// only thing in hand is a file someone extracted earlier.
    /// </remarks>
    public TextureThumbnail? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                SaveAsPngCommand.Reconsider();
                ReplaceCommand.Reconsider();
                RepointCommand.Reconsider();
                RetintCommand.Reconsider();
                AddCommand.Reconsider();
                OfferColours();
                Raise(nameof(SelectedName));
                Raise(nameof(SelectedAdvice));
            }
        }
    }

    /// <summary>What the buttons will act on, for the pane to show.</summary>
    public string SelectedName => _selected?.Name ?? "nothing selected";

    /// <summary>
    /// The costume colours on offer for the selected texture, or empty.
    /// </summary>
    /// <remarks>
    /// Filled only when the selection is one of the game's 80 paper scans. A
    /// material binding a face, a logo or a blank sheet is not a colour, and a
    /// grid of eighty swatches beside it would invite an edit that makes no
    /// sense.
    /// </remarks>
    public ObservableCollection<SwatchChoice> Swatches { get; } = [];

    /// <summary>Whether the selected texture is a colour that can be changed.</summary>
    public bool SelectedIsColour => Swatches.Count > 0;

    /// <summary>
    /// The colour chosen from the grid, which fills in the repoint target.
    /// </summary>
    /// <remarks>
    /// It sets the same field the text box does rather than acting on its own.
    /// The edit stays one operation with one button, so choosing a colour and
    /// typing a path cannot mean two different things — and the path is left
    /// visible, because what is about to be written should not be hidden behind
    /// a swatch.
    /// </remarks>
    public SwatchChoice? SelectedSwatch
    {
        get => _selectedSwatch;
        set
        {
            if (Set(ref _selectedSwatch, value) && value is not null)
            {
                RepointTo = value.Swatch.TexturePath;
            }
        }
    }

    /// <summary>Offers the palette when the selection is one of its papers.</summary>
    private void OfferColours()
    {
        Swatches.Clear();
        _selectedSwatch = null;

        if (_selected is not null && !_palette.IsDefaultOrEmpty)
        {
            PaperSwatch? current = PaperPalette.Match(_palette, _selected.Path);
            if (current is not null)
            {
                foreach (PaperSwatch swatch in _palette)
                {
                    Swatches.Add(new SwatchChoice(
                        swatch, string.Equals(swatch.Name, current.Name, StringComparison.Ordinal)));
                }
            }
        }

        Raise(nameof(SelectedSwatch));
        Raise(nameof(SelectedIsColour));
    }

    /// <summary>
    /// Which of the two edits will actually do something to this texture's parts.
    /// </summary>
    /// <remarks>
    /// Roadmap §6.11. Half a model's parts carry their colour in the image and
    /// half carry it in the tint, so each edit is inert on the other's half.
    /// Saying which is which is the difference between an edit that works first
    /// time and one that appears to do nothing. The tint decides it: a part
    /// painted with coloured paper leaves its tint at (1,1,1) in all 60,365
    /// corpus sections, so a tint that is anything else is a blank sheet being
    /// coloured.
    /// </remarks>
    public string SelectedAdvice
    {
        get
        {
            if (_selected is null || _editordata is null)
            {
                return string.Empty;
            }

            (int repointable, int tinted) = Populations(_selected.Path);

            if (repointable == 0)
            {
                return "Nothing in this model binds it.";
            }

            return tinted == 0
                ? $"Painted onto {repointable} parts, none of them tinted. Repointing changes them."
                : $"Painted onto {repointable} parts, {tinted} of them coloured by a tint rather than by "
                  + "the image. Repointing will not change those; recolouring will.";
        }
    }

    /// <summary>The path to bind instead, as typed.</summary>
    public string RepointTo
    {
        get => _repointTo;
        set
        {
            if (Set(ref _repointTo, value))
            {
                RepointCommand.Reconsider();
            }
        }
    }

    /// <summary>
    /// Which parts to change, as section numbers, or empty for all of them.
    /// </summary>
    /// <remarks>
    /// A section number is a model part's ordinal, and nothing in the editordata
    /// says which part is which: material names are the artist's — the paper or
    /// marker used, never the anatomy. The number comes from looking at the
    /// model, where an exported GLB names each mesh <c>mode3-record-N</c> and
    /// that N is this number.
    /// </remarks>
    public string Parts
    {
        get => _parts;
        set
        {
            if (Set(ref _parts, value))
            {
                Raise(nameof(PartsNote));
                Raise(nameof(AddButtonText));
                AddCommand.Reconsider();
            }
        }
    }

    /// <summary>
    /// The add button's label, which says what it will actually do.
    /// </summary>
    /// <remarks>
    /// Written out rather than left to a tooltip because the two operations on
    /// this pane produce the same-looking result and differ only in who else is
    /// affected — and "give it its own copy" is the phrase that says why one
    /// model changing does not change the rest.
    /// </remarks>
    public string AddButtonText =>
        ParsedParts() is { Count: > 0 } chosen
            ? $"Paint {(chosen.Count == 1 ? "part" : "parts")} {string.Join(", ", chosen)} with my image…"
            : "Give this model its own copy, from my image…";

    /// <summary>What the parts box will do, in words.</summary>
    public string PartsNote =>
        _parts.Trim().Length == 0
            ? "Every part painted with the selected texture."
            : ParsedParts() is { Count: > 0 } chosen
                ? $"Only {(chosen.Count == 1 ? "part" : "parts")} {string.Join(", ", chosen)}."
                : "Not a list of part numbers — separate them with commas.";

    /// <summary>The colour to give the tinted parts, as three decimals.</summary>
    public string RetintTo
    {
        get => _retintTo;
        set
        {
            if (Set(ref _retintTo, value))
            {
                RetintCommand.Reconsider();
            }
        }
    }

    /// <summary>The mod's name, which is also its folder.</summary>
    public string ModName
    {
        get => _modName;
        set => Set(ref _modName, value);
    }

    /// <summary>Whoever is making it.</summary>
    public string ModAuthor
    {
        get => _modAuthor;
        set => Set(ref _modAuthor, value);
    }

    /// <summary>The mod's version, as free text.</summary>
    /// <remarks>
    /// Not a number and not validated: a shipped mod was observed declaring
    /// <c>25 WIP</c>, and the loader only displays it.
    /// </remarks>
    public string ModVersion
    {
        get => _modVersion;
        set => Set(ref _modVersion, value);
    }

    /// <summary>One line about the mod, for the loader's overlay.</summary>
    public string ModDescription
    {
        get => _modDescription;
        set => Set(ref _modDescription, value);
    }

    /// <summary>
    /// The loader's wider asset support, off by default.
    /// </summary>
    /// <remarks>
    /// Left alone when a mod already works without it, as it may cause crashes.
    /// So it is offered rather than chosen, and never set on somebody's behalf.
    /// </remarks>
    public bool PreloadCustomAssets
    {
        get => _preloadCustomAssets;
        set => Set(ref _preloadCustomAssets, value);
    }

    /// <summary>How many replacements are waiting to be written.</summary>
    public int ReplacingCount => _replacements.Count;

    public bool HasReplacements => _replacements.Count > 0;

    /// <summary>Remembers which archives the textures come from.</summary>
    public void UseArchives(string archiveRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = archiveRoot;
        _contentRoot = null;
        _paths = paths;
        _palette = default;
    }

    /// <summary>
    /// Shows the textures in a plain folder rather than in the archives.
    /// </summary>
    /// <remarks>
    /// Viewing only. Everything this pane writes — an edited texture, a mod,
    /// the check that a repointed path exists — needs the game's own file to
    /// compare against or to write beside, and a folder is neither. Those parts
    /// stay gated on an archive root, so choosing a folder shows the grid and
    /// leaves the rest as it was.
    /// </remarks>
    public void UseFolder(string contentRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _contentRoot = contentRoot;
        _archiveRoot = null;
        _paths = paths;
        _palette = default;
    }

    /// <summary>Where the grid reads from: the folder if there is one, else the archives.</summary>
    private (string? Content, string? Sdf) Reading => (_contentRoot, _archiveRoot);

    /// <summary>Whether anything at all can be read.</summary>
    private bool CanRead => _contentRoot is not null || _archiveRoot is not null;

    /// <summary>Remembers where the user's own work goes.</summary>
    public void UseWorkingFolder(string? folder) => _workingFolder = folder;

    /// <summary>Where a "save as PNG" should suggest writing.</summary>
    public string SuggestedPngPath()
    {
        string stem = _selected?.Name is string name && name.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : _selected?.Name ?? "texture";

        return _workingFolder is null
            ? stem + ".png"
            : Path.Combine(_workingFolder, stem + ".png");
    }

    /// <summary>Writes the selected texture out for editing.</summary>
    public async Task SaveAsPngAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // A read, so it works from a folder as well as the archives. Its
        // counterpart "Replace from PNG" does not, because taking an edit in
        // means writing a mod against the game's own file.
        if (_selected is not { } thumbnail || !CanRead)
        {
            return;
        }

        (string? Content, string? Sdf) root = Reading;
        string virtualPath = thumbnail.Path;

        Busy = true;
        Status = "Writing the PNG…";

        Result<int> written = await Task.Run(() => WritePng(root, virtualPath, path)).ConfigureAwait(true);

        Busy = false;
        Status = written.IsRefused
            ? written.Refusal.Message
            : $"Wrote {Path.GetFileName(path)}. Edit it, then use \"Replace from PNG\".";
    }

    /// <summary>
    /// Takes an edited PNG in against the selected texture.
    /// </summary>
    /// <remarks>
    /// Gathered rather than written. A person editing a character is usually
    /// changing several things, and five textures should become one mod to
    /// install, not five.
    /// </remarks>
    public async Task ReplaceFromPngAsync(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_selected is not { } thumbnail || _archiveRoot is null)
        {
            return;
        }

        string root = _archiveRoot;
        string virtualPath = thumbnail.Path;

        Busy = true;
        Status = "Converting…";

        Result<Converted> converted = await Task.Run(
            () => Convert(root, virtualPath, path)).ConfigureAwait(true);

        Busy = false;

        if (!converted.TryGetValue(out Converted made, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        if (!_replacements.ContainsKey(virtualPath))
        {
            // Named as a replacement, because that is what it is and what makes
            // it different from everything else in this list: it changes that
            // texture for every model in the game that binds it.
            Replacing.Add(new StagedFile(virtualPath, "replaces the game's own — for every model using it"));
        }

        _replacements[virtualPath] = made.Dds;

        // The warnings matter more than the count, so they lead.
        Status = made.Notes.Length > 0
            ? string.Join(" ", made.Notes.Select(note => note.Message))
            : $"{thumbnail.Name} replaced. {_replacements.Count} waiting — use \"Write mod\" when done.";

        Changed();
    }

    /// <summary>Writes everything gathered as one mod folder.</summary>
    public async Task WriteModAsync(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (_replacements.Count == 0)
        {
            return;
        }

        string name = _modName.Trim().Length > 0 ? _modName.Trim() : "Untitled mod";
        string author = _modAuthor.Trim().Length > 0 ? _modAuthor.Trim() : "unknown";

        List<ModFile> files =
            [.. Replacing.Select(staged => new ModFile(staged.Path, _replacements[staged.Path]))];

        Busy = true;
        Status = "Writing the mod…";

        string version = _modVersion.Trim().Length > 0 ? _modVersion.Trim() : "1.0.0";
        string description = _modDescription.Trim().Length > 0 ? _modDescription.Trim() : name;

        Result<ModOutcome> written = await Task.Run(
            () => TextureMod.Write(
                root,
                new ModDetails(name, author, version, description, _preloadCustomAssets),
                files))
            .ConfigureAwait(true);

        Busy = false;

        if (!written.TryGetValue(out ModOutcome? outcome, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {outcome.Files.Length} into {outcome.Folder}. Copy that folder into FractureLoader/Mods/.");

        Written();
    }

    /// <summary>
    /// Writes everything gathered as patches rather than as finished files.
    /// </summary>
    /// <remarks>
    /// The sharable form. A mod folder holds the game's own bytes; a patch
    /// holds only what the author changed, so it can be given away and applied
    /// against the recipient's own copy. Size is not a consideration — see
    /// <see cref="BytePatch"/>.
    /// </remarks>
    public async Task SavePatchesAsync(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        if (_replacements.Count == 0 || _archiveRoot is null)
        {
            return;
        }

        string root = _archiveRoot;
        List<(string Path, byte[] Bytes)> gathered =
            [.. Replacing.Select(staged => (staged.Path, _replacements[staged.Path]))];

        Busy = true;
        Status = "Writing patches…";

        Result<int> written = await Task.Run(
            () => WritePatches(root, folder, gathered)).ConfigureAwait(true);

        Busy = false;
        Status = written.TryGetValue(out int count, out Refusal? refusal)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {count} patches into {folder}. They carry only your changes, so nobody receives the game's own files.")
            : refusal.Message;
    }

    private static Result<int> WritePatches(
        string archiveRoot, string folder, List<(string Path, byte[] Bytes)> gathered)
    {
        using SdfContentSource source = new(archiveRoot);
        int written = 0;

        foreach ((string virtualPath, byte[] edited) in gathered)
        {
            Result<SdfContent> content = source.Read(virtualPath);
            if (!content.TryGetValue(out SdfContent original, out Refusal? refusal))
            {
                return refusal;
            }

            // A path the archives do not hold is a texture the author added, not
            // a missing original. Its patch carries the file whole, which is
            // theirs to give away — only the game's bytes may not travel.
            Result<byte[]> patch = original.IsPresent
                ? BytePatch.Make(original.Bytes.Span, edited, virtualPath)
                : BytePatch.MakeAddition(edited, virtualPath);
            if (!patch.TryGetValue(out byte[]? bytes, out Refusal? bad))
            {
                return bad;
            }

            string stem = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
            int dot = stem.LastIndexOf('.');
            string name = (dot > 0 ? stem[..dot] : stem) + ".perianthpatch";

            Result<int> published = AtomicFile.Publish(Path.Combine(folder, name), bytes);
            if (published.IsRefused)
            {
                return published.Refusal;
            }

            written++;
        }

        return Result.Ok(written);
    }

    /// <summary>
    /// How many parts the texture is painted onto, and how many of those take
    /// their colour from a tint instead of from the image.
    /// </summary>
    private (int Painted, int Tinted) Populations(string path)
    {
        if (_editordata is null)
        {
            return (0, 0);
        }

        string wanted = path.Replace('\\', '/').ToLowerInvariant();
        int painted = 0;
        int tinted = 0;

        foreach (MaterialBinding binding in MaterialEdit.Bindings(_editordata))
        {
            // Diffuse only, because this advises which operation to use and a
            // retint acts on what is drawn. tex_white16_d.dds is the blank sheet
            // for some parts and the placeholder in the other four channels of
            // nearly every part, so counting every channel would tell someone
            // that a whole model is theirs to recolour — Roadmap §6.14.
            if (!string.Equals(binding.Channel, "DiffuseColor", StringComparison.Ordinal) ||
                !string.Equals(
                    binding.Path.Replace('\\', '/').ToLowerInvariant(), wanted, StringComparison.Ordinal))
            {
                continue;
            }

            painted++;

            if (binding.Tint is Rgb tint && (tint.R != 1 || tint.G != 1 || tint.B != 1))
            {
                tinted++;
            }
        }

        return (painted, tinted);
    }

    private void Repoint()
    {
        if (_selected is null || _editordata is null || _editordataPath is null)
        {
            return;
        }

        if (MistypedParts())
        {
            return;
        }

        string target = RepointTo.Trim();

        Apply(MaterialEdit.Repoint(_editordata, _selected.Path, target, ParsedParts()), outcome =>
            $"Repointed to {target} in {outcome.Sections} parts.{Needed(target)}");
    }

    /// <summary>
    /// Puts an image in the mod under a path of its own, and points this
    /// model's parts at it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation most people want and the one that needed two halves done
    /// by hand: <see cref="ReplaceFromPngAsync"/> changes a texture at its own
    /// path, which changes it for <em>every</em> model binding it — often
    /// dozens, since the shipped art is a shared library of scanned paper. This
    /// adds a new texture instead and repoints only this model.
    /// </para>
    /// <para>
    /// Both halves use one string, so the path cannot be typed twice and
    /// differ — the mistake <c>ModCheck</c> otherwise catches after the fact.
    /// The path is proposed rather than imposed: whatever is in the repoint box
    /// wins, and the proposal is written back into it, so what was used is
    /// visible and can be redone differently.
    /// </para>
    /// </remarks>
    public async Task UseNewImageAsync(string png)
    {
        ArgumentNullException.ThrowIfNull(png);

        if (_editordata is null || _editordataPath is null || _archiveRoot is null)
        {
            return;
        }

        if (MistypedParts())
        {
            return;
        }

        List<int>? parts = ParsedParts();

        if (_selected is null && parts is not { Count: > 0 })
        {
            Status = "Choose a texture in the grid, or name the parts to change.";
            return;
        }

        string aim = Aim(parts);

        if (!TexturePath.Normalize(ChoosePath(parts), "DiffuseColor")
                .TryGetValue(out string? normalized, out Refusal? bad))
        {
            Status = bad.Message;
            return;
        }

        // A path the game already holds is a replacement wearing the clothes of
        // an addition: writing there changes that texture for every model, which
        // is the thing this operation exists to avoid.
        if (Ships(normalized))
        {
            Status = $"The game already has a texture at {normalized}. "
                + "Choose another path, or use \"Replace from PNG\" to change that one everywhere.";
            return;
        }

        // Refused rather than allowed to overwrite, because the parts already
        // pointed at this path would change to the new image too, and nothing
        // would say so. Redoing the *same* aim with a different image is not
        // this case and stays allowed: that is how you correct an image you
        // have just added.
        if (_addedFor.TryGetValue(normalized, out string? already) &&
            !string.Equals(already, aim, StringComparison.Ordinal))
        {
            Status = $"{normalized} already holds an image you added for other parts, and writing "
                + "another there would change those too. Clear the path box for a fresh path, or "
                + "type one of your own.";
            return;
        }

        Busy = true;
        Status = "Converting…";

        byte[] image;
        try
        {
            image = await File.ReadAllBytesAsync(png).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Busy = false;
            Status = $"'{png}' could not be read.";
            return;
        }

        Result<byte[]> converted = await Task.Run(
            () => TextureMod.Import(image, withMips: true)).ConfigureAwait(true);

        Busy = false;

        if (!converted.TryGetValue(out byte[]? dds, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        // Two operations, chosen by what the user actually said. With a texture
        // selected this moves that texture's bindings; with only parts named
        // there is nothing to move *from*, and the part's binding is replaced
        // whatever it was.
        Result<MaterialEditOutcome> repointed = _selected is { } thumbnail
            ? MaterialEdit.Repoint(_editordata, thumbnail.Path, normalized, parts)
            : MaterialEdit.Bind(_editordata, parts!, "DiffuseColor", normalized);

        if (repointed.IsRefused)
        {
            // Nothing has been staged yet, so a refusal here leaves the mod as
            // it was rather than carrying a texture nothing points at.
            Status = repointed.Refusal.Message;
            return;
        }

        if (!_replacements.ContainsKey(normalized))
        {
            Replacing.Add(new StagedFile(normalized, Purpose(parts)));
        }

        _replacements[normalized] = dds;
        _addedFor[normalized] = aim;

        RepointTo = normalized;
        _proposed = normalized;

        Apply(repointed, outcome =>
            $"Added {normalized} and pointed {outcome.Sections} "
            + $"{(outcome.Sections == 1 ? "part" : "parts")} of this model at it.");
    }

    /// <summary>
    /// What the next added image should be written to.
    /// </summary>
    /// <remarks>
    /// A path the user typed always wins — that is the promise the box makes.
    /// A path this pane proposed last time does not, because it was proposed
    /// for the selection that was current then, and reusing it is what made a
    /// second custom texture overwrite the first while silently repainting the
    /// part the first one was for.
    /// </remarks>
    internal string ChoosePath(IReadOnlyCollection<int>? parts)
    {
        string wanted = RepointTo.Trim();

        if (wanted.Length > 0 && !string.Equals(wanted, _proposed, StringComparison.Ordinal))
        {
            return wanted;
        }

        // Remembered here rather than by the caller, so that whatever proposes
        // is what knows it proposed. A typed path deliberately does not update
        // it: the user's own path is theirs to reuse.
        _proposed = MaterialEdit.ProposePath(
            _assets?.Name ?? string.Empty,
            _selected?.Path ?? "part.dds",
            parts);

        return _proposed;
    }

    /// <summary>What an addition is aimed at: the texture, and the parts.</summary>
    internal string Aim(IReadOnlyCollection<int>? parts) =>
        $"{_selected?.Path ?? string.Empty}|{string.Join(',', (parts ?? []).Order())}";

    /// <summary>What an addition was for, for the staged list to say.</summary>
    /// <remarks>
    /// The parts come first when there are any, because that is what
    /// distinguishes one addition from another. Naming the texture instead
    /// would give every addition aimed at one paper sheet the same description,
    /// which is the ambiguity this exists to remove.
    /// </remarks>
    internal string Purpose(IReadOnlyCollection<int>? parts)
    {
        if (parts is { Count: > 0 })
        {
            return parts.Count == 1
                ? $"new — for part {parts.First()}"
                : $"new — for parts {string.Join(", ", parts.Order())}";
        }

        return _selected is { } thumbnail
            ? $"new — for every part using {thumbnail.Name}"
            : "new";
    }

    /// <summary>
    /// Whether the parts box holds something that is not a list of numbers, and
    /// says so where it was typed.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the edit's own refusal. That one says
    /// an edit restricted to no sections would change nothing, which is true
    /// and unhelpful: the reason is a semicolon, and this is the only place
    /// that knows it.
    /// </remarks>
    private bool MistypedParts()
    {
        if (_parts.Trim().Length == 0 || ParsedParts() is { Count: > 0 })
        {
            return false;
        }

        Status = "The parts box wants part numbers separated by commas — or leave it empty to change "
            + "every part painted with this texture. A part's number is the N in mode2-record-N or "
            + "mode3-record-N. In Blender that is the name of the mesh, not of the object: select "
            + "the part and read Object Data Properties, the green triangle tab. A posed export "
            + "names the object after the character's own skeleton instead.";
        return true;
    }

    /// <summary>
    /// The section numbers typed, or null for every part.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty list, because the two mean opposite things to
    /// <see cref="MaterialEdit.Repoint"/>: no restriction at all, against a
    /// restriction that permits nothing. Anything unparseable yields an empty
    /// list, which that method refuses — better than quietly changing the whole
    /// model because a comma was a semicolon.
    /// </remarks>
    private List<int>? ParsedParts()
    {
        string typed = _parts.Trim();

        if (typed.Length == 0)
        {
            return null;
        }

        List<int> chosen = [];

        foreach (string piece in typed.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(piece.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal))
            {
                return [];
            }

            chosen.Add(ordinal);
        }

        return chosen;
    }

    private bool Ships(string path)
    {
        if (_archiveRoot is null)
        {
            return false;
        }

        using SdfContentSource source = new(_archiveRoot);
        return source.Read(path).TryGetValue(out SdfContent content, out _) && content.IsPresent;
    }

    /// <summary>
    /// Whether the mod will also have to carry the texture just repointed to.
    /// </summary>
    /// <remarks>
    /// Said at the moment the path is typed, which the window can do and the
    /// command line cannot: the archives are always open here. A path that
    /// differs from the intended one by a character binds a texture nothing
    /// provides, and the game reports that by drawing the wrong thing.
    /// </remarks>
    private string Needed(string target)
    {
        if (_archiveRoot is null)
        {
            return string.Empty;
        }

        using SdfContentSource source = new(_archiveRoot);

        return source.Read(target).TryGetValue(out SdfContent content, out _) && content.IsPresent
            ? " The game ships that texture."
            : " Nothing in the game has that path, so put your own texture there before writing the mod.";
    }

    private void Retint()
    {
        if (_selected is null || _editordata is null || _editordataPath is null)
        {
            return;
        }

        string[] parts = RetintTo.Split(',');
        double[] channels = new double[3];

        if (parts.Length != 3)
        {
            Status = "A colour is three numbers as R,G,B — try 0.1,0.2,0.8.";
            return;
        }

        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(
                    parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out channels[i]))
            {
                Status = $"'{parts[i].Trim()}' is not a number.";
                return;
            }
        }

        Apply(
            MaterialEdit.Retint(_editordata, _selected.Path, null, new Rgb(channels[0], channels[1], channels[2])),
            outcome => $"Recoloured {outcome.Sections} parts.");
    }

    /// <summary>
    /// Takes an edit, and puts the rewritten file into the same mod everything
    /// else is waiting in.
    /// </summary>
    /// <remarks>
    /// Deliberately the same list as an edited texture. Someone changing a
    /// character usually changes several things, and a material edit is one of
    /// them rather than a separate kind of output — so it shares "Write mod",
    /// "Save as patches" and "Discard" without any of them learning a new case.
    /// </remarks>
    /// <summary>The model as edited so far, or null when none is loaded.</summary>
    /// <remarks>
    /// A seam. Loading reads an archive, so the accumulation of edits across a
    /// write cannot otherwise be checked without one.
    /// </remarks>
    internal EditordataFile? Current => _editordata;

    /// <summary>Takes a loaded model, as <see cref="ReloadAsync"/> does.</summary>
    internal void Load(EditordataFile file, string path)
    {
        _editordata = file;
        _pristine = file;
        _editordataPath = path;
    }

    internal void Apply(Result<MaterialEditOutcome> edit, Func<MaterialEditOutcome, string> describe)
    {
        if (!edit.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        Result<byte[]> written = EditordataWriter.Write(outcome.File);
        if (!written.TryGetValue(out byte[]? bytes, out Refusal? unwritable))
        {
            Status = unwritable.Message;
            return;
        }

        // Kept, so a second edit builds on the first rather than starting over
        // from what the archives hold.
        _editordata = outcome.File;

        if (!_replacements.ContainsKey(_editordataPath!))
        {
            // One entry however many edits it accumulates: it is a single file
            // rewritten each time, not one per change.
            Replacing.Add(new StagedFile(_editordataPath!, "this model's materials — what each part is painted with"));
        }

        _replacements[_editordataPath!] = bytes;

        Status = describe(outcome) + $" {_replacements.Count} waiting — use \"Write mod\" when done.";
        Raise(nameof(SelectedAdvice));
        Changed();
    }

    /// <summary>
    /// Clears what was staged, keeping the material edits it carried.
    /// </summary>
    /// <remarks>
    /// Writing a mod is not discarding one, and using <see cref="Forget"/> for
    /// both made adding a second custom texture silently impossible. The edits
    /// accumulate in <c>_editordata</c> so that a second one builds on the
    /// first; resetting to the archives' copy after a write meant the next edit
    /// started from there, and the next write replaced the mod's editordata with
    /// one that had never carried the first repoint. Both images sit in the
    /// folder, one is bound, and nothing says so.
    /// </remarks>
    internal void Written()
    {
        _replacements.Clear();
        Replacing.Clear();

        Raise(nameof(SelectedAdvice));
        Changed();
    }

    internal void Forget()
    {
        _replacements.Clear();
        Replacing.Clear();

        // Discarding must undo the material edits too, or the next one builds
        // on changes the user has just thrown away and the advice line keeps
        // describing a model that is no longer what the archives hold.
        _editordata = _pristine;

        // And what was added for what, which described those edits.
        _addedFor.Clear();
        _proposed = string.Empty;
        RepointTo = string.Empty;

        Raise(nameof(SelectedAdvice));
        Changed();
    }

    private static Result<int> WritePng(
        (string? Content, string? Sdf) root, string virtualPath, string output)
    {
        using ContentSources source = new(root.Content, root.Sdf);

        Result<byte[]?> content = source.Read(virtualPath);
        if (!content.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Resource(
                "This texture is not in the folder or the archives.", DiagnosticIds.ResourceMissing);
        }

        Result<DdsImage> read = DdsReader.Read(bytes);
        if (!read.TryGetValue(out DdsImage? image, out Refusal? bad))
        {
            return bad;
        }

        return AtomicFile.Publish(
            output,
            PngEncoder.Encode(new RgbaImage(image.Width, image.Height, image.Pixels.ToArray())));
    }

    private static Result<Converted> Convert(string archiveRoot, string virtualPath, string png)
    {
        byte[] image;
        try
        {
            image = File.ReadAllBytes(png);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{png}' could not be read.", DiagnosticIds.ResourceMissing);
        }

        Result<byte[]> converted = TextureMod.Import(image, withMips: true);
        if (!converted.TryGetValue(out byte[]? dds, out Refusal? refusal))
        {
            return refusal;
        }

        // Compared against what the archives hold, which is the original by
        // definition here — there is no extracted copy to have drifted from it.
        using SdfContentSource source = new(archiveRoot);
        Result<SdfContent> original = source.Read(virtualPath);

        ImmutableArray<Diagnostic> notes = original.TryGetValue(out SdfContent was, out _) && was.IsPresent
            ? TextureMod.Compare(dds, was.Bytes.Span)
            : [];

        return Result.Ok(new Converted(dds, notes));
    }

    private readonly record struct Converted(byte[] Dds, ImmutableArray<Diagnostic> Notes);

    /// <summary>
    /// Takes the model the middle pane resolved, without decoding anything yet.
    /// </summary>
    /// <remarks>
    /// Choosing a file in the left-hand pane must stay as quick as it is. The
    /// decode waits until the tab is looked at, which for most exports is never.
    /// </remarks>
    public void Show(CharacterAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        _pending?.Cancel();
        _assets = assets;
        _editordata = null;
        _pristine = null;
        _editordataPath = null;
        _loaded = false;
        _listed.Clear();
        Status = IsShowing ? "Reading the materials…" : "Open this tab to decode the textures.";
        Changed();

        // Fire and forget deliberately: this is reached from a selection
        // changing, and the whole point is to let that finish first. The reload
        // follows only when the tab is on screen — otherwise it waits for the
        // tab to be opened, which is what keeps choosing a file cheap.
        _ = ShowAsync();
    }

    /// <summary>
    /// Empties the grid by replacing it, not by clearing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A crash fixed twice, the first time wrongly, so the reasoning is worth
    /// keeping. Choosing another model with the Textures tab open threw
    /// <c>"Source collection was modified during selection update"</c>:
    /// <see cref="Show"/> empties the grid and the tab handler's reload empties
    /// it again, and Avalonia's selection model is still working through the
    /// first change when the second arrives.
    /// </para>
    /// <para>
    /// The first attempt set the selection to null before clearing, which made
    /// it worse — assigning the selection is itself what <em>starts</em> an
    /// update, so the clear then landed squarely inside one.
    /// </para>
    /// <para>
    /// The second attempt swapped the collection instead of emptying it, which
    /// is right and was not enough: it raised no change of its own, but the
    /// control still had to adopt a new source while its selection model was
    /// mid-update, and that throws too. It also left the selection set on the
    /// view while the field said null, so the two disagreed.
    /// </para>
    /// <para>
    /// What settles it is doing both, in order, and <strong>not from inside the
    /// framework's own event</strong>: the collection is replaced first so the
    /// list adopts an empty source, then the selection is raised as null so the
    /// view and the field agree. Callers reach this through
    /// <see cref="ResetThumbnailsAsync"/>, which waits for the current update to
    /// finish before touching anything bound. Reasoning about when Avalonia is
    /// mid-update is what failed twice; standing aside until it is not cannot.
    /// </para>
    /// <para>
    /// Replacing rather than emptying also makes a late decode harmless: a batch
    /// that finishes after the model changed appends to a collection nothing is
    /// bound to.
    /// </para>
    /// </remarks>
    private Task ResetThumbnailsAsync() =>
        Dispatcher.UIThread.InvokeAsync(ResetThumbnails, DispatcherPriority.Background).GetTask();

    private void ResetThumbnails()
    {
        _thumbnails = [];
        Raise(nameof(Thumbnails));

        // After the source, not before: assigning the selection is itself what
        // starts an update, and doing it first put the swap inside one.
        _selected = null;
        Raise(nameof(Selected));
        Raise(nameof(SelectedName));
        Raise(nameof(SelectedAdvice));

        SaveAsPngCommand.Reconsider();
        ReplaceCommand.Reconsider();
        RepointCommand.Reconsider();
        RetintCommand.Reconsider();
        AddCommand.Reconsider();
    }

    /// <summary>
    /// Whether the tab is on screen, which decides when a new model is decoded.
    /// </summary>
    /// <remarks>
    /// Which tab is showing is the window's business, so the window sets this.
    /// Without it, choosing another model while the tab was already open left
    /// the old grid in place until the tabs were toggled: the decode is
    /// deferred to the tab being opened, and it was already open.
    /// </remarks>
    public bool IsShowing { get; set; }

    /// <summary>Decodes and shows them, once, when the tab is first opened.</summary>
    public Task OpenedAsync() => _loaded ? Task.CompletedTask : Superseded(ReloadAsync());

    /// <summary>
    /// Writes everything staged into a tree laid out with the game's own paths.
    /// </summary>
    /// <remarks>
    /// So an export can see the edits before the game does. The export pane
    /// extracts a model's files into a working folder and resolves textures
    /// against it, and that folder is the same shape as a mod — the archive's
    /// own paths — so laying the staged files over it is the whole of it. The
    /// edited editordata lands on the extracted one and its materials are used
    /// too, not only the images.
    /// </remarks>
    /// <summary>How many edits are waiting, for another pane to describe.</summary>
    public int Staged => _replacements.Count;

    public Result<int> OverlayInto(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        int written = 0;

        foreach (string virtualPath in Replacing.Select(staged => staged.Path))
        {
            string destination = Path.Combine(
                root, virtualPath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refusal.Resource(
                    $"'{destination}' could not be created.", DiagnosticIds.ResourceMissing);
            }

            Result<int> published = AtomicFile.Publish(destination, _replacements[virtualPath]);
            if (published.IsRefused)
            {
                return published.Refusal;
            }

            written++;
        }

        return Result.Ok(written);
    }

    private async Task ShowAsync()
    {
        await ResetThumbnailsAsync().ConfigureAwait(true);

        if (IsShowing)
        {
            await Superseded(ReloadAsync()).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Awaits work that a later selection may have cancelled, and treats that
    /// as the ordinary outcome it is.
    /// </summary>
    /// <remarks>
    /// Choosing another model cancels the decode of the one before, and a
    /// cancelled <see cref="Task.Run(Action, CancellationToken)"/> completes by
    /// throwing. Both callers are reached from event handlers that return void,
    /// so an uncaught one ends the process — which is what selecting two models
    /// quickly did. Cancellation here is expected, not a fault, and this is the
    /// project's rule that a refusal is a value rather than an exception
    /// applied to the one case where the framework insists otherwise.
    /// </remarks>
    internal static async Task Superseded(Task work)
    {
        try
        {
            await work.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync()
    {
        if (_assets?.Editordata is null || !CanRead)
        {
            return;
        }

        _pending?.Cancel();
        CancellationTokenSource mine = new();
        _pending = mine;

        _loaded = true;
        await ResetThumbnailsAsync().ConfigureAwait(true);
        Busy = true;
        Status = "Reading the materials…";
        Changed();

        (string? Content, string? Sdf) root = Reading;
        string editordata = _assets.Editordata;
        string name = _assets.Name;

        Result<Listing> listed = await Task.Run(
            () => List(root, editordata, name), mine.Token).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        // Read once per source rather than per model: the palette is the game's,
        // not this model's, and it does not change while a folder stays open. A
        // source that has none — a mod folder holding only textures — simply
        // offers no colours, which is not a failure worth reporting.
        if (_palette.IsDefault)
        {
            ImmutableArray<SdfPathEntry> paths = _paths;
            _palette = await Task.Run(
                () =>
                {
                    using ContentSources sources = new(root.Content, root.Sdf);
                    Result<ImmutableArray<PaperSwatch>> read = PaperPalette.Read(sources, paths);
                    return read.TryGetValue(out ImmutableArray<PaperSwatch> palette, out _)
                        ? palette
                        : [];
                }, mine.Token).ConfigureAwait(true);
        }

        if (!listed.TryGetValue(out Listing listing, out Refusal? refusal))
        {
            Busy = false;
            Status = refusal.Message;
            return;
        }

        _editordata = listing.File;
        _pristine = listing.File;
        _editordataPath = editordata;
        _listed.Clear();
        _listed.AddRange(listing.Textures);

        await DecodeAsync(FirstScreenful, mine).ConfigureAwait(true);
    }

    private void ShowAll() =>
        _ = Superseded(DecodeAsync(Filtered().Count, _pending ?? new CancellationTokenSource()));

    /// <summary>
    /// Decodes up to <paramref name="count"/> more, on top of what is shown.
    /// </summary>
    /// <remarks>
    /// Counted rather than clamped at the caller: asking for "the rest" with
    /// <see cref="int.MaxValue"/> overflowed the addition below to a negative
    /// count, and the range it then asked for threw into a discarded task, so
    /// the button did nothing and said nothing.
    /// </remarks>
    private async Task DecodeAsync(int count, CancellationTokenSource mine)
    {
        if (!CanRead || count <= 0)
        {
            return;
        }

        List<TextureReference> selected = Filtered();
        int wanted = Math.Min(selected.Count, Thumbnails.Count + Math.Min(count, selected.Count));
        (string? Content, string? Sdf) root = Reading;

        List<TextureReference> batch = selected.GetRange(
            Thumbnails.Count, wanted - Thumbnails.Count);

        Busy = true;
        Status = "Decoding…";
        Changed();

        // One batch on one background thread, holding the archives open across
        // all of it. Opening a content source per texture would re-read the
        // table of contents eighty times to save nothing.
        ObservableCollection<TextureThumbnail> into = _thumbnails;

        List<TextureThumbnail> decoded = await Task.Run(
            () => Decode(root, batch), mine.Token).ConfigureAwait(true);

        // Both checks, because they catch different races: the token covers a
        // cancelled reload, and the identity covers one whose replacement grid
        // is already on screen. Appending to the wrong one would show a model's
        // textures under another model's name.
        if (mine.IsCancellationRequested || !ReferenceEquals(into, _thumbnails))
        {
            return;
        }

        foreach (TextureThumbnail thumbnail in decoded)
        {
            into.Add(thumbnail);
        }

        Busy = false;
        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"{Thumbnails.Count} of {selected.Count} textures, from {_listed.Count} the materials bind.");

        Changed();
    }

    /// <summary>The textures the current filter admits.</summary>
    private List<TextureReference> Filtered()
    {
        if (_all)
        {
            return _listed;
        }

        List<TextureReference> pictures = [];
        foreach (TextureReference reference in _listed)
        {
            if (reference.Own || string.Equals(reference.Channel, "DiffuseColor", StringComparison.Ordinal))
            {
                pictures.Add(reference);
            }
        }

        return pictures;
    }

    /// <summary>The listing and the file it came from, kept together.</summary>
    /// <remarks>
    /// The file used to be discarded once the textures were listed. Editing
    /// needs it: a repoint rewrites the same records the listing was read from,
    /// and re-reading it later would be a second decode of bytes already held.
    /// </remarks>
    private readonly record struct Listing(
        ImmutableArray<TextureReference> Textures, EditordataFile File);

    private static Result<Listing> List(
        (string? Content, string? Sdf) root, string editordata, string name)
    {
        using ContentSources source = new(root.Content, root.Sdf);

        Result<byte[]?> content = source.Read(editordata);
        if (!content.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Resource(
                "This model's editordata is not in the folder or the archives.", DiagnosticIds.ResourceMissing);
        }

        Result<EditordataFile> read = EditordataReader.Read(
            SourceFile.FromMemory(editordata, bytes));

        return read.TryGetValue(out EditordataFile? file, out Refusal? bad)
            ? Result.Ok(new Listing(MaterialTextures.List(file, name), file))
            : bad;
    }

    private static List<TextureThumbnail> Decode(
        (string? Content, string? Sdf) root, List<TextureReference> batch)
    {
        using ContentSources source = new(root.Content, root.Sdf);

        List<TextureThumbnail> decoded = new(batch.Count);
        foreach (TextureReference reference in batch)
        {
            decoded.Add(Decode(source, reference));
        }

        return decoded;
    }

    private static TextureThumbnail Decode(ContentSources source, TextureReference reference)
    {
        string stem = reference.Path[(reference.Path.LastIndexOf('/') + 1)..];

        Result<byte[]?> content = source.Read(reference.Path);
        if (!content.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return Missing(stem, reference, refusal.Message);
        }

        if (bytes is null)
        {
            return Missing(stem, reference, "This texture is not in the folder or the archives.");
        }

        Result<DdsImage> read = DdsReader.Read(bytes);
        if (!read.TryGetValue(out DdsImage? image, out Refusal? bad))
        {
            return Missing(stem, reference, bad.Message);
        }

        return new TextureThumbnail(
            stem,
            reference.Path,
            Detail(reference, image.Width, image.Height),
            ToBitmap(image),
            string.Empty);
    }

    private static TextureThumbnail Missing(string stem, TextureReference reference, string note) =>
        new(stem, reference.Path, reference.Channel, null, note);

    private static string Detail(TextureReference reference, int width, int height) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{reference.Channel}  {width}×{height}  ×{reference.Bindings}");

    /// <summary>
    /// Turns a decoded image into one Avalonia can draw.
    /// </summary>
    /// <remarks>
    /// The decoder gives straight RGBA and Avalonia wants BGRA, so the two
    /// colour bytes are swapped on the way in. Alpha is left unpremultiplied so
    /// that a mask drawn over the checkerboard shows its own shape rather than a
    /// black one.
    /// </remarks>
    private static Bitmap ToBitmap(DdsImage image)
    {
        ReadOnlySpan<byte> pixels = image.Pixels;
        byte[] bgra = new byte[pixels.Length];

        for (int at = 0; at < pixels.Length; at += 4)
        {
            bgra[at] = pixels[at + 2];
            bgra[at + 1] = pixels[at + 1];
            bgra[at + 2] = pixels[at];
            bgra[at + 3] = pixels[at + 3];
        }

        GCHandle handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);

        try
        {
            return new Bitmap(
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(),
                new PixelSize(image.Width, image.Height),
                new Vector(96, 96),
                image.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private void Changed()
    {
        Raise(nameof(More));
        Raise(nameof(HasMore));
        Raise(nameof(ShowAllText));
        Raise(nameof(ReplacingCount));
        Raise(nameof(HasReplacements));
        ShowAllCommand.Reconsider();
        WriteModCommand.Reconsider();
        SavePatchesCommand.Reconsider();
        ForgetCommand.Reconsider();
        RepointCommand.Reconsider();
        RetintCommand.Reconsider();
        AddCommand.Reconsider();
    }
}
