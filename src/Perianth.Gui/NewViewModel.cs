using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Perianth.Formats.Sdf;
using Perianth.Pipeline;

namespace Perianth.Gui;

/// <summary>
/// Making something the game did not have: a costume piece, a prop or a
/// character, from a mesh made elsewhere.
/// </summary>
/// <remarks>
/// <para>
/// Every other pane starts from a file already chosen on the left. This one
/// starts from the author's own work, so it asks four things and finds the rest:
/// the mesh, what it is like, what to call it, and — for a costume piece — how
/// the player comes by it.
/// </para>
/// <para>
/// <b>"What it is like" is one choice that resolves several files.</b> Picking a
/// hat gives the declaration to copy and the model to lay the mesh over; picking
/// a prop gives its graph object and through that its model; picking a character
/// gives both. Nothing here asks for a path, because knowing which path to type
/// is exactly what the window exists to spare somebody.
/// </para>
/// <para>
/// The lists are read once per kind on a background thread and searched in
/// memory. They are large — 408 costume entries, 7,871 props, 1,824 characters —
/// so the pane shows the first
/// <see cref="Shown"/> matches rather than all of them.
/// </para>
/// </remarks>
public sealed class NewViewModel : ViewModelBase
{
    /// <summary>How many matches to put on screen at once.</summary>
    private const int Shown = 200;

    /// <summary>The file listing what the player starts with.</summary>
    private const string StartingInventory =
        "camel/game system data/juice/items/starting_inventory.juice";

    /// <summary>Where shops and loot tables are declared.</summary>
    private const string ItemFolder = "camel/game system data/juice/items/";

    private const string LootFolder = "camel/game system data/juice/loot/";

    /// <summary>The setting a costume piece most obviously belongs in.</summary>
    private const string CostumeSettings = "CostumeSettings";

    private readonly Dictionary<string, ImmutableArray<NewTemplate>> _loaded = new(StringComparer.Ordinal);

    private ImmutableArray<SdfPathEntry> _paths;
    private string? _archiveRoot;
    private string _kind = "Costume piece";
    private string _search = string.Empty;
    private NewTemplate? _chosen;
    private string? _glb;
    private string _name = string.Empty;
    private string _obtain = NotObtainable;
    private ImmutableArray<NewTemplate> _places;
    private string _placeSearch = string.Empty;
    private NewTemplate? _place;
    private string _gameState = "Day_1";
    private double _chance = 1.0;
    private bool _ownUv0;
    private int _least = 1;
    private int _most = 1;
    private double _x;
    private double _y;
    private double _z;
    private string _status = "Open the game's archives on the left to begin.";
    private string _saved = string.Empty;
    private bool _busy;

    public NewViewModel()
    {
        ChooseMeshCommand = new RelayCommand(() => ChooseMeshRequested?.Invoke());
        SaveCommand = new RelayCommand(() => SaveRequested?.Invoke());
    }

    /// <summary>Asks the window for the author's GLB.</summary>
    public event Action? ChooseMeshRequested;

    /// <summary>Asks the window where to put the mod.</summary>
    public event Action? SaveRequested;

    public RelayCommand ChooseMeshCommand { get; }

    public RelayCommand SaveCommand { get; }

    /// <summary>The matches for what is in the search box.</summary>
    public ObservableCollection<NewTemplate> Candidates { get; } = [];

    /// <summary>What the author should know about what was just written.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>The label for making nothing give it to the player.</summary>
    private const string NotObtainable = "No way to get it yet";

    private const string FromInventory = "Already in the player's inventory when a new game starts";

    private const string FromShop = "Sold in a shop";

    private const string FromLoot = "Dropped as loot";

