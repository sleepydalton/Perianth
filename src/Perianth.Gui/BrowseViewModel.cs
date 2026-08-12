using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Gui;

/// <summary>
/// Finding a file in the archives.
/// </summary>
/// <remarks>
/// The pane the whole front end exists for. Someone who has just unpacked the
/// game cannot name a path, and the container offers no listing, so this is the
/// only way in: walk the index once, then search what it spelled.
/// </remarks>
public sealed class BrowseViewModel : ViewModelBase
{
    /// <summary>
    /// How many results are shown at once.
    /// </summary>
    /// <remarks>
    /// A single letter matches hundreds of thousands of paths, and drawing them
    /// is slower than finding them. The order is what makes a cap tolerable:
    /// results are ranked by what the tool can open, so the first screenful is
    /// the useful one rather than whatever sorts first.
    /// </remarks>
    public const int Shown = 500;

    /// <summary>
    /// How long typing must pause before the search runs.
    /// </summary>
    /// <remarks>
    /// Short enough to feel immediate, long enough that typing a word runs one
    /// search rather than seven.
    /// </remarks>
    private const int SettleMilliseconds = 120;

    private ArchiveSearch? _index;
    private int _pathCount;
    private CancellationTokenSource? _pending;
    private CancellationTokenSource? _typing;
    private string? _folder;
    private string? _looseFolder;
    private string _search = string.Empty;
    private string _fileType = AnyType;
    private string _status = "Choose the folder holding sdf.sdftoc, or browse a folder of files.";
    private string? _selected;
    private bool _busy;

    /// <summary>What the type dropdown shows when it is not narrowing anything.</summary>
    public const string AnyType = "Any type";

    public BrowseViewModel() => ClearSearchCommand = new RelayCommand(() => Search = string.Empty);

    /// <summary>
    /// The file types the opened archives hold, most numerous first, with
    /// <see cref="AnyType"/> at the front.
    /// </summary>
    /// <remarks>
    /// Filled from the index rather than from a list written here. The archives
    /// hold types this tool has no reader for, and seeing them is most of what a
    /// browser is for — a curated list would quietly hide them.
    /// </remarks>
    public ObservableCollection<string> FileTypes { get; } = [AnyType];

    /// <summary>The type currently narrowing the search.</summary>
    /// <remarks>
    /// A type on its own is a valid search, so choosing one with the box empty
    /// lists that type rather than waiting for text.
    /// </remarks>
    public string FileType
    {
        get => _fileType;
        set
        {
            if (Set(ref _fileType, value ?? AnyType))
            {
                _ = RefreshAsync();
            }
        }
    }

    /// <summary>Every path the archives hold, for a pane that needs them all.</summary>
    public ImmutableArray<SdfPathEntry> Paths { get; private set; } = [];

    /// <summary>The paths currently listed, already capped.</summary>
    public ObservableCollection<string> Results { get; } = [];

    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Raised when the user picks a path, so the asset pane can follow.</summary>
    public event Action<string>? Chosen;

    /// <summary>Raised when an archive folder has been read, with its root.</summary>
    public event Action<string>? Opened;

    /// <summary>
    /// Raised when a plain folder of loose files has been read, with its root.
    /// </summary>
    /// <remarks>
    /// A separate event rather than a flag on <see cref="Opened"/>, because the
    /// two mean different things to the panes listening: an archive root is a
    /// container to read through, a content root is a tree to read from. Panes
    /// that only work against the archives simply do not subscribe.
    /// </remarks>
    public event Action<string>? OpenedFolder;

    /// <summary>Where the archives were opened from, for the folder button's label.</summary>
    public string FolderLabel => _folder is null ? "Choose archive folder…" : Shorten(_folder);

    /// <summary>
    /// Where a loose folder was opened from, for the second button's label.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="FolderLabel"/> so each button says what it
    /// itself did. One label shared between them showed a loose folder's path on
    /// the archive button, which reads as "the archives are here" and is exactly
    /// wrong.
    /// </remarks>
    public string LooseFolderLabel =>
        _looseFolder is null ? "…or browse a folder of files" : Shorten(_looseFolder);

    /// <summary>What is being browsed, so the pane can say which it is.</summary>
    public bool IsLooseFolder { get; private set; }

    /// <summary>Whether the index has been walked and can be searched.</summary>
    public bool HasArchives => _index is not null;

