using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perianth.Core.Content;
using Perianth.Core.Imaging;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Formats.Sdf;
using Perianth.Pipeline;

namespace Perianth.Gui;

/// <summary>
/// Dressing the main character: a piece per slot, and a colour per piece.
/// </summary>
/// <remarks>
/// <para>
/// The thing the equipment work was for. Everything under it was already true —
/// equipment shares the main character's hierarchy, so a piece posed by the
/// character's own setup lands exactly where the character is, and several
/// models merge into one file — but none of it was reachable without knowing
/// which files to name.
/// </para>
/// <para>
/// <b>The main character only.</b> All 1,196 equipment models are named by that
/// one hierarchy, which is what makes this work and also what makes it specific:
/// hanging a costume on another character would pose it by a hierarchy that was
/// never measured against it. The pane says so rather than appearing empty.
/// </para>
/// <para>
/// Colours are a repoint of the piece's own editordata, staged as an overlay the
/// export reads. Each piece is its own file, so recolouring one cannot reach
/// another, and the export sees an ordinary content root rather than anything
/// this pane invented.
/// </para>
/// </remarks>
public sealed class CostumeViewModel : ViewModelBase
{
    /// <summary>The model this pane dresses.</summary>
    private const string MainCharacter = "chr_maincharacter";

    private ImmutableArray<SdfPathEntry> _paths;
    private ImmutableArray<CostumeItem> _catalogue;
    private ImmutableArray<PaperSwatch> _palette;
    private ImmutableArray<TintColour> _tints;
    private string? _archiveRoot;
    private string? _model;
    private string _status = "Choose the main character to dress it.";
    private bool _busy;

    /// <summary>Where something can be worn, each with what is worn there.</summary>
    public ObservableCollection<CostumeSlot> Slots { get; } = [];

    /// <summary>Whether this pane applies to the model that is selected.</summary>
    public bool Applies => _model is not null;

    /// <summary>What the pane is doing, or why it is not.</summary>
    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True while the catalogue is being read.</summary>
    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    /// <summary>How many pieces are being worn, for the export pane to say.</summary>
    public int WornCount => Slots.Count(slot => slot.IsWorn);

    /// <summary>Raised when what is worn changes, so the export pane can say so.</summary>
    public event Action? WornChanged;

    /// <summary>Takes the archives, which is where the item list lives.</summary>
    public void UseArchives(string archiveRoot, ImmutableArray<SdfPathEntry> paths)
    {
        _archiveRoot = archiveRoot;
        _paths = paths;
        _catalogue = default;
        _palette = default;
        _tints = default;
    }

    /// <summary>Takes the model the middle pane resolved.</summary>
    public void Show(CharacterAssets assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        string stem = Path.GetFileNameWithoutExtension(assets.Model);
        bool applies = string.Equals(stem, MainCharacter, StringComparison.OrdinalIgnoreCase);

        _model = applies ? assets.Model : null;
        Slots.Clear();
        Raise(nameof(Applies));
        Raise(nameof(WornCount));

        if (!applies)
        {
            // Said plainly rather than shown empty. Equipment is measured against
            // one hierarchy, and quietly offering it here would produce a costume
            // posed by a hierarchy nobody checked it against.
            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"Costumes can only be added to the main character. This is '{stem}'.");
            return;
        }