    /// <summary>
    /// The ways the game hands something over, named by what happens rather than
    /// by what the file is called.
    /// </summary>
    /// <remarks>
    /// All three the game has, plus doing nothing. Crafting is the fourth and is
    /// not offered here: a recipe names a <em>second</em> item — the recipe the
    /// player holds — which needs a way to be got hold of in its own right, so
    /// it is two new items rather than one. <c>perianth item --craft</c> does it.
    /// </remarks>
    public ImmutableArray<string> ObtainOptions { get; } =
        [NotObtainable, FromInventory, FromShop, FromLoot];

    /// <summary>The points in the story a shop can start stocking something.</summary>
    public ImmutableArray<string> GameStates { get; } = ItemEdit.GameStates;

    /// <summary>The three things this can make.</summary>
    public ImmutableArray<string> Kinds { get; } =
        ["Costume piece", "Prop", "Character"];

    /// <summary>Which one is being made.</summary>
    public string Kind
    {
        get => _kind;
        set
        {
            if (Set(ref _kind, value))
            {
                _chosen = null;
                Raise(nameof(Chosen));
                Raise(nameof(IsCostume));
                Raise(nameof(IsProp));
                Raise(nameof(Caveat));
                Raise(nameof(Ready));
                _ = LoadAsync();
            }
        }
    }

    /// <summary>Whether the costume-only fields apply.</summary>
    public bool IsCostume => string.Equals(_kind, "Costume piece", StringComparison.Ordinal);

    /// <summary>Whether the prop-only fields apply.</summary>
    public bool IsProp => string.Equals(_kind, "Prop", StringComparison.Ordinal);

    /// <summary>What is not known to work about the kind being made.</summary>
    /// <remarks>
    /// One sentence per kind rather than one paragraph over all three. The
    /// paragraph said the game has never been seen to load a file it did not
    /// ship, and that is wrong twice over: the model, its materials and a graph
    /// object each go to a path of their own, and a reference by path resolves
    /// to a new path — proven in game (Roadmap §10.110). Saying otherwise talks
    /// an author out of work that would have worked.
    ///
    /// What is unproven is the declaration, and it differs: an item and a
    /// character are found by listing a folder, which nothing has confirmed a
    /// mod can add to; a graph object is named by path and is on the proven
    /// side, so what is open for a prop is placing it — which has twice been
    /// installed and honoured by nothing, and is said here as well as in the
    /// note the save leaves, because this one is read before the effort is spent
    /// rather than after.
    ///
    /// None of that mechanism reaches the author, and neither does the half
    /// that works: saying the model and its materials load fine answers a doubt
    /// nobody has, and it buried the sentence that matters. What is left is the
    /// caveat itself.
    ///
    /// <b>"May not yet work" rather than "does not".</b> A new item has never
    /// been seen to work and has never been seen to fail either — the probe
    /// built to settle it returned no verdict on any arm. The mechanism predicts
    /// failure and that is a reading, so the wording claims no more than that.
    /// A prop is the other case and says so plainly: two installs, two layers
    /// that drew nothing.
    /// </remarks>
    public string Caveat => _kind switch
    {
        "Prop" =>
            "Placing a new prop in the world currently has issues where the layer will not draw. "
            + "Keep a backup of the map you edit.",
        "Character" =>
            "Adding a new character to the game's existing list may not yet work.",
        _ =>
            "Adding a new item to the game's existing list may not yet work. Changing an item the "
            + "game already has works.",
    };