    /// <summary>What the pane is doing, or what it found.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True while the index is being walked, so the window can say so.</summary>
    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value) && value is not null)
            {
                Chosen?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Walks the index of the archives in <paramref name="folder"/>.
    /// </summary>
    /// <remarks>
    /// Off the UI thread, and cancelling any walk already running: choosing a
    /// second folder while the first is still being read must end with the
    /// second one's contents, not whichever finished last.
    /// </remarks>
    public async Task OpenAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _pending?.Cancel();
        CancellationTokenSource mine = new();
        _pending = mine;

        _folder = folder;
        _looseFolder = null;
        Raise(nameof(FolderLabel));
        Raise(nameof(LooseFolderLabel));
        Busy = true;
        Status = "Reading the archive index…";

        Result<ImmutableArray<SdfPathEntry>> walked = await Task.Run(() =>
        {
            using SdfContentSource source = new(folder);
            return source.Paths();
        }).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        Busy = false;
        await AdoptAsync(walked, folder, loose: false).ConfigureAwait(true);
    }

    /// <summary>
    /// Lists a plain folder of loose files — an extraction, or a mod.
    /// </summary>
    /// <remarks>
    /// The same pane, the same search and the same type list. A folder that
    /// mirrors the archive's paths, which is what this tool's own extraction
    /// writes, browses exactly as the archives do; one that does not still
    /// lists, and simply will not resolve as a character's asset set.
    /// </remarks>
    public async Task OpenFolderAsync(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        _pending?.Cancel();
        CancellationTokenSource mine = new();
        _pending = mine;

        _looseFolder = folder;
        _folder = null;
        Raise(nameof(FolderLabel));
        Raise(nameof(LooseFolderLabel));
        Busy = true;
        Status = "Listing the folder…";

        Result<ImmutableArray<SdfPathEntry>> walked =
            await Task.Run(() => FolderIndex.Paths(folder)).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        Busy = false;
        await AdoptAsync(walked, folder, loose: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Takes a walked path list as the thing being browsed, whichever it came
    /// from.
    /// </summary>
    /// <remarks>
    /// Both sources end here on purpose. The searching, the type list and the
    /// capped results are the same work over the same shape, and the only thing
    /// that differs is which event the window is told about — so the difference
    /// is one parameter rather than two copies that drift.
    /// </remarks>
    private async Task AdoptAsync(Result<ImmutableArray<SdfPathEntry>> walked, string folder, bool loose)
    {
        if (!walked.TryGetValue(out ImmutableArray<SdfPathEntry> paths, out Refusal? refusal))
        {
            _index = null;
            Paths = [];
            Results.Clear();

            // The refusal says which of the container's parts could not be read
            // and why. Shortening it to "could not open" would discard the only
            // thing that tells the user what to do next.
            Status = refusal.Message;
            Raise(nameof(HasArchives));
            return;
        }

        // Prepared once, off the UI thread: normalizing every path costs 289ms
        // and repeating it per keystroke is most of what made typing lag.
        _index = await Task.Run(() => new ArchiveSearch(paths)).ConfigureAwait(true);
        Paths = paths;
        _pathCount = paths.Length;
        IsLooseFolder = loose;
        Raise(nameof(IsLooseFolder));

        ImmutableArray<(string Extension, int Count)> types =
            await Task.Run(() => _index.Extensions()).ConfigureAwait(true);
        FileTypes.Clear();
        FileTypes.Add(AnyType);
        // Ordered by how many there are, so the types worth looking at are at the
        // top of the list rather than alphabetically among the ones that are not.
        foreach ((string extension, int _) in types)
        {
            FileTypes.Add(extension);
        }

        _fileType = AnyType;
        Raise(nameof(FileType));
        Raise(nameof(HasArchives));

        if (loose)
        {
            OpenedFolder?.Invoke(folder);
        }
        else
        {
            Opened?.Invoke(folder);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Runs the search once typing settles, off the UI thread.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. Searching on the UI thread is what made the
    /// letters themselves appear late — a query matching most of the archive
    /// took two seconds — and without the pause, typing a seven-letter name
    /// starts seven searches of which six are already stale.
    /// </remarks>
    private async Task RefreshAsync()
    {
        _typing?.Cancel();
        CancellationTokenSource mine = new();
        _typing = mine;

        if (_index is null)
        {
            return;
        }

        string? narrowing = _fileType == AnyType ? null : _fileType;

        if (_search.Length == 0 && narrowing is null)
        {
            // Listed rather than left blank. An empty pane over a folder the user
            // has just opened reads as "there is nothing here", and the folder is
            // usually small enough that the whole thing fits; over the archives it
            // is a sample, which the status line says plainly.
            (ImmutableArray<SdfPathEntry> head, int total) = _index.First(Shown);
            Show(head, total);
            return;
        }

        try
        {
            await Task.Delay(SettleMilliseconds, mine.Token).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        string wanted = _search;
        ArchiveSearch index = _index;

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found =
            await Task.Run(() => index.Best(wanted, Shown, narrowing), mine.Token).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        if (!found.TryGetValue(out (ImmutableArray<SdfPathEntry> Best, int Total) hits, out Refusal? refusal))
        {
            Results.Clear();
            Status = refusal.Message;
            return;
        }

        Show(hits.Best, hits.Total);
    }

    /// <summary>Puts a page of paths on screen and says what is not on it.</summary>
    /// <remarks>
    /// Shared by the listing and the search so the cap is reported the same way
    /// in both. Saying how many were held back rather than only how many are
    /// shown: a cap the user cannot see is a cap that quietly loses their file.
    /// </remarks>
    private void Show(ImmutableArray<SdfPathEntry> shown, int total)
    {
        Results.Clear();
        foreach (SdfPathEntry entry in shown)
        {
            Results.Add(entry.Path);
        }

        Status = total > shown.Length
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{total:N0} paths, first {shown.Length} shown. Search or pick a file type to narrow them.")
            : string.Create(CultureInfo.InvariantCulture, $"{total:N0} of {_pathCount:N0} paths.");
    }

    /// <summary>Keeps the tail of a long path, which is the part that identifies it.</summary>
    private static string Shorten(string path) =>
        path.Length <= 44 ? path : "…" + path[^43..];
}