        _ = LoadAsync();
    }

    /// <summary>The models being worn, for the export to draw alongside.</summary>
    /// <remarks>
    /// Through <see cref="CostumeCatalogue.Wear"/> rather than by listing what
    /// each slot holds, because one entry can be drawn several ways and only
    /// one of them belongs in the file.
    /// </remarks>
    public ImmutableArray<WornModel> WornModels =>
    [
        .. CostumeCatalogue.Wear(Slots.Where(slot => slot.IsWorn)
                .Select(slot => new CostumeCatalogue.CostumeWorn(slot.Chosen!.Item!, slot.Variant?.Piece)))
            .Select(drawn => new WornModel(drawn.ModelPath, drawn.Replaces)),
    ];

    /// <summary>
    /// Writes the recoloured editordata for every piece whose colour changed,
    /// and takes back the ones it wrote last time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Into the same overlay the texture pane writes to, so the export reads one
    /// content root and knows nothing about where its contents came from. A
    /// piece with no colour changed writes nothing.
    /// </para>
    /// <para>
    /// <b>A colour here is derived from what is selected now, not authored.</b>
    /// So it must not outlive the selection: left in place, a colour chosen once
    /// is applied to every export afterwards, including in later sessions, and
    /// the piece comes out wrong with nothing on screen saying why. That
    /// happened — a costume recoloured one evening was still recolouring it the
    /// next day.
    /// </para>
    /// <para>
    /// Which files were ours is recorded rather than inferred, because the pane
    /// shares the overlay with hand-authored files that must survive. Deleting
    /// by any rule about paths would eventually delete somebody's work.
    /// </para>
    /// </remarks>
    public Result<int> OverlayInto(string root)
    {
        ArgumentNullException.ThrowIfNull(root);

        Result<int> cleared = OverlayLedger.Withdraw(root, Ledger);
        if (cleared.IsRefused)
        {
            return cleared.Refusal;
        }

        if (_archiveRoot is null)
        {
            return Result.Ok(0);
        }

        using ContentSources content = new(contentRoot: null, sdfRoot: _archiveRoot);
        List<string> ours = [];
        int written = 0;

        foreach (CostumeSlot slot in Slots)
        {
            if (slot.Chosen?.Item is not CostumeItem item)
            {
                continue;
            }

            List<(string From, string To)> changes =
            [
                .. slot.Colours
                    .Where(colour => colour.Chosen is not null &&
                        !string.Equals(colour.Chosen.Swatch.TexturePath, colour.TexturePath, StringComparison.Ordinal))
                    .Select(colour => (colour.TexturePath, colour.Chosen!.Swatch.TexturePath)),
            ];

            // The colour the item itself ships with, which for a hairstyle is
            // the hair colour and for almost everything else is nothing at all.
            TintColour? shipped = null;
            foreach (string uid in item.Tints)
            {
                shipped ??= PaperPalette.Tint(_tints, uid);
            }

            if (changes.Count == 0 && shipped is null)
            {
                continue;
            }

            Result<int> one = Recolour(content, item, changes, shipped, root, ours);
            if (!one.TryGetValue(out int count, out Refusal? refusal))
            {
                return refusal;
            }

            written += count;
        }

        Result<int> recorded = OverlayLedger.Record(root, Ledger, ours);
        return recorded.IsRefused ? recorded.Refusal : Result.Ok(written);
    }

    /// <summary>The untinted white a paper carries before a colour is put on it.</summary>
    private static readonly Rgb White = new(1.0, 1.0, 1.0);

    /// <summary>Where the pane records which overlay files are its own.</summary>
    /// <remarks>
    /// A plain list of archive paths, one per line. It stands at no archive
    /// path of its own, so an export reading the overlay never sees it.
    /// </remarks>
    private const string Ledger = "perianth-costume-colours.txt";

    /// <summary>
    /// Recolours every way the piece can be drawn, which for a hairstyle is six
    /// models.
    /// </summary>
    /// <remarks>
    /// All of them, not only the one being exported, so that changing which
    /// variant is drawn does not silently drop the colour with it. A variant
    /// need not carry every paper the others do, so each takes only the changes
    /// it actually binds and one binding none is left alone. Handing every
    /// change to every model would instead refuse, because an edit that matched
    /// nothing is how a mistyped path is caught.
    /// </remarks>
    private static Result<int> Recolour(
        ContentSources content, CostumeItem item, List<(string From, string To)> changes,
        TintColour? shipped, string root, List<string> ours)
    {
        int written = 0;
        foreach (CostumePiece piece in item.Variants)
        {
            Result<int> one = RecolourOne(content, item, piece, changes, shipped, root, ours);
            if (!one.TryGetValue(out int count, out Refusal? refusal))
            {
                return refusal;
            }

            written += count;
        }

        return Result.Ok(written);
    }

    private static Result<int> RecolourOne(
        ContentSources content,
        CostumeItem item,
        CostumePiece piece,
        List<(string From, string To)> changes,
        TintColour? shipped,
        string root,
        List<string> ours)
    {
        string editordataPath = Path.ChangeExtension(piece.ModelPath, ".editordata");

        Result<byte[]?> read = content.Read(editordataPath);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"'{item.Name}' cannot be recoloured — {editordataPath} is not in the archives."));
        }

        Result<EditordataFile> parsed = EditordataReader.Read(SourceFile.FromMemory(editordataPath, bytes));
        if (!parsed.TryGetValue(out EditordataFile? file, out Refusal? badFile) || file is null)
        {
            return badFile ?? Refusal.Malformed($"{editordataPath} could not be read.");
        }

        var bound = MaterialTextures.List(file, item.Name)
            .Select(texture => texture.Path)
            .ToHashSet(StringComparer.Ordinal);

        int applied = 0;
        foreach ((string from, string to) in changes)
        {
            if (!bound.Contains(from))
            {
                continue;
            }

            Result<MaterialEditOutcome> edited = MaterialEdit.Repoint(file, from, to);
            if (!edited.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? editRefusal) || outcome is null)
            {
                return editRefusal ?? Refusal.Malformed("The recolour produced nothing.");
            }

            file = outcome.File;
            applied++;
        }

        // The colour the item ships with, applied to the paper it is drawn on
        // and to nothing else. Selected by tint rather than by texture: a
        // hairstyle's sheet is near-white and takes its colour entirely from
        // here, while the ink line work over it is tinted black and must stay
        // black. Retinting everything flattens the drawing — that is what the
        // `replacing` argument is for, and Roadmap §6.14 measured why.
        if (shipped is not null)
        {
            Rgb colour = new(shipped.Red / 255.0, shipped.Green / 255.0, shipped.Blue / 255.0);
            foreach (string texture in bound)
            {
                Result<MaterialEditOutcome> tinted = MaterialEdit.Retint(file, texture, White, colour);
                if (!tinted.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? tintRefusal))
                {
                    // Nothing on this texture carries the untinted white, which
                    // is ordinary: a variant need not use every sheet. Only a
                    // real fault is worth stopping for.
                    if (!string.Equals(tintRefusal.DiagnosticId, DiagnosticIds.MaterialEditMatchedNothing, StringComparison.Ordinal))
                    {
                        return tintRefusal;
                    }

                    continue;
                }

                file = outcome!.File;
                applied++;
            }
        }

        if (applied == 0)
        {
            return Result.Ok(0);
        }

        Result<byte[]> written = EditordataWriter.Write(file);
        if (!written.TryGetValue(out byte[]? output, out Refusal? writeRefusal))
        {
            return writeRefusal;
        }

        string destination = Path.Combine(root, editordataPath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{destination}' could not be created.");
        }

        Result<int> published = AtomicFile.Publish(destination, output);
        if (published.IsRefused)
        {
            return published.Refusal;
        }

        ours.Add(editordataPath);
        return Result.Ok(1);
    }

    private async Task LoadAsync()
    {
        if (_archiveRoot is null)
        {
            Status = "Open the game's archives to see what can be worn.";
            return;
        }

        Busy = true;
        Status = "Reading what can be worn…";

        string root = _archiveRoot;
        ImmutableArray<SdfPathEntry> paths = _paths;

        (ImmutableArray<CostumeItem> items, ImmutableArray<PaperSwatch> palette,
         ImmutableArray<TintColour> tints) = await Task.Run(() =>
        {
            using ContentSources content = new(contentRoot: null, sdfRoot: root);
            Result<ImmutableArray<CostumeItem>> catalogue = CostumeCatalogue.Read(content, paths);
            Result<ImmutableArray<PaperSwatch>> colours = PaperPalette.Read(content, paths);
            Result<ImmutableArray<TintColour>> table = PaperPalette.Tints(content);

            return (
                catalogue.TryGetValue(out ImmutableArray<CostumeItem> read, out _) ? read : [],
                colours.TryGetValue(out ImmutableArray<PaperSwatch> swatches, out _) ? swatches : [],
                table.TryGetValue(out ImmutableArray<TintColour> tints, out _) ? tints : []);
        }).ConfigureAwait(true);

        _catalogue = items;
        _palette = palette;
        _tints = tints;
        Busy = false;

        if (items.IsEmpty)
        {
            Status = "Nothing to choose from — the archives hold no item list.";
            return;
        }

        Slots.Clear();
        foreach (string name in CostumeCatalogue.Slots(items))
        {
            CostumeSlot slot = new(name);
            foreach (CostumeItem item in items.Where(i => string.Equals(i.Slot, name, StringComparison.Ordinal)))
            {
                slot.Items.Add(new CostumeChoice(item));
            }

            slot.Changed += OnSlotChanged;
            Slots.Add(slot);
        }

        Status = string.Create(
            CultureInfo.InvariantCulture,
            $"{items.Length} pieces across {Slots.Count} slots. Choose what to wear, then export.");
    }

    private void OnSlotChanged(CostumeSlot slot)
    {
        TakeOffTheOtherOutfit(slot);
        Raise(nameof(WornCount));
        WornChanged?.Invoke();
        if (slot.Chosen?.Item is not null)
        {
            _ = ColoursAsync(slot);
        }
    }

    /// <summary>
    /// Clears whatever belongs to an outfit other than the one just chosen from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The character has three outfits and wears one — see
    /// <see cref="CostumeCatalogue.Outfit"/>. This pane offers all fourteen
    /// slots at once, so it will happily put a hero body over a street body and
    /// a backstory pair of hands over both, and each of those replaces the
    /// character's own parts where it draws. The result is two suits in one
    /// place: an extra pair of hands where the arm ends, and half an outfit
    /// hidden inside the other, which is exactly what was reported.
    /// </para>
    /// <para>
    /// <b>Done here rather than refused at export</b>, because this is what the
    /// game does when a costume is equipped, and because a refusal arriving at
    /// the end of a dressing session cannot say which piece was the mistake.
    /// The cleared slots change in front of the reader and the status line names
    /// them, so nothing is dropped silently.
    /// </para>
    /// </remarks>
    private void TakeOffTheOtherOutfit(CostumeSlot slot)
    {
        if (slot.Chosen?.Item is not CostumeItem chosen || !chosen.IsExclusive)
        {
            return;
        }

        List<string> removed = [];
        foreach (CostumeSlot other in Slots)
        {
            if (ReferenceEquals(other, slot) ||
                other.Chosen?.Item is not CostumeItem worn ||
                !worn.IsExclusive ||
                string.Equals(worn.Outfit, chosen.Outfit, StringComparison.Ordinal))
            {
                continue;
            }

            removed.Add(worn.Name);
            other.Chosen = other.Nothing;
        }

        if (removed.Count > 0)
        {
            Status = string.Create(
                CultureInfo.InvariantCulture,
                $"'{chosen.Name}' is part of the {chosen.Outfit} outfit, so {string.Join(", ", removed)} came off — a character wears one outfit at a time.");
        }
    }

    /// <summary>Reads the chosen piece's papers, and offers the palette for each.</summary>
    private async Task ColoursAsync(CostumeSlot slot)
    {
        if (_archiveRoot is null || slot.Chosen?.Item is not CostumeItem item || _palette.IsDefaultOrEmpty)
        {
            return;
        }

        string root = _archiveRoot;
        ImmutableArray<PaperSwatch> palette = _palette;

        ImmutableArray<(string Path, int Sections, PaperSwatch Swatch)> found = await Task.Run(() =>
        {
            using ContentSources content = new(contentRoot: null, sdfRoot: root);

            // Summed across every model the piece draws. A hairstyle is six
            // models sharing one set of papers, and reading only the first
            // would offer the colours of whichever region happened to come
            // first while recolouring all six.
            Dictionary<string, int> bindings = new(StringComparer.Ordinal);
            foreach (CostumePiece part in item.Variants)
            {
                string editordata = Path.ChangeExtension(part.ModelPath, ".editordata");

                Result<byte[]?> read = content.Read(editordata);
                if (!read.TryGetValue(out byte[]? bytes, out _) || bytes is null)
                {
                    continue;
                }

                Result<EditordataFile> parsed = EditordataReader.Read(SourceFile.FromMemory(editordata, bytes));
                if (!parsed.TryGetValue(out EditordataFile? file, out _))
                {
                    continue;
                }

                foreach (TextureReference texture in MaterialTextures.List(file, item.Name))
                {
                    bindings[texture.Path] = bindings.GetValueOrDefault(texture.Path) + texture.Bindings;
                }
            }

            // Ordered by how much of the piece wears each colour: 244 sections
            // against 1 is the garment against a scrap of trim. Ties break on
            // the path, so the list is the same every time it is read.
            return ImmutableArray.CreateRange(
                bindings
                    .Select(row => (row.Key, row.Value, Swatch: PaperPalette.Match(palette, row.Key)))
                    .Where(row => row.Swatch is not null)
                    .OrderByDescending(row => row.Value)
                    .ThenBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => (row.Key, row.Value, row.Swatch!)));
        }).ConfigureAwait(true);

        // The choice may have moved on while this ran.
        if (slot.Chosen?.Item != item)
        {
            return;
        }

        slot.Colours.Clear();
        int total = found.Sum(row => row.Sections);
        foreach ((string path, int sections, PaperSwatch swatch) in found)
        {
            CostumeColour colour = new(path, sections, swatch) { Total = total };
            foreach (PaperSwatch option in palette)
            {
                colour.Swatches.Add(new SwatchChoice(
                    option, string.Equals(option.Name, swatch.Name, StringComparison.Ordinal)));
            }

            slot.Colours.Add(colour);
        }

        slot.ColoursArrived();
    }
}