    /// <summary>What to search the list for.</summary>
    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Filter();
            }
        }
    }

    /// <summary>The template the new thing is based on.</summary>
    public NewTemplate? Chosen
    {
        get => _chosen;
        set
        {
            if (Set(ref _chosen, value))
            {
                Raise(nameof(Ready));

                // A prop starts where the one it copies stands, so somebody who
                // wants it near that thing has to change nothing, and somebody
                // who wants it elsewhere has a number to work from rather than
                // an empty box and a map with no visible origin.
                if (value is not null && IsProp)
                {
                    X = value.X;
                    Y = value.Y;
                    Z = value.Z;
                }
            }
        }
    }

    /// <summary>The author's mesh, as they will recognise it.</summary>
    public string Mesh => _glb is null ? "No mesh chosen." : Path.GetFileName(_glb);

    /// <summary>What to call it.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                Raise(nameof(Ready));
                Raise(nameof(Filenames));
            }
        }
    }

    /// <summary>The name the files will actually take, shown as it is typed.</summary>
    /// <remarks>
    /// Said rather than left to surprise. The name a person types is what the
    /// game shows; the files need a plainer version of it, and seeing the two
    /// side by side answers "where did my file go" before it is asked.
    /// </remarks>
    public string Filenames => _name.Length == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"Files will be named {NewAsset.Stem(_name)}");

    /// <summary>How the player comes by a costume piece.</summary>
    public string Obtain
    {
        get => _obtain;
        set
        {
            if (Set(ref _obtain, value))
            {
                Raise(nameof(NeedsPlace));
                Raise(nameof(NeedsGameState));
                Raise(nameof(NeedsLoot));
                Raise(nameof(PlaceLabel));
                Raise(nameof(Ready));
                _place = null;
                Raise(nameof(Place));
                _ = LoadPlacesAsync();
            }
        }
    }

    /// <summary>Whether this route needs somewhere naming.</summary>
    public bool NeedsPlace => !string.Equals(_obtain, NotObtainable, StringComparison.Ordinal);

    /// <summary>Whether a point in the story has to be chosen too.</summary>
    public bool NeedsGameState => string.Equals(_obtain, FromShop, StringComparison.Ordinal);

    /// <summary>Whether the how-likely and how-many boxes apply.</summary>
    public bool NeedsLoot => string.Equals(_obtain, FromLoot, StringComparison.Ordinal);

    /// <summary>What the second choice is asking for, in the route's own terms.</summary>
    public string PlaceLabel => _obtain switch
    {
        FromShop => "Which shop",
        FromLoot => "Which loot table",
        FromInventory => "Which starting-inventory list",
        _ => string.Empty,
    };

    /// <summary>The shops, tables or lists matching the search.</summary>
    public ObservableCollection<NewTemplate> Places { get; } = [];

    /// <summary>What to search those for.</summary>
    public string PlaceSearch
    {
        get => _placeSearch;
        set
        {
            if (Set(ref _placeSearch, value))
            {
                FilterPlaces();
            }
        }
    }

    /// <summary>The shop, table or list chosen.</summary>
    public NewTemplate? Place
    {
        get => _place;
        set
        {
            if (Set(ref _place, value))
            {
                Raise(nameof(Ready));
            }
        }
    }

    /// <summary>From which point in the story a shop stocks it.</summary>
    public string GameState
    {
        get => _gameState;
        set => Set(ref _gameState, value);
    }

    /// <summary>How likely a drop is, from just above 0 to 1.</summary>
    public double Chance { get => _chance; set => Set(ref _chance, value); }

    /// <summary>The fewest dropped at once.</summary>
    public int Least { get => _least; set => Set(ref _least, value); }

    /// <summary>The most dropped at once.</summary>
    public int Most { get => _most; set => Set(ref _most, value); }

    /// <summary>Where a prop stands.</summary>
    public double X { get => _x; set => Set(ref _x, value); }

    /// <summary>Where a prop stands.</summary>
    public double Y { get => _y; set => Set(ref _y, value); }

    /// <summary>Where a prop stands.</summary>
    public double Z { get => _z; set => Set(ref _z, value); }

    /// <summary>
    /// Whether the new thing should use the texture layout from the author's
    /// own file rather than one worked out from where its points sit.
    /// </summary>
    /// <remarks>
    /// Off by default, and worth offering here more than anywhere: this pane is
    /// where a mesh made from scratch arrives, and a mesh made from scratch is
    /// the one most likely to be solid rather than a flat cut-out.
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

    /// <summary>Where the mod went, once it has gone somewhere.</summary>
    public string Saved
    {
        get => _saved;
        private set => Set(ref _saved, value);
    }

    /// <summary>True while a list is being read.</summary>
    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>Whether there is enough to make something.</summary>
    /// <remarks>
    /// A route that names nowhere is not a route: an item added to no shop is
    /// added to no shop, and the save would refuse rather than the button being
    /// unavailable. Better to say what is missing by leaving it disabled.
    /// </remarks>
    public bool Ready => _archiveRoot is not null && _chosen is not null
        && _glb is not null && NewAsset.Stem(_name).Length > 0
        && (!IsCostume || !NeedsPlace || _place is not null);

    /// <summary>Takes the archives, which every list is read from.</summary>
    public void UseArchives(string archiveRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = archiveRoot;
        _paths = paths;
        _loaded.Clear();
        Chosen = null;
        Status = "Choose a mesh, and something for it to be based on.";
        _ = LoadAsync();
        _ = LoadPlacesAsync();
    }

    /// <summary>Takes the mesh the window asked for.</summary>
    public void UseMesh(string path)
    {
        _glb = path;
        Raise(nameof(Mesh));
        Raise(nameof(Ready));
    }

    /// <summary>Writes the mod, and says what went into it.</summary>
    public async Task SaveAsync(string destination, string modName)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(modName);

        if (!Ready)
        {
            return;
        }

        Busy = true;
        Saved = string.Empty;
        Notes.Clear();
        Status = "Making it…";

        Result<NewAssetOutcome> made = await Task.Run(Make).ConfigureAwait(true);

        if (!made.TryGetValue(out NewAssetOutcome? outcome, out Refusal? refusal))
        {
            Busy = false;
            Status = refusal.Message;
            return;
        }

        Result<ModOutcome> wrote = TextureMod.Write(
            destination,
            new ModDetails(modName, "unknown", "1.0.0", modName),
            outcome.Files);

        Busy = false;

        if (!wrote.TryGetValue(out ModOutcome? written, out Refusal? unwritten))
        {
            Status = unwritten.Message;
            return;
        }

        Saved = string.Create(
            CultureInfo.InvariantCulture,
            $"Saved to {written.Folder}. Copy that folder into FractureLoader/Mods/.");

        // The notes are as much the point of this pane as the files are: every
        // one names something the mod folder cannot show. Listed one to a line
        // rather than run together, because three sentences joined by spaces is
        // a paragraph nobody reads.
        Notes.Clear();
        foreach (Diagnostic note in outcome.Notes)
        {
            Notes.Add(note.Message);
        }

        Status = outcome.Summary;
    }

    private Result<NewAssetOutcome> Make()
    {
        using ContentSources content = new(null, _archiveRoot);

        byte[] glb;
        try
        {
            glb = File.ReadAllBytes(_glb!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{Path.GetFileName(_glb)}' could not be read.");
        }

        NewTemplate template = _chosen!;

        if (IsProp)
        {
            Result<byte[]?> layer = content.Read(template.Path);
            if (!layer.TryGetValue(out byte[]? bytes, out Refusal? refusal))
            {
                return refusal;
            }

            return bytes is null
                ? Refusal.Resource($"'{template.Path}' is not in the archives.")
                : NewAsset.Prop(
                    content, glb, _name, template.Path, bytes, template.Entity,
                    new PropPosition(_x, _y, _z), _ownUv0);
        }

        if (!IsCostume)
        {
            return NewAsset.Character(content, glb, _name, _name, template.Path, _ownUv0);
        }

        Result<ObtainRoute?> route = Route(content);
        return route.TryGetValue(out ObtainRoute? chosen, out Refusal? unrouted)
            ? NewAsset.CostumePiece(content, glb, _name, _name, template.Path, chosen, _ownUv0)
            : unrouted;
    }

    /// <summary>
    /// The route the save will take, as the boxes above describe it.
    /// </summary>
    private Result<ObtainRoute?> Route(ContentSources content)
    {
        if (!NeedsPlace || _place is null)
        {
            return Result.Ok<ObtainRoute?>(null);
        }

        Result<byte[]?> read = content.Read(_place.Path);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Resource($"'{_place.Path}' is not in the archives.");
        }

        ObtainKind kind = _obtain switch
        {
            FromShop => ObtainKind.Shop,
            FromLoot => ObtainKind.Loot,
            _ => ObtainKind.Inventory,
        };

        return Result.Ok<ObtainRoute?>(new ObtainRoute(
            _place.Path, bytes, _place.Name, kind, _gameState, _chance, _least, _most));
    }

    /// <summary>Reads the shops, tables or lists the chosen route can use.</summary>
    private async Task LoadPlacesAsync()
    {
        Places.Clear();
        _places = default;

        if (!NeedsPlace || _archiveRoot is null || _paths.IsDefaultOrEmpty)
        {
            return;
        }

        Busy = true;
        string route = _obtain;
        Result<ImmutableArray<NewTemplate>> read =
            await Task.Run(() => ReadPlaces(route)).ConfigureAwait(true);
        Busy = false;

        if (!read.TryGetValue(out ImmutableArray<NewTemplate> found, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        _places = found;
        FilterPlaces();

        // The obvious answer, chosen rather than left blank. A costume piece
        // granted at the start belongs in the costume list, and making somebody
        // find that among 55 is a question with one sensible answer.
        Place = found.FirstOrDefault(
            p => p.Name.Equals(CostumeSettings, StringComparison.Ordinal));
    }

    private void FilterPlaces()
    {
        Places.Clear();

        if (_places.IsDefaultOrEmpty)
        {
            return;
        }

        string word = _placeSearch.Trim();
        int shown = 0;

        foreach (NewTemplate place in _places)
        {
            if (word.Length > 0 && !place.Matches(word))
            {
                continue;
            }

            Places.Add(place);
            if (++shown == Shown)
            {
                break;
            }
        }
    }

    private Result<ImmutableArray<NewTemplate>> ReadPlaces(string route) => route switch
    {
        FromShop => Declarations(ItemFolder, ".mvendorconfig", "VendorConfig", "a shop"),
        FromLoot => Declarations(LootFolder, ".juice", "LootTable", "a loot table"),
        _ => Settings(),
    };

    /// <summary>The starting-inventory lists, which are nested one level down.</summary>
    private Result<ImmutableArray<NewTemplate>> Settings()
    {
        using ContentSources content = new(null, _archiveRoot);

        Result<byte[]?> read = content.Read(StartingInventory);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Resource("The archives hold no starting inventory.");
        }

        ImmutableArray<NewTemplate>.Builder found = ImmutableArray.CreateBuilder<NewTemplate>();
        foreach (string name in Named(
                     System.Text.Encoding.Latin1.GetString(bytes), "StartingInventorySetting"))
        {
            found.Add(new NewTemplate(name, "a starting-inventory list", StartingInventory));
        }

        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// Every declaration of a class, across the files of a folder.
    /// </summary>
    /// <remarks>
    /// Several files rather than one, because both populations are split: shops
    /// live in a base file and three DLC ones, and loot tables in a dozen. The
    /// file is carried with the name because the edit has to go back into the
    /// one the declaration came from.
    /// </remarks>
    private Result<ImmutableArray<NewTemplate>> Declarations(
        string folder, string extension, string declared, string detail)
    {
        using ContentSources content = new(null, _archiveRoot);

        ImmutableArray<NewTemplate>.Builder found = ImmutableArray.CreateBuilder<NewTemplate>();

        foreach (SdfPathEntry entry in _paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            if (!path.StartsWith(folder, StringComparison.Ordinal)
                || !path.EndsWith(extension, StringComparison.Ordinal))
            {
                continue;
            }

            Result<byte[]?> read = content.Read(path);
            if (!read.TryGetValue(out byte[]? bytes, out Refusal? _) || bytes is null)
            {
                continue;
            }

            foreach (string name in Named(System.Text.Encoding.Latin1.GetString(bytes), declared))
            {
                found.Add(new NewTemplate(name, detail, path));
            }
        }

        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// The names a class is declared under in a juice file.
    /// </summary>
    /// <remarks>
    /// A name may be quoted, because several shops have spaces in theirs. The
    /// quotes are taken off here: what the edit needs is the name, and
    /// <c>JuiceDocument</c> puts them back where the file wants them.
    /// </remarks>
    private static IEnumerable<string> Named(string text, string declared)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith(declared, StringComparison.Ordinal)
                || trimmed.Length <= declared.Length
                || trimmed[declared.Length] != ' ')
            {
                continue;
            }

            string rest = trimmed[(declared.Length + 1)..].TrimEnd('\r').Trim();
            if (rest.Length == 0)
            {
                continue;
            }

            if (rest[0] == '"')
            {
                int close = rest.IndexOf('"', 1);
                if (close > 0)
                {
                    yield return rest[1..close];
                }

                continue;
            }

            int space = rest.IndexOf(' ', StringComparison.Ordinal);
            yield return space < 0 ? rest : rest[..space];
        }
    }

    private async Task LoadAsync()
    {
        if (_archiveRoot is null || _paths.IsDefaultOrEmpty)
        {
            return;
        }

        if (_loaded.TryGetValue(_kind, out ImmutableArray<NewTemplate> already))
        {
            Show(already);
            return;
        }

        Busy = true;
        Status = "Reading the list…";

        string kind = _kind;
        Result<ImmutableArray<NewTemplate>> read = await Task.Run(() => Read(kind)).ConfigureAwait(true);

        Busy = false;

        if (!read.TryGetValue(out ImmutableArray<NewTemplate> found, out Refusal? refusal))
        {
            Status = refusal.Message;
            return;
        }

        _loaded[kind] = found;
        Show(found);
        Status = "Choose a mesh, and something for it to be based on.";
    }

    private void Show(ImmutableArray<NewTemplate> templates)
    {
        _all = templates;
        Filter();
    }

    private ImmutableArray<NewTemplate> _all;

    private void Filter()
    {
        Candidates.Clear();

        if (_all.IsDefaultOrEmpty)
        {
            return;
        }

        string word = _search.Trim();
        int shown = 0;

        foreach (NewTemplate template in _all)
        {
            if (word.Length > 0 && !template.Matches(word))
            {
                continue;
            }

            Candidates.Add(template);
            if (++shown == Shown)
            {
                break;
            }
        }
    }

    private Result<ImmutableArray<NewTemplate>> Read(string kind) => kind switch
    {
        "Prop" => Props(),
        "Character" => Characters(),
        _ => Costumes(),
    };

    /// <summary>
    /// The costume entries that can serve as a template.
    /// </summary>
    /// <remarks>
    /// Filtered to the 364 of 408 that are <b>one record carrying both a model
    /// and a name</b>. The rest are parent entries whose models belong to their
    /// variant records, so copying one produces a piece that cannot be renamed
    /// and does not draw what was meant — the "a record is not an entry" rule
    /// (Roadmap §10.37) reaching authoring. Leaving them out costs almost
    /// nothing: every slot is still represented.
    /// </remarks>
    private Result<ImmutableArray<NewTemplate>> Costumes()
    {
        using ContentSources content = new(null, _archiveRoot);

        Result<ImmutableArray<CostumeItem>> read = CostumeCatalogue.Read(content, _paths);
        if (!read.TryGetValue(out ImmutableArray<CostumeItem> items, out Refusal? refusal))
        {
            return refusal;
        }

        ImmutableArray<NewTemplate>.Builder found = ImmutableArray.CreateBuilder<NewTemplate>();

        foreach (CostumeItem item in items)
        {
            if (item.SourcePath.Length == 0 || !Usable(content, item.SourcePath))
            {
                continue;
            }

            found.Add(new NewTemplate(item.Name, item.Slot, item.SourcePath));
        }

        return Result.Ok(found.ToImmutable());
    }

    private static bool Usable(ContentSources content, string path)
    {
        Result<byte[]?> read = content.Read(path);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? _) || bytes is null)
        {
            return false;
        }

        Result<JuiceDocument> document = JuiceDocument.Read(SourceFile.FromMemory(path, bytes));
        return document.IsSuccess
            && document.Value.TryGetField("myModel", out JuiceField model) && !model.IsBlock
            && document.Value.TryGetField("myUIName", out JuiceField name) && !name.IsBlock;
    }

    /// <summary>Every prop standing on the map, with the layer it stands in.</summary>
    private Result<ImmutableArray<NewTemplate>> Props()
    {
        using ContentSources content = new(null, _archiveRoot);

        ImmutableArray<NewTemplate>.Builder found = ImmutableArray.CreateBuilder<NewTemplate>();
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (SdfPathEntry entry in _paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            if (!path.EndsWith(".mlayer", StringComparison.Ordinal))
            {
                continue;
            }

            Result<byte[]?> read = content.Read(path);
            if (!read.TryGetValue(out byte[]? bytes, out Refusal? _) || bytes is null)
            {
                continue;
            }

            Result<ImmutableArray<LayerEntity>> held =
                PropPlace.List(SourceFile.FromMemory(path, bytes));
            if (!held.IsSuccess)
            {
                continue;
            }

            // The layer's own header says where in the world it is, in words a
            // person wrote -- a slash-separated place, narrowing to the room.
            // The uid-shaped archive path says nothing, and "a prop on the map"
            // says less.
            string where = Where(System.Text.Encoding.Latin1.GetString(bytes));

            foreach (LayerEntity held2 in held.Value)
            {
                // Named by the same name twice across the map is common, and one
                // of them is as good a template as the other.
                if (held2.Type is not PropPlace.PropType
                    || held2.Resource is null
                    || !seen.Add(held2.Name))
                {
                    continue;
                }

                found.Add(new NewTemplate(
                    held2.Name, where, path, held2.Name,
                    held2.Stands.X, held2.Stands.Y, held2.Stands.Z));
            }
        }

        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// Where a layer is, as its own header describes it in words.
    /// </summary>
    private static string Where(string layer)
    {
        const string Anchor = "path = \"";
        int at = layer.IndexOf(Anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return "somewhere on the map";
        }

        int start = at + Anchor.Length;
        int end = layer.IndexOf('"', start);
        return end < 0 ? "somewhere on the map" : layer[start..end].Trim('/');
    }

    /// <summary>Every character that draws something.</summary>
    private Result<ImmutableArray<NewTemplate>> Characters()
    {
        using ContentSources content = new(null, _archiveRoot);

        ImmutableArray<NewTemplate>.Builder found = ImmutableArray.CreateBuilder<NewTemplate>();

        foreach (SdfPathEntry entry in _paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            if (!path.EndsWith(".mnpc", StringComparison.Ordinal))
            {
                continue;
            }

            Result<byte[]?> read = content.Read(path);
            if (!read.TryGetValue(out byte[]? bytes, out Refusal? _) || bytes is null)
            {
                continue;
            }

            Result<JuiceDocument> document =
                JuiceDocument.Read(SourceFile.FromMemory(path, bytes));

            // A definition with no graph object draws nothing — 181 of 1,824 —
            // so it is not a template for a character somebody can see.
            if (!document.IsSuccess
                || !document.Value.TryGetField("myGraphObjectFile", out JuiceField field)
                || field.IsBlock)
            {
                continue;
            }

            // An .mnpc's file name is not its declared name on 875 of 1,824,
            // so showing both is what lets somebody recognise the one they
            // meant.
            found.Add(new NewTemplate(
                document.Value.DeclaredName,
                System.IO.Path.GetFileNameWithoutExtension(path),
                path));
        }

        return Result.Ok(found.ToImmutable());
    }
}
