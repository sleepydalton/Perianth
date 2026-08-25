using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Perianth.Core;
using Perianth.Core.Audio;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Mmb;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Lipsync;
using Perianth.Formats.Sdf;
using Perianth.Pipeline;

namespace Perianth.Gui;

/// <summary>
/// Taking the resolved set out of the archives, and turning it into a GLB.
/// </summary>
/// <remarks>
/// <para>
/// Two actions over one working folder, because export reads files from disk
/// rather than from the container: extracting is not a separate errand but the
/// first half of exporting. Asking for it twice, or hiding it somewhere the
/// user did not choose, would both be worse — the extracted tree is the thing a
/// modder actually wants, and it is what a later diff compares against.
/// </para>
/// <para>
/// The two sit in named subfolders so that the game's own files and the GLBs
/// written from them never mix.
/// </para>
/// </remarks>
public sealed class ExportViewModel : ViewModelBase
{
    /// <summary>Where the game's own files land, under the working folder.</summary>
    public const string ExtractedFolder = "extracted";

    /// <summary>Where the GLBs land, under the working folder.</summary>
    public const string ExportsFolder = "exports";

    /// <summary>
    /// Where the user's own files are laid out when an export uses them.
    /// </summary>
    /// <remarks>
    /// Only ever the user's files — an edited texture, a mod folder they wrote.
    /// The game's own files are read out of the archives and never written, so
    /// this folder holds nothing that was not already theirs.
    /// </remarks>
    public const string OverridesFolder = "my-files";

    /// <summary>
    /// Where the summary starts warning that an export will take a moment.
    /// </summary>
    /// <remarks>
    /// Not a cap, and deliberately not enforced. A whole character's 44 clips
    /// exported in three and a half seconds to 6.4 MB, so nothing here is worth
    /// refusing — the number exists only so that asking for a lot says it will
    /// take a moment rather than appearing to hang.
    /// </remarks>
    private const int ManyClips = 12;

    private CancellationTokenSource? _running;
    private CharacterAssets? _assets;
    private ImmutableArray<SdfPathEntry> _paths = [];
    private string? _archiveRoot;
    private string? _contentRoot;
    private string? _rememberedArchives;
    private bool _fillFromArchives;
    private string? _working;
    private string _status = "Choose a model, then a working folder.";
    private string _progress = string.Empty;
    private bool _busy;
    private bool _pose = true;
    private bool _materials = true;
    private bool _staged = true;
    private string? _modFolder;
    private DonorChoice? _primaryDonor;
    private DonorChoice? _gapDonor;
    private string _donorNote = string.Empty;
    private string _clipFilter = string.Empty;
    private int _queuedIndex = -1;
    private bool _playInOrder = true;
    private bool _animate = true;
    private string _blinks = string.Empty;
    private bool _meshNeutralPupils;
    private int _extracted;
    private bool _lipsync;
    private bool _audio;
    private string _speechId = string.Empty;
    private string _locale = SpeechCatalogue.DefaultLocale;
    private string? _vgmstream;
    private string _speechStatus = string.Empty;
    private ImmutableHashSet<string>? _schedules;
    private SubtitleCatalogue? _subtitles;
    private string _lineSearch = string.Empty;

    public ExportViewModel()
    {
        SampleSpeechCommand = new RelayCommand(SampleSpeech, () => _schedules is not null);

        foreach (FacialChoice facial in Facial)
        {
            facial.Changed += Describe;
            facial.SurveyRequested += choice => _ = SurveyAsync(choice);
        }

        AddShownCommand = new RelayCommand(
            () => { foreach (ClipChoice c in ShownClips) { Queue.Add(c); } ClipsChanged(); },
            () => ShownClips.Count > 0);
        ClearQueueCommand = new RelayCommand(
            () => { Queue.Clear(); ClipsChanged(); },
            () => Queue.Count > 0);
        RemoveCommand = new RelayCommand(
            () => { if (QueuedIndex >= 0) { Queue.RemoveAt(QueuedIndex); ClipsChanged(); } },
            () => QueuedIndex >= 0);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => QueuedIndex > 0);
        MoveDownCommand = new RelayCommand(() => Move(1), () => QueuedIndex >= 0 && QueuedIndex < Queue.Count - 1);
        RepeatCommand = new RelayCommand(
            () => { Queue.Insert(QueuedIndex + 1, Queue[QueuedIndex]); ClipsChanged(); },
            () => QueuedIndex >= 0);

        // Asked for rather than automatic: reading 665 hierarchies is seconds of
        // work, and most models have a setup and never need it.
        FindDonorsCommand = new RelayCommand(() => _ = FindDonorsAsync(), () => NeedsDonor && !_busy);

