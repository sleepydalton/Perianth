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

    private CancellationTokenSource? _running;
    private CharacterAssets? _assets;
    private ImmutableArray<SdfPathEntry> _paths = [];
    private string? _archiveRoot;
    private string? _working;
    private string _status = "Choose a model, then a working folder.";
    private string _progress = string.Empty;
    private bool _busy;
    private bool _pose = true;
    private bool _materials = true;
    private bool _staged = true;
    private string? _modFolder;
    private ClipChoice? _clip;
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

    /// <summary>The clips this model can play, with "None" first.</summary>
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

    /// <summary>The clip to play, or none for a still.</summary>
    public ClipChoice? Clip
    {
        get => _clip;
        set
        {
            if (Set(ref _clip, value))
            {
                Raise(nameof(HasClip));
                Raise(nameof(PoseNote));
                Raise(nameof(PoseLabel));
                Describe();
            }
        }
    }

    public bool HasClip => _clip is not null && _clip.VirtualPath is not null;

    /// <summary>Emit the whole clip rather than one sampled pose.</summary>
    public bool Animate
    {
        get => _animate;
        set { if (Set(ref _animate, value)) { Describe(); } }
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

            return _clip is null
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
                    $"Using that mod, plus {waiting} unsaved {(waiting == 1 ? "edit" : "edits")} from the Textures tab.");
            }

            if (waiting > 0)
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"Using {waiting} unsaved {(waiting == 1 ? "edit" : "edits")} from the Textures tab.");
            }

            return _modFolder is not null
                ? "Using that mod folder."
                : "Nothing to apply: the Textures tab has no unsaved edits. Writing a mod clears them, so choose that mod folder here.";
        }
    }

    /// <summary>Remembers which archives the files come from.</summary>
    public void UseArchives(string archiveRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = archiveRoot;
        _paths = paths;
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
        Clips.Clear();
        Clips.Add(ClipChoice.None);
        foreach (ResolvedAsset clip in assets.Clips)
        {
            Clips.Add(ClipChoice.For(clip.VirtualPath, assets.Name));
        }

        _clip = Clips[0];
        Raise(nameof(Clip));
        Raise(nameof(HasClip));

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
                $"Ready: {_assets.Paths().Length} files to {ExtractedFolder}/, then a GLB in {ExportsFolder}/.");
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
    /// <returns>False when something was named and could not be read.</returns>
    internal bool ApplyOwnFiles(string working)
    {
        if (!_staged)
        {
            return true;
        }

        string extracted = Path.Combine(working, ExtractedFolder);
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
        string extracted = Path.Combine(working, ExtractedFolder);

        string Local(string virtualPath) =>
            Path.Combine(extracted, virtualPath.Replace('/', Path.DirectorySeparatorChar));

        string name = assets.Model[(assets.Model.LastIndexOf('/') + 1)..];
        string stem = name[..name.LastIndexOf('.')];

        // A prop has no setup ANIM — the convention is a character one, and no
        // prop in the archive has one — so the animation chosen in the list is
        // what poses it. That is exactly what the command line does when given
        // --setup-anim <that idle>: prp_aframe_sign_citywok goes from 25 parts
        // with every state overlaid to the 9 that are the standing sign.
        string? pose = assets.Setup?.VirtualPath ?? _clip?.VirtualPath;
        bool posed = _pose && pose is not null;

        // Only a model with its own setup takes a second animation as a clip.
        // Where the chosen animation *is* the pose, passing it twice would ask
        // for a clip against itself.
        string? clip = posed && assets.Setup is not null && _clip?.VirtualPath is string chosen
            ? Local(chosen)
            : null;

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
            ClipAnim = clip,
            Animate = clip is not null && _animate,
            Editordata = _materials && assets.Editordata is not null ? Local(assets.Editordata) : null,
            ContentRoot = _materials && assets.Editordata is not null ? extracted : null,
            SdfRoot = _materials && assets.Editordata is not null ? _archiveRoot : null,
            AllowUnposed = !posed,
            // Lip sync drives the mouth from the schedule, so it needs the
            // atlas and forbids a fixed state - the two would contradict.
            MouthAnim = Speaking(assets, posed) ? Local(assets.Mouth!.VirtualPath) : Atlas(assets.Mouth, Facial[0]),
            MouthState = Speaking(assets, posed) ? null : State(Facial[0]),
            EyesAnim = Atlas(assets.Eyes, Facial[1]) ?? BlinkAtlas(assets, posed, extracted),
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
                ? Path.Combine(extracted, "camel", "voice")
                : null,
            VgmstreamCli = Speaking(assets, posed) && _audio ? _vgmstream : null,
        };
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
    private string? BlinkAtlas(CharacterAssets assets, bool posed, string extracted)
    {
        if (!posed || assets.Eyes is null || ParseBlinks().Length == 0)
        {
            return null;
        }

        return Path.Combine(extracted, assets.Eyes.VirtualPath.Replace('/', Path.DirectorySeparatorChar));
    }

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
    private string Named(string stem)
    {
        List<string> parts = [stem];

        if (_clip?.VirtualPath is not null)
        {
            parts.Add(_clip.Label.Replace(' ', '_'));
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
            if (!await ExtractAsync(mine).ConfigureAwait(true))
            {
                return;
            }

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
    private async Task<bool> ExtractAsync(CancellationTokenSource mine)
    {
        CharacterAssets assets = _assets!;
        string working = _working!;
        string archives = _archiveRoot!;
        ImmutableArray<SdfPathEntry> paths = _paths;

        // The voice file is not part of the character's set - nothing in a
        // character's files names a speech ID - so it is added by the request
        // that asked for it.
        ImmutableArray<string> wanted = SpeechWem() is string wem
            ? assets.Paths().Add(wem)
            : assets.Paths();

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

            if (!await ExtractAsync(mine).ConfigureAwait(true))
            {
                return;
            }

            int extractedCount = _extracted;

            if (!exportAfterwards)
            {
                Status = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Extracted {extractedCount} files to {ExtractedFolder}/.");
                return;
            }

            Say(NoteKind.Done, string.Create(
                CultureInfo.InvariantCulture,
                $"Extracted {extractedCount} files to {ExtractedFolder}/."));

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
