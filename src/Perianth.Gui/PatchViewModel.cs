using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Gui;

/// <summary>One patch that has been opened, and whether it can be applied.</summary>
/// <param name="Name">The patch file's own name, for reading.</param>
/// <param name="File">
/// Its full path, which is what identifies it. Patches are written under the
/// mod's folder structure and a mod holds files sharing a name in different
/// folders, so the name alone names more than one thing.
/// </param>
/// <param name="VirtualPath">The archive path it replaces.</param>
/// <param name="Detail">What it says about itself, or why it cannot be used.</param>
/// <param name="Ready">Whether the original it wants was found in the archives.</param>
public sealed record PatchRow(string Name, string File, string VirtualPath, string Detail, bool Ready);

/// <summary>
/// Applying patches somebody else made.
/// </summary>
/// <remarks>
/// <para>
/// The receiving half of sharing a mod. A patch carries only its author's
/// changes, so applying one needs the original from the recipient's own copy of
/// the game — which this window already has open, so unlike the command line it
/// can find each original itself rather than asking for files extracted first.
/// </para>
/// <para>
/// Its own window because it is its own task. Nothing here depends on which
/// model is selected, or on anything else the main window is showing.
/// </para>
/// </remarks>
public sealed class PatchViewModel : ViewModelBase
{
    /// <summary>Why a file the archives do not hold produced no patch.</summary>
    /// <remarks>
    /// A constant because the status line counts these apart from the others,
    /// and a message compared by its prefix should not be able to drift from
    /// the one being written.
    /// </remarks>
    private const string NotInArchives = "identical to the game's own file, so there is nothing to patch";

    /// <summary>How a patch carrying a whole file of the author's own is labelled.</summary>
    private const string AddedWhole = "added whole, being your own file";

    private readonly Dictionary<string, byte[]> _patches = new(StringComparer.Ordinal);
    private string? _archiveRoot;
    private string _status = "Open the patches somebody sent you.";
    private string _modName = string.Empty;
    private string _modAuthor = string.Empty;
    private string _modVersion = "1.0.0";
    private string _modDescription = string.Empty;
    private bool _preloadCustomAssets;
    private bool _busy;

    public PatchViewModel()
    {
        OpenCommand = new RelayCommand(() => OpenRequested?.Invoke(), () => _archiveRoot is not null);
        OpenFilesCommand = new RelayCommand(() => OpenFilesRequested?.Invoke(), () => _archiveRoot is not null);
        MakeCommand = new RelayCommand(() => MakeRequested?.Invoke(), () => _archiveRoot is not null);
        WriteModCommand = new RelayCommand(() => WriteRequested?.Invoke(), () => Ready > 0);
        ClearCommand = new RelayCommand(Clear, () => Rows.Count > 0);
    }

    /// <summary>The patches opened so far, in the order they were opened.</summary>
    public ObservableCollection<PatchRow> Rows { get; } = [];

    /// <summary>The patches made from a mod folder.</summary>
    public ObservableCollection<PatchRow> Made { get; } = [];

    /// <summary>
    /// The files no patch was made for, and why.
    /// </summary>
    /// <remarks>
    /// Kept apart from the successes because the reasons are not variations of
    /// one thing. Measured on a real mod: 27 files were byte-identical to the
    /// game's own, which is harmless, and 22 were not found in the archives at
    /// all, which needs acting on — no patch can be made without an original to
    /// differ from, so those have to travel some other way.
    /// </remarks>
    public ObservableCollection<PatchRow> Skipped { get; } = [];

    public bool HasMade => Made.Count > 0;

    public bool HasSkipped => Skipped.Count > 0;

    private string _makeStatus = "Point at a mod folder — any folder holding the game's own paths.";

    /// <summary>What the making half is saying.</summary>
    public string MakeStatus
    {
        get => _makeStatus;
        private set => Set(ref _makeStatus, value);
    }

    public RelayCommand OpenCommand { get; }

    /// <summary>Makes patches out of an already-edited mod folder.</summary>
    public RelayCommand MakeCommand { get; }

    /// <summary>Opens individual patch files, for when there are only a few.</summary>
    public RelayCommand OpenFilesCommand { get; }