        ExtractCommand = new RelayCommand(() => _ = RunAsync(exportAfterwards: false), () => Ready);
        ExportCommand = new RelayCommand(() => _ = RunAsync(exportAfterwards: true), () => Ready);
        CancelCommand = new RelayCommand(() => _running?.Cancel(), () => _busy);
    }

    public RelayCommand ExtractCommand { get; }

    public RelayCommand ExportCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Picks a line that has both a schedule and audio.</summary>
    public RelayCommand SampleSpeechCommand { get; }

    /// <summary>Lines matching what was typed, best first.</summary>
    public ObservableCollection<SpokenLine> Lines { get; } = [];

    /// <summary>
    /// Words to look for among the spoken lines.
    /// </summary>
    /// <remarks>
    /// The answer to "how do I find a voice line". Nothing names who speaks one,
    /// but the localization packages say what every line is, keyed by the same
    /// GUID the identifier table turns into a speech ID — 28,882 lines, of which
    /// 27,256 have both audio and a schedule.
    /// </remarks>
    public string LineSearch
    {
        get => _lineSearch;
        set
        {
            if (Set(ref _lineSearch, value))
            {
                SearchLines();
            }
        }
    }

    /// <summary>Taking a line from the list fills in its ID.</summary>
    public SpokenLine? ChosenLine
    {
        get => null;
        set
        {
            if (value is not null)
            {
                SpeechId = value.SpeechId;
            }
        }
    }

    /// <summary>What the last run had to say.</summary>
    public ObservableCollection<Note> Messages { get; } = [];

    /// <summary>
    /// Hierarchies offered to pose a model that has none of its own.
    /// </summary>
    /// <remarks>
    /// Twenty-nine of the game's characters ship without a setup ANIM, and
    /// nothing in such a model's own files leads to one that fits. The list is
    /// ranked, and each row says what it draws, because the choice is otherwise
    /// between 665 identical-looking names.
    /// </remarks>
    public ObservableCollection<DonorChoice> PrimaryDonors { get; } = [];

    /// <summary>Hierarchies offered for the parts the chosen pose cannot name.</summary>
    public ObservableCollection<DonorChoice> GapDonors { get; } = [];

    /// <summary>The hierarchy posing this model, where it has none of its own.</summary>
    public DonorChoice? PrimaryDonor
    {
        get => _primaryDonor;
        set
        {
            if (Set(ref _primaryDonor, value))
            {
                GapDonors.Clear();
                GapDonor = null;
                Raise(nameof(HasPrimaryDonor));
                Describe();
                _ = FindGapDonorsAsync();
            }
        }
    }

    /// <summary>The hierarchy filling what the pose cannot name.</summary>
    public DonorChoice? GapDonor
    {
        get => _gapDonor;
        set { if (Set(ref _gapDonor, value)) { Raise(nameof(DonorWarning)); Describe(); } }
    }

    /// <summary>Whether the search has found anything to choose from.</summary>
    public bool HasDonors => PrimaryDonors.Count > 0;

    /// <summary>Whether a gap filler can be chosen yet.</summary>
    public bool HasPrimaryDonor => _primaryDonor is not null;

    /// <summary>Whether this model needs a borrowed hierarchy at all.</summary>
    public bool NeedsDonor => _assets is not null && _assets.Setup is null;

    /// <summary>What the search found, or what it is doing.</summary>
    public string DonorNote
    {
        get => _donorNote;
        private set => Set(ref _donorNote, value);
    }

    /// <summary>
    /// Said plainly when the chosen gap filler disagrees with the pose.
    /// </summary>
    /// <remarks>
    /// The ranking already puts it last. This is for someone who picked it
    /// anyway: the export will look like scattered wreckage, and the only clue
    /// otherwise is opening it in a viewer.
    /// </remarks>
    public string DonorWarning => _gapDonor?.HasWarning == true ? _gapDonor.Warning : string.Empty;

    /// <summary>Every clip this model can play; none chosen means a still.</summary>
    public ObservableCollection<ClipChoice> Clips { get; } = [];

    /// <summary>
    /// The four facial systems, each with the vocabulary its atlas holds.
    /// </summary>
    public ImmutableArray<FacialChoice> Facial { get; } =
    [
        new FacialChoice("Mouth", 24),
        new FacialChoice("Eyes", 11),
        new FacialChoice("Pupils", 13),
        new FacialChoice("Eyebrows", 6),
    ];

    /// <summary>
    /// The first chosen clip, or none for a still.
    /// </summary>
    /// <remarks>
    /// Several can be chosen now, so this is the first of them rather than the
    /// selection itself. It stays because one clip is still the common case and
    /// because a prop is posed by whichever animation comes first; setting it
    /// means "choose exactly this one".
    /// </remarks>
    public ClipChoice? Clip
    {
        get => Chosen.Count > 0 ? Chosen[0] : null;
        set
        {
            Queue.Clear();
            if (value is not null)
            {
                ClipChoice row = Clips.FirstOrDefault(
                    c => string.Equals(c.VirtualPath, value.VirtualPath, StringComparison.Ordinal)) ?? value;
                if (!Clips.Contains(row))
                {
                    Clips.Add(row);
                }

                Queue.Add(row);
            }

            ClipsChanged();
        }
    }

    /// <summary>
    /// The animations to export, in the order they will play.
    /// </summary>
    /// <remarks>
    /// A list rather than a set of ticks, because the order matters and the same
    /// animation may appear twice. Ticking boxes could express neither.
    /// </remarks>
    public ObservableCollection<ClipChoice> Queue { get; } = [];

    /// <summary>The chosen animations, in the order they play.</summary>
    public IReadOnlyList<ClipChoice> Chosen => [.. Queue];

    /// <summary>Which row of the queue the reordering buttons act on.</summary>
    public int QueuedIndex
    {
        get => _queuedIndex;
        set
        {
            if (Set(ref _queuedIndex, value))
            {
                RemoveCommand.Reconsider();
                MoveUpCommand.Reconsider();
                MoveDownCommand.Reconsider();
                RepeatCommand.Reconsider();
            }
        }
    }

    /// <summary>Play them in order down one timeline, rather than keeping each apart.</summary>
    /// <remarks>
    /// On by default: it is the one that shows something when a viewer presses
    /// play. Separate animations arrive as tracks stashed across every animated
    /// object, which is correct and almost unusable for checking an export.
    /// </remarks>
    public bool PlayInOrder
    {
        get => _playInOrder;
        set { if (Set(ref _playInOrder, value)) { Raise(nameof(ClipSummary)); Describe(); } }
    }

    public bool HasClip => Queue.Count > 0;

    /// <summary>Words to narrow the clip list by; a character can have hundreds.</summary>
    public string ClipFilter
    {
        get => _clipFilter;
        set
        {
            if (Set(ref _clipFilter, value))
            {
                ShowClips();
            }
        }
    }

    /// <summary>The clips the filter currently admits.</summary>
    public ObservableCollection<ClipChoice> ShownClips { get; } = [];

    /// <summary>
    /// How many are chosen, and what that will cost.
    /// </summary>
    /// <remarks>
    /// The warning is about waiting rather than a limit, because there is no
    /// limit worth imposing: a character's whole set — 44 clips — measured 6.4 MB
    /// and three and a half seconds, where the geometry is written once and each
    /// animation adds only its own tracks. So this says what to expect and gets
    /// out of the way.
    /// </remarks>
    public string ClipSummary
    {
        get
        {
            if (Clips.Count == 0)
            {
                return string.Empty;
            }

            int chosen = Queue.Count;
            if (chosen == 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nothing queued, so this is a still. {Clips.Count} to choose from.");
            }

            string count = string.Create(
                CultureInfo.InvariantCulture, $"{chosen} queued of {Clips.Count}");

            string shape = chosen == 1
                ? "."
                : _playInOrder
                    ? ", playing one after another as a single animation."
                    : ", each kept as its own animation.";

            return chosen >= ManyClips
                ? count + shape + " This will take a few seconds and make a large file."
                : count + shape;
        }
    }

    /// <summary>Look for a hierarchy that can pose a model lacking its own.</summary>
    public RelayCommand FindDonorsCommand { get; }

    /// <summary>Add every animation the filter is showing to the end of the queue.</summary>
    public RelayCommand AddShownCommand { get; }

    /// <summary>Empty the queue.</summary>
    public RelayCommand ClearQueueCommand { get; }

    /// <summary>Take the selected row out of the queue.</summary>
    public RelayCommand RemoveCommand { get; }

    /// <summary>Move the selected row earlier.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Move the selected row later.</summary>
    public RelayCommand MoveDownCommand { get; }

    /// <summary>Queue the selected row a second time, straight after itself.</summary>
    public RelayCommand RepeatCommand { get; }

    /// <summary>Adds one animation to the end of the queue.</summary>
    public void Enqueue(ClipChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Queue.Add(choice);
        ClipsChanged();
    }

    private void Move(int by)
    {
        int from = _queuedIndex;
        ClipChoice row = Queue[from];
        Queue.RemoveAt(from);
        Queue.Insert(from + by, row);
        QueuedIndex = from + by;
        ClipsChanged();
    }

    /// <summary>Emit the whole clip rather than one sampled pose.</summary>
    public bool Animate
    {
        get => _animate;
        set { if (Set(ref _animate, value)) { Describe(); Raise(nameof(CostumeNote)); } }
    }

    /// <summary>
    /// Times, in seconds, at which to inject a blink.
    /// </summary>
    /// <remarks>
    /// Free text rather than a number box, because there can be several. The
    /// clips are short — a stride is about a third of a second — so a time
    /// beyond the end is easy to ask for and is refused by name.
    /// </remarks>
    public string Blinks
    {
        get => _blinks;
        set { if (Set(ref _blinks, value)) { Describe(); } }
    }

    /// <summary>The voice locales a speech ID can be taken from.</summary>
    /// <remarks>Static in fact, but bound to like any other list on this pane.</remarks>
    public static ImmutableArray<string> Locales => SpeechCatalogue.Locales;

    /// <summary>
    /// Drive the mouth from a speech schedule rather than a fixed state.
    /// </summary>
    public bool Lipsync
    {
        get => _lipsync;
        set
        {
            if (Set(ref _lipsync, value))
            {
                Raise(nameof(CanUseAudio));
                CheckSpeech();
            }
        }
    }

    /// <summary>
    /// Decode the voice line to a WAV beside the GLB.
    /// </summary>
    /// <remarks>
    /// Separate from the mouth on purpose. The schedule and the audio are
    /// different files with different requirements — one needs the lip-sync
    /// database, the other needs an external decoder — and someone without the
    /// decoder should still get a talking mouth rather than nothing at all.
    /// </remarks>
    public bool Audio
    {
        get => _audio;
        set { if (Set(ref _audio, value)) { CheckSpeech(); } }
    }

    public bool CanUseAudio => _lipsync;

    public string SpeechId
    {
        get => _speechId;
        set { if (Set(ref _speechId, value)) { CheckSpeech(); } }
    }

    public string Locale
    {
        get => _locale;
        set { if (Set(ref _locale, value)) { CheckSpeech(); } }
    }

    /// <summary>Whether the ID has a schedule and a voice file, said plainly.</summary>
    public string SpeechStatus
    {
        get => _speechStatus;
        private set => Set(ref _speechStatus, value);
    }

    /// <summary>Where the decoder is, or nothing.</summary>
    public string? Vgmstream
    {
        get => _vgmstream;
        private set
        {
            if (Set(ref _vgmstream, value))
            {
                Raise(nameof(VgmstreamLabel));
                CheckSpeech();
            }
        }
    }

    public string VgmstreamLabel => _vgmstream is null
        ? "vgmstream-cli not found — choose it"
        : "Decoder: " + Shorten(_vgmstream);

    /// <summary>Takes the decoder the user pointed at.</summary>
    public void UseVgmstream(string path)
    {
        Vgmstream = path;
        Saved?.Invoke();
    }

    /// <summary>Raised whenever something worth remembering changed.</summary>
    public event Action? Saved;

    /// <summary>Suppress the pupil atlas translation, reaching the mesh placement.</summary>
    public bool MeshNeutralPupils
    {
        get => _meshNeutralPupils;
        set { if (Set(ref _meshNeutralPupils, value)) { Describe(); } }
    }

    public string WorkingLabel => _working is null ? "Choose working folder…" : Shorten(_working);

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>How far a long run has got.</summary>
    public string Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                Raise(nameof(Idle));
                ExtractCommand.Reconsider();
                ExportCommand.Reconsider();
                CancelCommand.Reconsider();
                foreach (FacialChoice facial in Facial)
                {
                    facial.Busy = value;
                }
            }
        }
    }

    public bool Idle => !_busy;

    /// <summary>Whether a model and a folder are both in hand.</summary>
    public bool Ready => _assets is not null && _working is not null && !_busy;

    /// <summary>Place the parts using the resolved setup ANIM.</summary>
    public bool Pose
    {
        get => _pose;
        set { if (Set(ref _pose, value)) { Describe(); Raise(nameof(PoseNote)); } }
    }

    /// <summary>
    /// What an export will look like when nothing poses it.
    /// </summary>
    /// <remarks>
    /// Worth saying in the pane rather than only in the diagnostics afterwards.
    /// An unposed model shows every alternate state at once — a prop shows its
    /// intact and broken halves together — and the result reads as missing and
    /// doubled pieces, which looks like a fault in the tool rather than the
    /// absence of a choice. Props have no setup ANIM at all; the convention is
    /// a character one, so the Animation list is where their pose comes from.
    /// </remarks>
    /// <summary>
    /// What the pose checkbox is offering, which differs by what poses it.
    /// </summary>
    public string PoseLabel =>
        _assets?.Setup is null && _assets?.Clips.IsEmpty == false
            ? "Pose with the chosen animation"
            : "Pose with the setup ANIM";

    public string PoseNote
    {
        get
        {
            if (_assets is null)
            {
                return string.Empty;
            }

            if (_assets.Setup is not null)
            {
                return _pose
                    ? string.Empty
                    : "Unposed: every alternate state at once, so expect missing and doubled pieces.";
            }

            if (_assets.Clips.IsEmpty)
            {
                return "Nothing in the archives poses this model, so it can only be exported as its complete part list.";
            }

            return Clip is null
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"No setup ANIM, which is normal for a prop: the Animation below is what poses it. Choose one of its {_assets.Clips.Length} — an idle is usually the resting state. Without one you get every alternate state at once: missing and doubled pieces, and art that reads mirrored.")
                : "Posed by the animation chosen below.";
        }
    }

    /// <summary>Reconstruct materials from the editordata and its textures.</summary>
    public bool Materials
    {
        get => _materials;
        set { if (Set(ref _materials, value)) { Describe(); } }
    }

    /// <summary>
    /// Whether to lay the texture pane's unsaved edits over the extracted files
    /// before exporting.
    /// </summary>
    /// <remarks>
    /// So a change can be looked at in Blender before it is ever loaded in the
    /// game, which matters more than it sounds: the mod loader is the scarce
    /// resource here, and a GLB costs nothing. It works because the extracted
    /// tree and a mod folder are the same shape — the archive's own paths — so
    /// laying one over the other is a file copy and no more.
    /// </remarks>
    public bool IncludeStagedChanges
    {
        get => _staged;
        set { if (Set(ref _staged, value)) { Describe(); Raise(nameof(ChangesNote)); } }
    }

    /// <summary>
    /// Writes the texture pane's staged edits into the extracted tree.
    /// </summary>
    /// <remarks>
    /// Supplied by <c>MainViewModel</c> rather than reached for, so this pane
    /// keeps knowing nothing about the other one beyond a function that puts
    /// files somewhere.
    /// </remarks>
    public Func<string, Result<int>>? StagedChanges { get; set; }

    /// <summary>How many edits the texture pane is holding, for the note below.</summary>
    public Func<int>? StagedCount { get; set; }

    /// <summary>
    /// What the character is wearing, drawn into the same file.
    /// </summary>
    /// <remarks>
    /// A function rather than a value because the costume pane owns the choice
    /// and this pane only asks at the moment it composes. The two panes do not
    /// know about each other; the window connects them.
    /// </remarks>
    public Func<ImmutableArray<WornModel>>? Equipment { get; set; }

    /// <summary>
    /// What will happen to what the character is wearing, when that is not
    /// simply "it goes in".
    /// </summary>
    /// <remarks>
    /// The pipeline refuses equipment alongside an animation, and its message
    /// names command-line flags — which a window user cannot act on. So the
    /// window does not produce that combination, and says so here instead.
    /// </remarks>
    public string CostumeNote
    {
        get
        {
            int worn = Equipment?.Invoke().Length ?? 0;
            if (worn == 0)
            {
                return string.Empty;
            }

            string pieces = worn == 1 ? "1 worn piece" : $"{worn} worn pieces";

            return _assets?.Setup is null && _primaryDonor is null
                ? $"{pieces} left out: they need a pose to be drawn into."
                : $"{pieces} will be exported with the character, and move with it.";
        }
    }

    /// <summary>Says what is worn may have changed, so the note is re-read.</summary>
    public void CostumeChanged() => Raise(nameof(CostumeNote));

    /// <summary>A mod folder to export against, or null for none.</summary>
    /// <remarks>
    /// The durable half of the same idea. Edits held in the texture pane are
    /// lost once they are written into a mod, which is the ordinary thing to
    /// do — so without this, the natural order of edit, write the mod, then
    /// look at it, had nothing left to apply and said nothing about it.
    /// </remarks>
    public string? ModFolder
    {
        get => _modFolder;
        set
        {
            if (Set(ref _modFolder, value))
            {
                Raise(nameof(ModFolderLabel));
                Raise(nameof(ChangesNote));
            }
        }
    }

    /// <summary>What the mod folder button shows.</summary>
    public string ModFolderLabel =>
        _modFolder is null
            ? "Textures from a mod folder…"
            : "Mod: " + Path.GetFileName(_modFolder.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>What the export will actually apply on top of the game's files.</summary>
    public string ChangesNote
    {
        get
        {
            if (!_staged)
            {
                return string.Empty;
            }

            int waiting = StagedCount?.Invoke() ?? 0;

            if (waiting > 0 && _modFolder is not null)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Using that mod, plus {waiting} unsaved {(waiting == 1 ? "edit" : "edits")} of your own.");
            }

            if (waiting > 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Using {waiting} unsaved {(waiting == 1 ? "edit" : "edits")} of your own.");
            }

            return _modFolder is not null
                ? "Using that mod folder."
                : "Nothing to apply: no unsaved edits on the Textures or Shape tabs. Writing a mod clears them, so choose that mod folder here.";
        }
    }

    /// <summary>Remembers which archives the files come from.</summary>
    public void UseArchives(string archiveRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = archiveRoot;
        _rememberedArchives = archiveRoot;
        _contentRoot = null;
        _paths = paths;
        Raise(nameof(IsFolderSource));
        Raise(nameof(CanFillFromArchives));
    }

    /// <summary>
    /// Takes a plain folder of loose files as the source instead of the archives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The archive root is cleared rather than kept alongside. Browsing a folder
    /// means exporting what is in it, and leaving a previously opened archive
    /// underneath would silently fill the gaps in a mod folder with the game's
    /// own files — which is the one thing someone checking their mod is trying
    /// to find out.
    /// </para>
    /// <para>
    /// Everything here that reads the archives directly — the hierarchy search,
    /// the facial survey, finding a voice line — already gives up quietly when
    /// there is no archive root, so those simply do nothing rather than needing
    /// a second code path.
    /// </para>
    /// </remarks>
    public void UseFolder(string contentRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _contentRoot = contentRoot;
        _archiveRoot = _fillFromArchives ? _rememberedArchives : null;
        _paths = paths;
        Raise(nameof(IsFolderSource));
        Raise(nameof(CanFillFromArchives));
    }

    /// <summary>Whether a folder is what is being exported from.</summary>
    public bool IsFolderSource => _contentRoot is not null;

    /// <summary>Whether there are archives to fall back on at all.</summary>
    public bool CanFillFromArchives => _contentRoot is not null && _rememberedArchives is not null;

    /// <summary>
    /// Whether files the folder does not hold come from the game's archives.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Off by default, and that is the point of it. A mod folder holds only what
    /// was changed, so filling the gaps is the only way to export one at all —
    /// but filling them silently is how a mod with a misspelt path exports
    /// perfectly here and draws nothing in the game. Asking makes the difference
    /// between "this is my mod" and "this is my mod over the game" a thing the
    /// user said rather than a thing the tool assumed.
    /// </para>
    /// <para>
    /// It re-enables the archive-backed parts of this pane too — the hierarchy
    /// search, the facial survey, finding a voice line — because when it is on,
    /// the archives really are available to them.
    /// </para>
    /// </remarks>
    public bool FillFromArchives
    {
        get => _fillFromArchives;
        set
        {
            if (Set(ref _fillFromArchives, value))
            {
                _archiveRoot = _contentRoot is null || value ? _rememberedArchives : null;
            }
        }
    }

    /// <summary>Takes the model the middle pane resolved.</summary>
    public void Show(CharacterAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        _assets = assets;
        Raise(nameof(PoseNote));
        Raise(nameof(PoseLabel));
        Messages.Clear();

        // The options belong to the model that was resolved, so a new model
        // starts from its own defaults rather than the last one's leftovers.
        PrimaryDonors.Clear();
        GapDonors.Clear();
        _primaryDonor = null;
        _gapDonor = null;
        DonorNote = string.Empty;
        Raise(nameof(NeedsDonor));
        Raise(nameof(PrimaryDonor));
        Raise(nameof(GapDonor));
        Raise(nameof(HasPrimaryDonor));
        Raise(nameof(HasDonors));
        FindDonorsCommand.Reconsider();

        Clips.Clear();
        foreach (ResolvedAsset clip in assets.Clips)
        {
            Clips.Add(ClipChoice.For(clip.VirtualPath, assets.Name));
        }

        Queue.Clear();
        QueuedIndex = -1;
        ClipFilter = string.Empty;
        ShowClips();
        ClipsChanged();

        Facial[0].Available = assets.Mouth is not null;
        Facial[1].Available = assets.Eyes is not null;
        Facial[2].Available = assets.Pupils is not null;
        Facial[3].Available = assets.Eyebrows is not null;
        foreach (FacialChoice facial in Facial)
        {
            facial.Clear();
        }

        _blinks = string.Empty;
        Raise(nameof(Blinks));
        _meshNeutralPupils = false;
        Raise(nameof(MeshNeutralPupils));

        Raise(nameof(Ready));
        ExtractCommand.Reconsider();
        ExportCommand.Reconsider();
        Describe();
    }

    /// <summary>The folder both halves write beneath, for remembering.</summary>
    public string? WorkingFolder => _working;

    /// <summary>Puts back what was chosen last time.</summary>
    public void Restore(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _vgmstream = settings.VgmstreamCli ?? VgmstreamDecoder.OnPath();
        Raise(nameof(VgmstreamLabel));

        if (settings.Locale is string locale && SpeechCatalogue.Locales.Contains(locale))
        {
            _locale = locale;
            Raise(nameof(Locale));
        }

        if (settings.WorkingFolder is string folder && Directory.Exists(folder))
        {
            UseWorkingFolder(folder);
        }
    }

    /// <summary>Sets the folder both halves write beneath.</summary>
    public void UseWorkingFolder(string folder)
    {
        _working = folder;
        Raise(nameof(WorkingLabel));
        Raise(nameof(Ready));
        ExtractCommand.Reconsider();
        ExportCommand.Reconsider();
        Describe();
        Saved?.Invoke();
    }

    /// <summary>
    /// Says whether the speech ID has a schedule, a voice file, and a decoder.
    /// </summary>
    /// <remarks>
    /// Answered while it is being typed, because all three facts are cheap to
    /// check and each has a different remedy. Left to the export, a missing
    /// decoder refuses the whole thing after the geometry is already built.
    /// </remarks>
    private void CheckSpeech()
    {
        Describe();

        if (!_lipsync)
        {
            SpeechStatus = string.Empty;
            return;
        }

        if (_assets?.LipsyncDatabase is null)
        {
            SpeechStatus = "No lip-sync database was found, so a schedule cannot be played.";
            return;
        }

        // Before any of the early returns below: the button that offers a
        // workable line is useless if it only wakes up once everything else is
        // already right.
        LoadSchedules();

        if (_speechId.Trim().Length == 0)
        {
            SpeechStatus = _schedules is null
                ? "Enter a speech ID to play."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"Enter a speech ID, or take one of the {_schedules.Count:N0} the database can play.");
            return;
        }

        Result<SpeechAudio> found = SpeechCatalogue.Find(_paths, _speechId, _locale);
        if (!found.TryGetValue(out SpeechAudio? audio, out Refusal? refusal))
        {
            SpeechStatus = refusal.Message;
            return;
        }

        if (audio.Locales.Length == 0)
        {
            SpeechStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"No voice file for {audio.SpeechId}. The mouth will still move if the database has its schedule.");
            return;
        }

        if (audio.Wem is null)
        {
            SpeechStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"{audio.SpeechId} is not in {_locale}, but is in: {string.Join(", ", audio.Locales)}.");
            return;
        }

        // Audio and schedules are different populations: 55,677 lines are
        // voiced, 35,334 have schedules, and only 31,865 have both. A number
        // that plays can still leave the face still, which looks like a broken
        // export rather than a line without a schedule.
        if (_schedules is not null && !_schedules.Contains(audio.SpeechId))
        {
            SpeechStatus = string.Create(
                CultureInfo.InvariantCulture,
                $"{audio.SpeechId} has audio but no lip-sync schedule, so the mouth will not move. Take a line that has both.");
            return;
        }

        if (_audio && _vgmstream is null)
        {
            SpeechStatus = "Audio needs vgmstream-cli, which was not found. Choose it below, or untick audio for a silent mouth.";
            return;
        }

        LoadSubtitles();
        string said = _subtitles?.Line(audio.SpeechId) is SpokenLine line ? $" \u201c{line.Text}\u201d" : string.Empty;

        SpeechStatus = string.Create(
            CultureInfo.InvariantCulture, $"{audio.SpeechId}:{said} schedule and audio, in {_locale}.");
    }

    /// <summary>
    /// Reads which lines the database can play, once.
    /// </summary>
    /// <remarks>
    /// From the archives rather than the extraction, because the answer is
    /// wanted while the ID is being typed and the extraction has not happened
    /// yet.
    /// </remarks>
    private void LoadSchedules()
    {
        if (_schedules is not null || _assets?.LipsyncDatabase is null || _archiveRoot is null)
        {
            return;
        }

        try
        {
            using SdfContentSource source = new(_archiveRoot);
            Result<SdfContent> content = source.Read(_assets.LipsyncDatabase);
            if (!content.TryGetValue(out SdfContent bytes, out _) || !bytes.IsPresent)
            {
                return;
            }

            Result<ImmutableArray<string>> ids = LipsyncReader.Ids(
                SourceFile.FromMemory(_assets.LipsyncDatabase, bytes.Bytes));

            if (ids.TryGetValue(out ImmutableArray<string> known, out _))
            {
                _schedules = [.. known];
                SampleSpeechCommand.Reconsider();
            }
        }
        catch (IOException)
        {
            // Without the list the field simply checks less; it is not worth
            // failing over.
        }
    }

    private void SearchLines()
    {
        Lines.Clear();
        LoadSubtitles();

        if (_subtitles is null || _lineSearch.Trim().Length == 0)
        {
            return;
        }

        // Said once per search rather than as a permanent label: the words are
        // findable, the speaker is not. Nothing static says who speaks a line —
        // the subtitle packages carry GUID, index and text and no more, the
        // 1,110 speaker IDs in the sound constants are never joined to a line,
        // and the scene sequences name clips after scenes rather than
        // characters. The game casts the voice at runtime.
        SpeechStatus = "Lines are found by their words. Which character speaks one is not recorded anywhere, "
            + "so check the voice suits the model you are exporting.";

        Result<ImmutableArray<SpokenLine>> found = _subtitles.Search(_lineSearch, limit: 40);
        if (found.IsRefused)
        {
            return;
        }

        foreach (SpokenLine line in found.Value)
        {
            Lines.Add(line);
        }
    }

    /// <summary>
    /// Reads the subtitle packages, once.
    /// </summary>
    /// <remarks>
    /// English regardless of the audio locale: the text is how the line is
    /// found, and the locale decides which voice recording is taken. Someone
    /// searching in English for a German reading is doing something reasonable.
    /// </remarks>
    private void LoadSubtitles()
    {
        if (_subtitles is not null || _archiveRoot is null)
        {
            return;
        }

        try
        {
            using SdfContentSource source = new(_archiveRoot);

            Result<SdfContent> ids = source.Read("camel/localization/packages/oasisids.txt");
            Result<SdfContent> subs = source.Read("camel/localization/packages/english/subtitles.locpack");

            if (!ids.TryGetValue(out SdfContent table, out _) || !table.IsPresent ||
                !subs.TryGetValue(out SdfContent text, out _) || !text.IsPresent)
            {
                return;
            }

            Result<SubtitleCatalogue> built = SubtitleCatalogue.Read(table.Bytes, [text.Bytes]);
            if (built.TryGetValue(out SubtitleCatalogue? catalogue, out _))
            {
                _subtitles = catalogue;
            }
        }
        catch (IOException)
        {
            // Without the packages the field simply searches nothing.
        }
    }

    /// <summary>
    /// Offers a line that will actually work.
    /// </summary>
    /// <remarks>
    /// Nothing authored links a character to a speech ID — the census in
    /// Roadmap §6.7 measured both forms that exist and neither reaches one — so
    /// there is nothing to search by. What can be offered is a line that has
    /// both halves, which beats typing numbers until one does.
    /// </remarks>
    private void SampleSpeech()
    {
        if (_schedules is null)
        {
            return;
        }

        foreach (string id in _schedules.OrderBy(_ => Random.Shared.Next()).Take(64))
        {
            Result<SpeechAudio> found = SpeechCatalogue.Find(_paths, id, _locale);
            if (!found.IsRefused && found.Value.Wem is not null)
            {
                SpeechId = id;
                return;
            }
        }

        SpeechStatus = "Could not find a line with both a schedule and audio in this locale.";
    }

    /// <summary>The voice file this export needs extracting, if any.</summary>
    private string? SpeechWem()
    {
        if (!_lipsync || !_audio || _speechId.Trim().Length == 0)
        {
            return null;
        }

        Result<SpeechAudio> found = SpeechCatalogue.Find(_paths, _speechId, _locale);
        return found.IsRefused ? null : found.Value.Wem;
    }

    /// <summary>
    /// Says what the current options would produce, or why they cannot.
    /// </summary>
    /// <remarks>
    /// The rules between an export's settings live beside the request rather
    /// than in the command line's grammar, so a window can ask the same question
    /// before running anything. A refusal that arrives while the boxes are still
    /// being ticked is worth more than the same refusal after a wait.
    /// </remarks>
    private void Describe()
    {
        if (_assets is null)
        {
            Status = "Choose a model on the left.";
            return;
        }

        if (_working is null)
        {
            Status = "Choose a working folder to extract and export into.";
            return;
        }

        Result<ExportRequest> checked_ = ExportRequest.Validate(Compose(_working));
        Status = checked_.IsRefused
            ? checked_.Refusal.Message
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Ready: Export writes a GLB to {ExportsFolder}/ and nothing else. Extract writes the model's {_assets.Paths().Length} own files plus the textures they use, to {ExtractedFolder}/.");
    }

    /// <summary>
    /// The export this pane would run, with paths pointing into the extraction.
    /// </summary>
    /// <summary>
    /// Lays the user's own files over the extracted tree, and says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// After extraction and before export, so the edits sit on top of the
    /// game's own files exactly as a mod would. The written mod first and the
    /// unsaved edits second, so an edit in hand wins over the same file
    /// already written out.
    /// </para>
    /// <para>
    /// One method because there are two export paths — the ordinary one and
    /// the loop that walks a facial system's states — and the first version of
    /// this lived in only one of them. It was the loop, so the Export button
    /// applied nothing and said nothing.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Finds the hierarchies that could pose a model with no setup of its own.
    /// </summary>
    /// <remarks>
    /// On a background thread and only when asked: it reads every setup ANIM the
    /// archives hold, which is 665 of them, and poses a shortlist. Counting is
    /// cheap and posing is not, so the ranking does the counting first -- see
    /// DonorSearch.
    /// </remarks>
    private async Task FindDonorsAsync()
    {
        if (_assets is null || _archiveRoot is null)
        {
            return;
        }

        DonorNote = "Looking for hierarchies that name this model's parts...";
        PrimaryDonors.Clear();
        PrimaryDonor = null;
        Raise(nameof(HasDonors));

        ImmutableArray<DonorCandidate> found = await Task.Run(() =>
        {
            Result<GeometryModel> geometry = ReadGeometry();
            return geometry.IsSuccess
                ? DonorSearch.Primaries(geometry.Value, ReadSetups(), declared: Declared())
                : [];
        }).ConfigureAwait(true);

        foreach (DonorCandidate candidate in found)
        {
            PrimaryDonors.Add(new DonorChoice(candidate, isGapFiller: false));
        }

        Raise(nameof(HasDonors));
        // Deliberately unconfident. An earlier wording said these "fit" and that
        // the first "poses the most of it", and both overclaimed: none of them is
        // this model's hierarchy, the ordering is by how much each draws rather
        // than by how right it is, and on a real model the best result came from
        // combining two that each ranked below the one drawing the most. Say
        // what was counted and leave the judgement where it belongs.
        DonorNote = PrimaryDonors.Count == 0
            ? "No hierarchy in the archives names any of this model's parts."
            : PrimaryDonors[0].Candidate.Declared
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{PrimaryDonors.Count} to try, ordered by how much of the model each one draws. The first is the one the game's own files name for this character, which is a record and not a promise — try others, and try filling the gaps from a second.")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{PrimaryDonors.Count} to try, ordered by how much of the model each one draws — which is not the same as which is right. Two together often cover more than any one alone, so try filling the gaps from a second.");
    }

    /// <summary>
    /// The hierarchies the game's own actor definition names for this model.
    /// </summary>
    /// <remarks>
    /// Two cheap reads rather than a search, and empty is the ordinary answer:
    /// most models have no actor definition, and most systems name no setup. A
    /// refusal here is not worth reporting — the search runs either way, and
    /// this only decides the order within it.
    /// </remarks>
    private HashSet<string> Declared()
    {
        HashSet<string> declared = new(StringComparer.OrdinalIgnoreCase);
        if (_assets is null || _archiveRoot is null)
        {
            return declared;
        }

        using ContentSources content = new(contentRoot: null, sdfRoot: _archiveRoot);
        Result<ImmutableArray<string>> setups = AnimationSystems.SetupsFor(content, _assets.Model);
        if (setups.TryGetValue(out ImmutableArray<string> named, out _))
        {
            declared.UnionWith(named);
        }

        return declared;
    }

    /// <summary>Finds hierarchies for the parts the chosen pose cannot name.</summary>
    private async Task FindGapDonorsAsync()
    {
        if (_assets is null || _archiveRoot is null || _primaryDonor is null)
        {
            return;
        }

        string primaryPath = _primaryDonor.VirtualPath;
        ImmutableArray<DonorCandidate> found = await Task.Run(() =>
        {
            Result<GeometryModel> geometry = ReadGeometry();
            if (!geometry.IsSuccess)
            {
                return ImmutableArray<DonorCandidate>.Empty;
            }

            using SdfContentSource source = new(_archiveRoot);
            Result<SdfContent> raw = source.Read(primaryPath);
            if (!raw.TryGetValue(out SdfContent bytes, out _) || !bytes.IsPresent)
            {
                return ImmutableArray<DonorCandidate>.Empty;
            }

            Result<AnimFile> primary = AnimReader.Read(
                SourceFile.FromMemory(primaryPath, bytes.Bytes), hierarchy: true);

            return primary.IsSuccess
                ? DonorSearch.GapFillers(geometry.Value, primary.Value, ReadSetups())
                : [];
        }).ConfigureAwait(true);

        foreach (DonorCandidate candidate in found)
        {
            GapDonors.Add(new DonorChoice(candidate, isGapFiller: true));
        }

        Raise(nameof(HasPrimaryDonor));
    }

    /// <summary>The model's assembled geometry, read from the archives.</summary>
    private Result<GeometryModel> ReadGeometry()
    {
        using SdfContentSource source = new(_archiveRoot!);

        Result<SdfContent> mmbBytes = source.Read(_assets!.Model);
        if (!mmbBytes.TryGetValue(out SdfContent mmbRaw, out Refusal? mmbRefusal) || !mmbRaw.IsPresent)
        {
            return mmbRefusal ?? Refusal.Resource("The archives hold no model.", DiagnosticIds.ResourceMissing);
        }

        Result<MmbModel> model = MmbReader.Read(SourceFile.FromMemory(_assets.Model, mmbRaw.Bytes));
        if (!model.IsSuccess)
        {
            return model.Refusal;
        }

        Result<SdfContent> camBytes = source.Read(_assets.Cameldata!);
        if (!camBytes.TryGetValue(out SdfContent camRaw, out Refusal? camRefusal) || !camRaw.IsPresent)
        {
            return camRefusal ?? Refusal.Resource("The archives hold no cameldata.", DiagnosticIds.ResourceMissing);
        }

        Result<CameldataFile> cameldata = CameldataReader.Read(
            SourceFile.FromMemory(_assets.Cameldata!, camRaw.Bytes));

        return cameldata.IsSuccess
            ? GeometryAssembler.Assemble(model.Value, cameldata.Value)
            : cameldata.Refusal;
    }

    /// <summary>Every setup ANIM the archives hold, read one at a time.</summary>
    /// <remarks>
    /// Streamed rather than gathered: 665 hierarchies held at once is a lot of
    /// memory for a list that only two of them will be picked from.
    /// </remarks>
    private IEnumerable<(string Path, AnimFile Anim)> ReadSetups()
    {
        using SdfContentSource source = new(_archiveRoot!);
        foreach (SdfPathEntry entry in _paths)
        {
            if (!entry.Path.EndsWith("_setup.anim", StringComparison.Ordinal))
            {
                continue;
            }

            Result<SdfContent> raw = source.Read(entry.Path);
            if (!raw.TryGetValue(out SdfContent bytes, out _) || !bytes.IsPresent)
            {
                continue;
            }

            Result<AnimFile> anim = AnimReader.Read(
                SourceFile.FromMemory(entry.Path, bytes.Bytes), hierarchy: true);
            if (anim.IsSuccess)
            {
                yield return (entry.Path, anim.Value);
            }
        }
    }

    /// <returns>False when something was named and could not be read.</returns>
    internal bool ApplyOwnFiles(string working)
    {
        if (!_staged)
        {
            return true;
        }

        string extracted = Path.Combine(working, OverridesFolder);
        int applied = 0;

        if (_modFolder is not null)
        {
            Result<int> copied = Overlay(_modFolder, extracted);
            if (!copied.TryGetValue(out int count, out Refusal? refusal))
            {
                Fail(refusal);
                return false;
            }

            applied += count;
        }

        if (StagedChanges is { } overlay)
        {
            Result<int> laid = overlay(extracted);
            if (!laid.TryGetValue(out int count, out Refusal? refusal))
            {
                Fail(refusal);
                return false;
            }

            applied += count;
        }

        if (applied > 0)
        {
            Say(NoteKind.Done, string.Create(
                CultureInfo.InvariantCulture,
                $"Using {applied} of your own {(applied == 1 ? "file" : "files")} instead of the game's."));
        }

        return true;
    }

    /// <summary>
    /// Copies a mod folder's files over the extracted tree.
    /// </summary>
    /// <remarks>
    /// Both are laid out with the game's own paths, which is what makes this a
    /// file copy rather than a merge. manifest.ini is the loader's and stands
    /// at no archive path, so it is left where it is.
    /// </remarks>
    private static Result<int> Overlay(string mod, string extracted)
    {
        if (!Directory.Exists(mod))
        {
            return Refusal.Resource($"'{mod}' is not a folder.", DiagnosticIds.ResourceMissing);
        }

        int copied = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(mod, file);

                if (relative.Equals("manifest.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destination = Path.Combine(extracted, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
                copied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{mod}' could not be read.", DiagnosticIds.ResourceMissing);
        }

        return Result.Ok(copied);
    }

    internal ExportRequest Compose(string working)
    {
        CharacterAssets assets = _assets!;

        // Export reads the game's files straight out of the archives, so the
        // inputs are archive paths and the only thing written is the GLB.
        // Asking for a model never meant asking for a copy of the game.
        string overrides = Path.Combine(working, OverridesFolder);
        string Local(string virtualPath) => virtualPath;

        string name = assets.Model[(assets.Model.LastIndexOf('/') + 1)..];
        string stem = name[..name.LastIndexOf('.')];

        // A prop has no setup ANIM — the convention is a character one, and no
        // prop in the archive has one — so the animation chosen in the list is
        // what poses it. That is exactly what the command line does when given
        // --setup-anim <that idle>: prp_aframe_sign_citywok goes from 25 parts
        // with every state overlaid to the 9 that are the standing sign.
        // A borrowed hierarchy poses a model that has none of its own, and takes
        // precedence over the prop convention of posing with the chosen clip:
        // asking for one is a more specific statement than picking an animation.
        string? pose = assets.Setup?.VirtualPath ?? _primaryDonor?.VirtualPath ?? Clip?.VirtualPath;
        bool posed = _pose && pose is not null;

        // Only a model with its own setup takes a second animation as a clip.
        // Where the chosen animation *is* the pose, passing it twice would ask
        // for a clip against itself.
        // With a setup of its own, every chosen animation plays against it. A
        // prop is posed by the first, so the rest are its clips and the first is
        // not passed again — that would be a clip against itself.
        List<string> clips = [];
        if (posed)
        {
            // Only where the first chosen animation *is* the pose is it left out
            // of the clips. A borrowed hierarchy poses instead, so every chosen
            // animation stays an animation.
            bool clipIsThePose = assets.Setup is null && _primaryDonor is null;
            foreach (ClipChoice choice in Chosen.Skip(clipIsThePose ? 1 : 0))
            {
                clips.Add(Local(choice.VirtualPath!));
            }
        }

        // A facial layer overlays a hierarchy, so without a pose there is
        // nothing to overlay and the atlases are left out rather than refused.
        string? Atlas(ResolvedAsset? asset, FacialChoice choice) =>
            posed && asset is not null && choice.State is not null ? Local(asset.VirtualPath) : null;

        int? State(FacialChoice choice) => posed ? choice.State : null;

        return new ExportRequest
        {
            Mmb = Local(assets.Model),
            Cameldata = assets.Cameldata is null ? string.Empty : Local(assets.Cameldata),
            Out = Path.Combine(working, ExportsFolder, Named(stem) + ".glb"),
            SetupAnim = posed ? Local(pose!) : null,
            ClipAnims = [.. clips],
            Animate = clips.Count > 0 && _animate,
            SeparateAnimations = !_playInOrder,
            Editordata = _materials && assets.Editordata is not null ? Local(assets.Editordata) : null,
            // The user's own files are tried first, then the archives — but only
            // when there are some. ApplyOwnFiles runs before this and writes the
            // folder only if something landed in it, so its existence is the
            // question rather than whether the checkbox is ticked: a ticked box
            // with nothing staged would otherwise name a folder nobody created.
            ContentRoot = Directory.Exists(overrides) ? overrides : _contentRoot,
            SdfRoot = _archiveRoot,
            // Stays on for a loose folder. Despite the name, this is the switch
            // between "these paths are virtual, resolve them" and "these paths
            // are files on disk" -- and a folder that mirrors the archive's
            // paths is resolved the same way, just with no archives behind it.
            // Turning it off here was a plausible reading that refused every
            // export from a folder, because the model path is then opened as a
            // literal file and no such file exists.
            ReadFromArchives = true,
            AllowUnposed = !posed,
            // A borrowed hierarchy does not account for the whole model -- that
            // is what makes it borrowed -- so the omissions are expected and
            // reported rather than refused.
            AllowMissingParts = _primaryDonor is not null,
            GapAnim = _primaryDonor is not null ? _gapDonor?.VirtualPath : null,
            // Only with a pose, which is the one thing the merge will not do
            // without: it refuses a posed model beside an unposed one. An
            // animation is no longer a reason to drop it -- the merge gives the
            // character's tracks to everything sharing its skeleton.
            With = posed ? Equipment?.Invoke() ?? [] : [],
            // Lip sync drives the mouth from the schedule, so it needs the
            // atlas and forbids a fixed state - the two would contradict.
            MouthAnim = Speaking(assets, posed) ? Local(assets.Mouth!.VirtualPath) : Atlas(assets.Mouth, Facial[0]),
            MouthState = Speaking(assets, posed) ? null : State(Facial[0]),
            EyesAnim = Atlas(assets.Eyes, Facial[1]) ?? BlinkAtlas(assets, posed),
            EyeState = State(Facial[1]),
            PupilsAnim = Atlas(assets.Pupils, Facial[2]),
            PupilState = State(Facial[2]),
            EyebrowsAnim = Atlas(assets.Eyebrows, Facial[3]),
            EyebrowState = State(Facial[3]),
            PupilPosition = _meshNeutralPupils ? "mesh-neutral" : "authored-state",
            BlinkAt = ParseBlinks(),
            LipsyncDatabase = Speaking(assets, posed) ? Local(assets.LipsyncDatabase!) : null,
            SpeechId = Speaking(assets, posed) ? _speechId.Trim() : null,
            WemRoot = Speaking(assets, posed) && _audio && SpeechWem() is not null
                ? Path.Combine(working, ExtractedFolder, "camel", "voice")
                : null,
            VgmstreamCli = Speaking(assets, posed) && _audio ? _vgmstream : null,
        };
    }

    /// <summary>The whole kit: every file the model could want.</summary>
    private ImmutableArray<string> AllPaths() =>
        SpeechWem() is string wem ? _assets!.Paths().Add(wem) : _assets!.Paths();

    /// <summary>
    /// Everything an extraction should hand over: the model's own files, and the
    /// textures its materials bind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="AllPaths"/> cannot include the textures, because everything it
    /// lists was found by naming convention and a texture is not named after the
    /// model. Reading the editordata is the only way to reach them, so this is
    /// asynchronous where that is not.
    /// </para>
    /// <para>
    /// The window has its own extraction path, separate from the command line's,
    /// and adding the textures to only one of them left this one quietly writing
    /// a kit that could not be exported from. Both call the same Core rule now.
    /// </para>
    /// </remarks>
    public async Task<ImmutableArray<string>> KitAsync()
    {
        ImmutableArray<string> own = AllPaths();
        CharacterAssets assets = _assets!;
        ImmutableArray<SdfPathEntry> paths = _paths;
        (string? content, string? sdf) = (_contentRoot, _archiveRoot);

        return await Task.Run(() =>
        {
            using ContentSources sources = new(content, sdf);
            Result<ImmutableArray<string>> bound =
                ArchiveExtraction.BoundTextures(paths, sources, assets);

            // A model whose textures cannot be listed is still worth extracting.
            // The export refuses over the missing one later, naming it.
            return bound.TryGetValue(out ImmutableArray<string> textures, out _)
                ? own.AddRange(textures)
                : own;
        }).ConfigureAwait(true);
    }

    /// <summary>Whether this export plays a speech schedule.</summary>
    private bool Speaking(CharacterAssets assets, bool posed) =>
        _lipsync && posed && assets.LipsyncDatabase is not null && assets.Mouth is not null
        && _speechId.Trim().Length > 0;

    /// <summary>
    /// The eye atlas a blink needs, even when no fixed eye state was chosen.
    /// </summary>
    /// <remarks>
    /// Blink drives the eye atlas from explicit events, so the atlas is required
    /// while its state is only the optional hold between them.
    /// </remarks>
    private string? BlinkAtlas(CharacterAssets assets, bool posed) =>
        !posed || assets.Eyes is null || ParseBlinks().Length == 0 ? null : assets.Eyes.VirtualPath;

    /// <summary>Reads the blink times, ignoring what is not yet a number.</summary>
    /// <remarks>
    /// Typing "0.4, " should not refuse mid-keystroke, so a fragment is simply
    /// not a time yet. What survives is checked against the clip when it runs.
    /// </remarks>
    private ImmutableArray<double> ParseBlinks()
    {
        if (string.IsNullOrWhiteSpace(_blinks))
        {
            return [];
        }

        ImmutableArray<double>.Builder times = ImmutableArray.CreateBuilder<double>();
        foreach (string piece in _blinks.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out double time))
            {
                times.Add(time);
            }
        }

        return times.ToImmutable();
    }

    /// <summary>
    /// The output name, carrying what was asked for.
    /// </summary>
    /// <remarks>
    /// Exporting a walk and then an idle should leave two files, not one
    /// overwritten twice — and the name is the only place the window can say
    /// which is which.
    /// </remarks>
    /// <summary>Re-checks everything that depends on which clips are chosen.</summary>
    private void ClipsChanged()
    {
        Raise(nameof(Clip));
        Raise(nameof(Chosen));
        Raise(nameof(HasClip));
        Raise(nameof(ClipSummary));
        Raise(nameof(PoseNote));
        Raise(nameof(PoseLabel));
        AddShownCommand.Reconsider();
        ClearQueueCommand.Reconsider();
        RemoveCommand.Reconsider();
        MoveUpCommand.Reconsider();
        MoveDownCommand.Reconsider();
        RepeatCommand.Reconsider();
        Describe();
    }

    /// <summary>
    /// Refills the shown list from the filter.
    /// </summary>
    /// <remarks>
    /// A filter rather than a longer scroll: a character can carry hundreds of
    /// clips, and the one being looked for is usually named after what it does.
    /// Hiding a row does not untick it — the chosen set survives retyping the
    /// search, which is what makes picking a few from several searches possible
    /// at all.
    /// </remarks>
    private void ShowClips()
    {
        ShownClips.Clear();
        foreach (ClipChoice choice in Clips)
        {
            if (_clipFilter.Length == 0 ||
                choice.Label.Contains(_clipFilter, StringComparison.OrdinalIgnoreCase))
            {
                ShownClips.Add(choice);
            }
        }

        AddShownCommand.Reconsider();
    }

    private string Named(string stem)
    {
        List<string> parts = [stem];

        if (Chosen.Count == 1)
        {
            parts.Add(Chosen[0].Label.Replace(' ', '_'));
        }
        else if (Chosen.Count > 1)
        {
            // Naming a file after twelve animations is not a name. The count is
            // what distinguishes this export from the single-clip one beside it.
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{Chosen.Count}_animations"));
        }

        // Every setting that changes what is drawn has to reach the name, or a
        // survey of 23 mouth states writes one file 23 times.
        foreach (FacialChoice facial in Facial)
        {
            if (facial.State is int state)
            {
                parts.Add(string.Create(
                    CultureInfo.InvariantCulture, $"{facial.Label.ToLowerInvariant()}{state:00}"));
            }
        }

        return string.Join('_', parts);
    }

    /// <summary>
    /// Exports one file per state of <paramref name="choice"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The states are numbers and nothing names them, so this is how a person
    /// finds out what they are: export the lot once, look through them, and know
    /// thereafter. It is the survey the deferred 3D preview would make
    /// unnecessary, not a substitute for it.
    /// </para>
    /// <para>
    /// A state the atlas does not reach is reported and skipped rather than
    /// ending the run. Cartman's mouth atlas holds 23 samples against the
    /// vocabulary's 24, and only 21 mouth textures exist in the whole archive,
    /// so gaps are expected and are part of what the survey shows.
    /// </para>
    /// </remarks>
    private async Task SurveyAsync(FacialChoice choice)
    {
        if (_assets is null || _working is null || _archiveRoot is null)
        {
            return;
        }

        if (!_pose || _assets.Setup is null)
        {
            Status = "A facial state overlays a pose, so surveying one needs the setup ANIM.";
            return;
        }

        CancellationTokenSource mine = new();
        _running = mine;
        Busy = true;
        Messages.Clear();

        int written = 0;
        List<int> empty = [];
        int restore = choice.Index;

        try
        {
            if (!ApplyOwnFiles(_working))
            {
                return;
            }

            Directory.CreateDirectory(Path.Combine(_working, ExportsFolder));

            for (int state = 1; state <= choice.States && !mine.IsCancellationRequested; state++)
            {
                Progress = string.Create(
                    CultureInfo.InvariantCulture, $"{choice.Label} state {state} of {choice.States}");

                choice.Index = state;
                Result<ExportRequest> request = ExportRequest.Validate(Compose(_working));
                if (!request.TryGetValue(out ExportRequest? settings, out Refusal? invalid))
                {
                    empty.Add(state);
                    continue;
                }

                Result<ExportOutcome> exported = await Task.Run(
                    () => ExportPipeline.Run(settings), mine.Token).ConfigureAwait(true);

                if (exported.IsRefused)
                {
                    empty.Add(state);
                    continue;
                }

                written++;
            }

            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {written} of {choice.States} {choice.Label.ToLowerInvariant()} states to {ExportsFolder}/.");

            if (empty.Count > 0)
            {
                Say(NoteKind.Caveat, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{choice.Label} states {string.Join(", ", empty)} produced nothing — this atlas does not reach them."));
            }

            Say(NoteKind.Tip, string.Create(
                CultureInfo.InvariantCulture,
                $"Open the {ExportsFolder}/ folder and step through the files to see what each number is."));

            // Measured: mouth state 12 emits a mesh called mouth13Front, and
            // eyebrow states 2 and 5 emit the same name as each other. The
            // filename is the only place the state is stated plainly.
            Messages.Insert(0, new Note(
                NoteKind.Tip,
                "Go by the file name, not the mesh name inside.",
                "The file name matches the number in this window. The mesh inside does not: mouth state 12 is a "
                + "mesh called mouth13Front, one ahead, and every eyebrow state emits the same name as the "
                + "others. Only the file name says which state you are looking at."));

            Messages.Insert(0, new Note(
                NoteKind.Tip,
                string.Create(CultureInfo.InvariantCulture, $"Each file holds one {choice.Label.ToLowerInvariant()} mesh."),
                "The rest of the numbered entries in Blender's outliner are empties — the atlas's selector nodes, "
                + "which carry no geometry. Toggling their visibility does nothing because there is nothing to show. "
                + "The one that matters is the only one with a mesh under it."));
        }
        catch (OperationCanceledException)
        {
            Status = string.Create(CultureInfo.InvariantCulture, $"Stopped after {written} states.");
        }
        finally
        {
            choice.Index = restore;
            Busy = false;
            Progress = string.Empty;
            Raise(nameof(Ready));
        }
    }

    /// <summary>
    /// Puts the model's files on disk, where the exporter reads them from.
    /// </summary>
    /// <remarks>
    /// Shared by the single export and the survey: both need the same files
    /// present, and the survey would otherwise extract them once per state.
    /// </remarks>
    private async Task<bool> ExtractAsync(CancellationTokenSource mine, ImmutableArray<string> wanted)
    {
        CharacterAssets assets = _assets!;
        string working = _working!;
        string archives = _archiveRoot!;
        ImmutableArray<SdfPathEntry> paths = _paths;

        int total = wanted.Length;

        Status = "Extracting…";
        Progress = string.Create(CultureInfo.InvariantCulture, $"0 of {total} files");

        IProgress<int> counted = new Progress<int>(done =>
            Progress = string.Create(CultureInfo.InvariantCulture, $"{done} of {total} files"));

        Result<ExtractionOutcome> extracted = await Task.Run(() =>
        {
            using SdfContentSource source = new(archives);
            Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Exactly(paths, wanted);

            return selected.TryGetValue(out ImmutableArray<SdfPathEntry> set, out Refusal? refusal)
                ? ArchiveExtraction.Extract(
                    source,
                    set,
                    assets.Model,
                    Path.Combine(working, ExtractedFolder),
                    limit: 0,
                    progress: counted,
                    cancellation: mine.Token)
                : refusal;
        }, mine.Token).ConfigureAwait(true);

        if (!extracted.TryGetValue(out ExtractionOutcome? outcome, out Refusal? extractRefusal))
        {
            Fail(extractRefusal);
            return false;
        }

        foreach (Diagnostic diagnostic in outcome.Diagnostics)
        {
            Messages.Add(Note.From(diagnostic));
        }

        _extracted = outcome.Files.Length;
        Progress = string.Empty;

        if (outcome.Cancelled)
        {
            Status = string.Create(
                CultureInfo.InvariantCulture, $"Stopped. {outcome.Files.Length} files extracted.");
            return false;
        }

        return true;
    }

    private async Task RunAsync(bool exportAfterwards)
    {
        if (_assets is null || _working is null || _archiveRoot is null)
        {
            return;
        }

        CancellationTokenSource mine = new();
        _running = mine;
        Busy = true;
        Messages.Clear();
        Progress = string.Empty;

        try
        {
            string working = _working;

            // Extract hands over the model's whole kit, which is what it is for.
            // Export reads the game's files out of the archives and writes none
            // of them: someone who asked for a model did not ask for a copy of
            // the game, and used to get several hundred files anyway.
            if (!exportAfterwards)
            {
                if (!await ExtractAsync(mine, await KitAsync().ConfigureAwait(true)).ConfigureAwait(true))
                {
                    return;
                }

                Status = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Extracted {_extracted} files to {ExtractedFolder}/.");
                return;
            }

            // The one game file an export can still write, and only when the
            // voice audio was asked for: vgmstream is a separate program and
            // decodes a file on disk, so there is nothing to hand it otherwise.
            if (SpeechWem() is string wem &&
                !await ExtractAsync(mine, [wem]).ConfigureAwait(true))
            {
                return;
            }

            if (!ApplyOwnFiles(working))
            {
                return;
            }

            Status = "Exporting…";

            // The subfolders are this window's convention, so creating them is
            // this window's job. Left to the writer, a missing directory is a
            // refusal about a file rather than about the folder it needed.
            Directory.CreateDirectory(Path.Combine(working, ExportsFolder));

            Result<ExportRequest> request = ExportRequest.Validate(Compose(working));
            if (!request.TryGetValue(out ExportRequest? settings, out Refusal? invalid))
            {
                Fail(invalid);
                return;
            }

            Result<ExportOutcome> exported = await Task.Run(
                () => ExportPipeline.Run(settings), mine.Token).ConfigureAwait(true);

            if (!exported.TryGetValue(out ExportOutcome? result, out Refusal? exportRefusal))
            {
                Fail(exportRefusal);
                return;
            }

            // Every warning, in full. The material disclosure in particular says
            // which of the engine's inputs the recovered shader does not
            // reproduce, and a viewer of the GLB has no other way to learn it.
            foreach (Diagnostic diagnostic in result.Diagnostics)
            {
                Messages.Add(Note.From(diagnostic));
            }

            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"Exported {result.Counts.Meshes} meshes, {result.Counts.Vertices:N0} vertices, {result.Counts.Triangles:N0} triangles.");
            Say(NoteKind.Done, string.Create(CultureInfo.InvariantCulture, $"Wrote {settings.Out}"));
            Advise(settings, result);
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        finally
        {
            Busy = false;
            Progress = string.Empty;
            Raise(nameof(Ready));
        }
    }

    /// <summary>
    /// Keeps what happened, rather than only what is happening.
    /// </summary>
    /// <remarks>
    /// A status line is replaced by the next one, and a run has several stages
    /// that pass in under a second. What refused, and what it wrote, has to stay
    /// on screen to be read at all.
    /// </remarks>
    private void Say(NoteKind kind, string message) => Messages.Insert(0, new Note(kind, message, null));

    private void Fail(Refusal refusal)
    {
        Status = refusal.Message;
        Messages.Insert(0, Note.Split(NoteKind.Problem, refusal.Message));
    }

    /// <summary>
    /// What to expect when the file is opened, rather than what the tool did.
    /// </summary>
    /// <remarks>
    /// Kept to what this particular export will actually look like. A tip that
    /// appears every time regardless is read once and skipped forever, which
    /// costs more than it gives.
    /// </remarks>
    private void Advise(ExportRequest settings, ExportOutcome result)
    {
        // Only where parts actually toggle: a facial atlas hides and shows the
        // state it is not showing, and an animated one does it repeatedly. A
        // plain posed still never pops anything, so the tip would be noise.
        if (settings.MouthAnim is not null || settings.EyesAnim is not null
            || settings.PupilsAnim is not null || settings.EyebrowsAnim is not null)
        {
            Messages.Insert(0, new Note(
            NoteKind.Tip,
            "Parts fading in oddly in Blender's Rendered view?",
            "glTF cannot say \"hide this part\", so the exporter scales it to zero instead, and a part reappears "
            + "instantly. Blender's temporal reprojection has no history for those pixels and fades them in over "
            + "several frames — worse the further away the camera is, because the stale history counts for more. "
            + "Render Properties › Sampling › untick Temporal Reprojection. Nothing is wrong with the file: it "
            + "shows correctly in Material Preview, which does not reproject."));
        }

        Messages.Insert(0, new Note(
            NoteKind.Tip,
            "Seeing stray lines around the model in Blender?",
            "Those are the node graph, not geometry: a posed export carries an empty per part, and Blender "
            + "draws each one with its relationship line to its parent. Viewport Overlays › untick Extras and "
            + "Relationship Lines, and they disappear."));

        if (settings.Editordata is not null)
        {
            Messages.Insert(0, new Note(
                NoteKind.Tip,
                "Surfaces are emitted double-sided on purpose.",
                "Assembled Camel model parts are mirrored planes, and culling their back faces would erase half "
                + "of each one. If you turn on backface culling in Blender, expect parts to vanish."));
        }

        if (settings.SetupAnim is null)
        {
            Messages.Insert(0, new Note(
                NoteKind.Tip,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{result.Counts.Meshes} meshes will overlap, and that is correct."),
                "Without a setup ANIM nothing places or hides the parts, so every alternate pose, facing and prop "
                + "variant sits on top of the others. Tick \"Pose with the setup ANIM\" to see one appearance."));
        }
    }

    private static string Shorten(string path) => path.Length <= 44 ? path : "…" + path[^43..];
}
