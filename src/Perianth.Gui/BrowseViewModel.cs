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
    private string _search = string.Empty;
    private string _status = "Choose the folder holding sdf.sdftoc to begin.";
    private string? _selected;
    private bool _busy;

    public BrowseViewModel() => ClearSearchCommand = new RelayCommand(() => Search = string.Empty);

    /// <summary>Every path the archives hold, for a pane that needs them all.</summary>
    public ImmutableArray<SdfPathEntry> Paths { get; private set; } = [];

    /// <summary>The paths currently listed, already capped.</summary>
    public ObservableCollection<string> Results { get; } = [];

    public RelayCommand ClearSearchCommand { get; }

    /// <summary>Raised when the user picks a path, so the asset pane can follow.</summary>
    public event Action<string>? Chosen;

    /// <summary>Raised when an archive folder has been read, with its root.</summary>
    public event Action<string>? Opened;

    /// <summary>Where the archives were opened from, for the folder button's label.</summary>
    public string FolderLabel => _folder is null ? "Choose archive folder…" : Shorten(_folder);

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
        Raise(nameof(FolderLabel));
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
        Raise(nameof(HasArchives));
        Opened?.Invoke(folder);
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

        if (_search.Length == 0)
        {
            Results.Clear();
            Status = string.Create(
                CultureInfo.InvariantCulture, $"{_pathCount:N0} paths. Type to search.");
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
            await Task.Run(() => index.Best(wanted, Shown), mine.Token).ConfigureAwait(true);

        if (mine.IsCancellationRequested)
        {
            return;
        }

        Results.Clear();

        if (!found.TryGetValue(out (ImmutableArray<SdfPathEntry> Best, int Total) hits, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        foreach (SdfPathEntry entry in hits.Best)
        {
            Results.Add(entry.Path);
        }

        // Saying how many were held back, rather than only how many are shown:
        // a cap the user cannot see is a cap that quietly loses their file.
        Status = hits.Total > hits.Best.Length
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"{hits.Total:N0} matches, best {hits.Best.Length} shown. Narrow the search to see the rest.")
            : string.Create(CultureInfo.InvariantCulture, $"{hits.Total:N0} of {_pathCount:N0} paths.");
    }

    /// <summary>Keeps the tail of a long path, which is the part that identifies it.</summary>
    private static string Shorten(string path) =>
        path.Length <= 44 ? path : "…" + path[^43..];
}