    public RelayCommand WriteModCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Raised when a file dialog is needed, which only a window can raise.</summary>
    public event Action? OpenRequested;

    public event Action? MakeRequested;

    public event Action? OpenFilesRequested;

    public event Action? WriteRequested;

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

    /// <summary>The mod's name, which is also its folder.</summary>
    public string ModName
    {
        get => _modName;
        set => Set(ref _modName, value);
    }

    /// <summary>Whoever is installing it, for the loader's overlay.</summary>
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

    /// <summary>How many opened patches have their original to hand.</summary>
    public int Ready
    {
        get
        {
            int ready = 0;
            foreach (PatchRow row in Rows)
            {
                if (row.Ready)
                {
                    ready++;
                }
            }

            return ready;
        }
    }

    public bool HasRows => Rows.Count > 0;

    /// <summary>Remembers which archives the originals come from.</summary>
    public void UseArchives(string archiveRoot)
    {
        _archiveRoot = archiveRoot;
        Status = "Open the patches somebody sent you.";
        OpenCommand.Reconsider();
        OpenFilesCommand.Reconsider();
        MakeCommand.Reconsider();
    }

    /// <summary>
    /// Makes a patch for every file in a mod folder that differs from the
    /// game's own.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mod folder mirrors the archive's paths, so a file's position in it
    /// <em>is</em> its archive path. Nothing is inferred: a file counts only if
    /// that exact path is in the archives, which is a lookup rather than a
    /// guess, and it is skipped with a reason otherwise.
    /// </para>
    /// <para>
    /// This exists because the other way into patches — edit a texture here,
    /// then save the result — only reaches work done in this window. Somebody
    /// with a finished mod, made however they made it, has the case that
    /// matters most and had no way in at all.
    /// </para>
    /// </remarks>
    public async Task MakeFromFolderAsync(string modFolder, string destination)
    {
        ArgumentNullException.ThrowIfNull(modFolder);
        ArgumentNullException.ThrowIfNull(destination);

        if (_archiveRoot is null)
        {
            return;
        }

        string archives = _archiveRoot;

        Busy = true;
        MakeStatus = "Comparing against the archives…";
        Made.Clear();
        Skipped.Clear();

        List<PatchRow> rows = await Task.Run(
            () => MakePatches(archives, modFolder, destination)).ConfigureAwait(true);

        int additions = 0;
        foreach (PatchRow row in rows)
        {
            if (row.Ready)
            {
                Made.Add(row);

                if (row.Detail.StartsWith(AddedWhole, StringComparison.Ordinal))
                {
                    additions++;
                }

                continue;
            }

            Skipped.Add(row);
        }

        Busy = false;
        Raise(nameof(HasMade));
        Raise(nameof(HasSkipped));

        // Said because the two kinds carry different things, and somebody about
        // to share these should know which of them hold a whole file.
        string added = additions > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $" {additions} of them add a file the game does not ship, carried whole — those are yours to give away; only the game's own bytes are not.")
            : string.Empty;

        MakeStatus = Made.Count > 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {Made.Count} patches into {destination}.{(Skipped.Count > 0 ? $" {Skipped.Count} files made no patch." : string.Empty)}{added}")
            : rows.Count == 0
                ? "That folder holds no files to compare."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"None of the {rows.Count} files here produced a patch. Each says why below; if they all say the same thing, that is the thing to fix.");
    }

    private static List<PatchRow> MakePatches(string archiveRoot, string modFolder, string destination)
    {
        using SdfContentSource source = new(archiveRoot);
        List<PatchRow> rows = [];

        try
        {
            // Once, before anything is compared. Left to the writer it failed
            // per file, and every failure then read as "this file is not in the
            // archives" -- blaming the mod for a missing output directory.
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new PatchRow(destination, destination, string.Empty, "could not be created", Ready: false)];
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(modFolder, "*", SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new PatchRow(modFolder, modFolder, string.Empty, "could not be read", Ready: false)];
        }

        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(modFolder, file).Replace('\\', '/');
            string name = Path.GetFileName(file);

            // The loader's own manifest is not game content and has no original.
            if (relative.Equals("manifest.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A file the archives do not hold is the mod's own, and a patch can
            // still carry it: those bytes belong to whoever made them, and only
            // the game's may not travel. It was previously skipped, which meant
            // a mod that adds art could not be shared as one set of patches.
            Result<SdfContent> original = source.Read(relative);
            bool addition = !original.TryGetValue(out SdfContent content, out _) || !content.IsPresent;

            byte[] edited;
            try
            {
                edited = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rows.Add(new PatchRow(name, file, relative, "could not be read", Ready: false));
                continue;
            }

            Result<byte[]> patch = addition
                ? BytePatch.MakeAddition(edited, relative)
                : BytePatch.Make(content.Bytes.Span, edited, relative);
            if (!patch.TryGetValue(out byte[]? bytes, out Refusal? refusal))
            {
                rows.Add(new PatchRow(name, file, relative, refusal.Message, Ready: false));
                continue;
            }

            // Written under the mod's own folder structure, not by name alone.
            // A mod holds files that share a name in different folders -- one
            // measured mod has 13 such pairs -- and naming a patch by its stem
            // silently overwrote one with the other.
            string output = Path.Combine(
                destination, relative.Replace('/', Path.DirectorySeparatorChar) + ".perianthpatch");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rows.Add(new PatchRow(name, file, relative, "its folder could not be created", Ready: false));
                continue;
            }

            Result<int> published = AtomicFile.Publish(output, bytes);

            rows.Add(published.IsRefused
                ? new PatchRow(name, file, relative, published.Refusal.Message, Ready: false)
                : new PatchRow(
                    name,
                    file,
                    relative,
                    // Which kind, because they carry different things: a patch
                    // against a shipped file holds only the difference, and one
                    // for a file of the author's own holds all of it.
                    addition
                        ? string.Create(CultureInfo.InvariantCulture, $"{AddedWhole} — {bytes.Length} bytes")
                        : string.Create(CultureInfo.InvariantCulture, $"patched — {bytes.Length} bytes"),
                    Ready: true));
        }

        return rows;
    }

    /// <summary>
    /// Opens patches and says, for each, whether it can be applied.
    /// </summary>
    /// <remarks>
    /// Everything is checked before anything is written, because a person
    /// holding five patches wants to know which of them will work, not to
    /// discover it one refusal at a time.
    /// </remarks>
    public async Task OpenFolderAsync(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);

        string[] found;
        try
        {
            // Recursive, because patches are written under the mod's own folder
            // structure — which is exactly why picking them one by one is the
            // chore this replaces.
            found = Directory.GetFiles(folder, "*.perianthpatch", SearchOption.AllDirectories);
            Array.Sort(found, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"'{folder}' could not be read.";
            return;
        }

        if (found.Length == 0)
        {
            Status = $"No .perianthpatch files under '{folder}'.";
            return;
        }

        await OpenAsync(found).ConfigureAwait(true);
    }

    public async Task OpenAsync(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (_archiveRoot is null || files.Count == 0)
        {
            return;
        }

        string root = _archiveRoot;

        Busy = true;
        Status = "Reading…";

        List<PatchRow> opened = await Task.Run(() => Open(root, files, _patches)).ConfigureAwait(true);

        foreach (PatchRow row in opened)
        {
            Rows.Add(row);
        }

        Busy = false;
        Changed();

        int ready = Ready;
        Status = ready == Rows.Count
            ? string.Create(CultureInfo.InvariantCulture, $"{ready} patches, all ready to apply.")
            : string.Create(CultureInfo.InvariantCulture, $"{ready} of {Rows.Count} can be applied; the rest are listed below.");
    }

    /// <summary>Applies every ready patch into one mod folder.</summary>
    public async Task WriteModAsync(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (_archiveRoot is null || Ready == 0)
        {
            return;
        }

        string archives = _archiveRoot;
        string name = _modName.Trim().Length > 0 ? _modName.Trim() : "Applied patches";
        string author = _modAuthor.Trim().Length > 0 ? _modAuthor.Trim() : "unknown";

        List<(string File, string Path)> ready = [];
        foreach (PatchRow row in Rows)
        {
            if (row.Ready)
            {
                ready.Add((row.File, row.VirtualPath));
            }
        }

        Busy = true;
        Status = "Applying…";

        string version = _modVersion.Trim().Length > 0 ? _modVersion.Trim() : "1.0.0";
        string description = _modDescription.Trim().Length > 0 ? _modDescription.Trim() : name;
        ModDetails details = new(name, author, version, description, _preloadCustomAssets);

        Result<ModOutcome> written = await Task.Run(
            () => Apply(archives, root, details, ready, _patches)).ConfigureAwait(true);

        Busy = false;
        Status = written.TryGetValue(out ModOutcome? outcome, out Refusal? refusal)
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {outcome.Files.Length} into {outcome.Folder}. Copy that folder into FractureLoader/Mods/.")
            : refusal.Message;
    }

    private static List<PatchRow> Open(
        string archiveRoot, IReadOnlyList<string> files, Dictionary<string, byte[]> keep)
    {
        using SdfContentSource source = new(archiveRoot);
        List<PatchRow> rows = [];

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rows.Add(new PatchRow(name, file, string.Empty, "could not be read", Ready: false));
                continue;
            }

            Result<PatchHeader> described = BytePatch.Describe(bytes);
            if (!described.TryGetValue(out PatchHeader? header, out Refusal? refusal))
            {
                rows.Add(new PatchRow(name, file, string.Empty, refusal.Message, Ready: false));
                continue;
            }

            // A patch that adds a file the game never had needs no original, so
            // the archives are not consulted for it and its absence there is
            // not a problem to report.
            ReadOnlyMemory<byte> against = ReadOnlyMemory<byte>.Empty;

            if (!header.IsNewFile)
            {
                Result<SdfContent> original = source.Read(header.VirtualPath);
                if (!original.TryGetValue(out SdfContent content, out _) || !content.IsPresent)
                {
                    rows.Add(new PatchRow(
                        name,
                        file,
                        header.VirtualPath,
                        "the archives do not hold the file this patch is for",
                        Ready: false));
                    continue;
                }

                against = content.Bytes;
            }

            // Checked now rather than at write time: this is the answer the
            // person wants before they commit to anything.
            Result<byte[]> applied = BytePatch.Apply(bytes, against.Span);
            if (applied.IsRefused)
            {
                rows.Add(new PatchRow(name, file, header.VirtualPath, applied.Refusal.Message, Ready: false));
                continue;
            }

            keep[file] = bytes;
            rows.Add(new PatchRow(
                name,
                file,
                header.VirtualPath,
                string.Create(CultureInfo.InvariantCulture, $"ready — produces {header.ResultLength} bytes"),
                Ready: true));
        }

        return rows;
    }

    private static Result<ModOutcome> Apply(
        string archiveRoot,
        string root,
        ModDetails details,
        List<(string File, string Path)> ready,
        Dictionary<string, byte[]> patches)
    {
        using SdfContentSource source = new(archiveRoot);
        ImmutableArray<ModFile>.Builder files = ImmutableArray.CreateBuilder<ModFile>(ready.Count);

        foreach ((string file, string virtualPath) in ready)
        {
            Result<PatchHeader> described = BytePatch.Describe(patches[file]);
            if (!described.TryGetValue(out PatchHeader? header, out Refusal? unreadable))
            {
                return unreadable;
            }

            ReadOnlyMemory<byte> against = ReadOnlyMemory<byte>.Empty;

            if (!header.IsNewFile)
            {
                Result<SdfContent> original = source.Read(virtualPath);
                if (!original.TryGetValue(out SdfContent content, out Refusal? refusal))
                {
                    return refusal;
                }

                against = content.Bytes;
            }

            Result<byte[]> applied = BytePatch.Apply(patches[file], against.Span);
            if (!applied.TryGetValue(out byte[]? result, out Refusal? bad))
            {
                return bad;
            }

            files.Add(new ModFile(virtualPath, result));
        }

        return TextureMod.Write(root, details, files.ToImmutable());
    }

    private void Clear()
    {
        Rows.Clear();
        Made.Clear();
        Skipped.Clear();
        Raise(nameof(HasMade));
        Raise(nameof(HasSkipped));
        _patches.Clear();
        Status = "Open the patches somebody sent you.";
        Changed();
    }

    private void Changed()
    {
        Raise(nameof(Ready));
        Raise(nameof(HasRows));
        WriteModCommand.Reconsider();
        ClearCommand.Reconsider();
    }
}
