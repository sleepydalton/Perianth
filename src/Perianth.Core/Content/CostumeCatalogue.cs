using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// One model an item can be drawn as.
/// </summary>
/// <param name="Kind">
/// The record type without its <c>CostumeItem</c> prefix — <c>Head</c>,
/// <c>StreetHairBangs</c>. This is the vocabulary <c>myHideSlot</c> speaks.
/// </param>
/// <param name="ModelPath">The model to draw.</param>
public sealed record CostumePiece(string Kind, string ModelPath);

/// <summary>
/// One thing a character can wear, as the game's own item list describes it.
/// </summary>
/// <param name="Name">The name the game shows, or the item's id where it has none.</param>
/// <param name="Slot">Where it is worn, named as the game's own menu names it.</param>
/// <param name="Kind">The record type this entry is, for <see cref="Hides"/> to match.</param>
/// <param name="Variants">
/// The models it can be drawn as, of which <b>exactly one</b> is drawn. Almost
/// everything has one; a hairstyle has up to six.
/// </param>
/// <param name="Hides">The slots this piece covers up, and so removes.</param>
/// <param name="Tints">
/// The colours the item ships with, as ids into the game's tint table. Almost
/// always <c>NoTint</c>; for a hairstyle it is the hair colour, and without it
/// hair draws as the near-white sheet its texture actually is.
/// </param>
/// <param name="Outfit">
/// Which of the character's outfits this belongs to — <c>Hero</c>, <c>Street</c>
/// or <c>Backstory</c>, or <see cref="CostumeCatalogue.EveryOutfit"/> for a
/// piece worn with all of them. The game's own <c>myCostumeType</c>.
/// </param>
/// <param name="SourcePath">
/// The archive path of the file declaring this entry. Carried because authoring
/// needs it: making a new piece means copying a shipped declaration, and this is
/// the only thing that knows which file each entry came from.
/// </param>
public sealed record CostumeItem(
    string Name,
    string Slot,
    string Kind,
    ImmutableArray<CostumePiece> Variants,
    ImmutableArray<string> Hides,
    ImmutableArray<string> Tints,
    string Outfit = CostumeCatalogue.EveryOutfit,
    string SourcePath = "")
{
    /// <summary>Whether this belongs to one outfit rather than to all of them.</summary>
    public bool IsExclusive =>
        !string.Equals(Outfit, CostumeCatalogue.EveryOutfit, StringComparison.Ordinal);

    /// <summary>
    /// Whether wearing this takes the character's own parts off underneath.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The three garment slots do: a suit is worn <i>instead of</i> the
    /// character's clothes, and leaving both draws underwear through it. The
    /// other five are worn <b>on</b> the character and take nothing off —
    /// face paint sits on the face, it does not replace it.
    /// </para>
    /// <para>
    /// Measured, because the difference is invisible until something is worn:
    /// a facial-hair piece and a makeup piece each occupy the joint the
    /// character's own skull hangs from, and replacing there deletes the head.
    /// One makeup piece adds a single decal at the eye joint and was taking all
    /// five of the character's eye meshes with it.
    /// </para>
    /// </remarks>
    public bool Replaces =>
        Kind.EndsWith("Head", StringComparison.Ordinal) ||
        Kind.EndsWith("Body", StringComparison.Ordinal) ||
        Kind.EndsWith("Hands", StringComparison.Ordinal);

    /// <summary>The variant to draw when nothing says otherwise.</summary>
    /// <remarks>
    /// <c>Full</c> is the whole hairstyle and 80 of the 81 have one, so it is
    /// the answer whenever the head is bare — which is the only case this can
    /// settle from the files. The rest fall back to the first variant declared,
    /// which is the file's own order and so is the same on every run.
    /// </remarks>
    public CostumePiece Default =>
        Variants.FirstOrDefault(v => v.Kind.EndsWith("Full", StringComparison.Ordinal)) ?? Variants[0];
}

/// <summary>
/// What a character can wear, read from the game's item definitions.
/// </summary>
/// <remarks>
/// <para>
/// Equipment cannot be resolved from the models alone. Names do not identify a
/// wearer — most pieces are generic, like a numbered mask — and what rigs a
/// piece identifies nothing either, because every one of the 1,196 equipment
/// models is named by the main character's hierarchy. The answer is the item
/// list: 3,038 definitions under <c>game system data/juice/items</c>.
/// </para>
/// <para>
/// <b>A record is not an entry.</b> A hairstyle is one thing in the game's menu
/// and up to seven records in the files: a parent that owns the rest through
/// <c>myExtraPieces</c>, and one child per way of drawing it — bangs, full,
/// high, low, skull, top. The parent is the entry and the children are its
/// variants. Taking every record that names a model instead offers six
/// hairstyles where the game offers one, and hides the 42 parents that own
/// variants without naming a model themselves. A record referenced as
/// somebody's variant is therefore never an entry of its own, which is
/// structural rather than a list of type names to keep current.
/// </para>
/// <para>
/// <b>The variants are alternatives and exactly one is drawn.</b> Rendered
/// together they are nested silhouettes of one hairstyle at decreasing size,
/// sharing not one mesh; drawn at once they are six hairstyles on top of each
/// other, which is what a costume looked like when this was read as regions
/// worn together.
/// </para>
/// <para>
/// <b>Which one goes under a given headpiece is in the schema, not the item.</b>
/// <c>items.fruit</c> gives <c>CostumeItemHead</c> a default that hides all
/// seven hair categories, plus eyewear and facial hair. A record's
/// <c>myHideSlot</c> rows are <b>overrides of those defaults by slot number</b>,
/// so <c>myHideSlot5 None</c> means "this one does not hide what slot 5 usually
/// hides". Read as a plain list, which is exactly what they look like, the
/// polarity is backwards: a headpiece does not name the two cuts it excludes,
/// it names the ones it allows. With the defaults applied, 32 of the 90
/// headpieces leave exactly one cut standing, four leave none, and 24 hide
/// eyewear.
/// </para>
/// <para>
/// A file may hold several records and most hold something else entirely — a
/// consumable, a starting inventory — so a record is taken only when it is a
/// costume item AND draws something. Everything else is skipped rather than
/// refused over: this is a catalogue being built, not a file being validated.
/// </para>
/// </remarks>
public static partial class CostumeCatalogue
{
    /// <summary>Where the game keeps its item definitions.</summary>
    public const string ItemFolder = "camel/game system data/juice/items/";

    /// <summary>
    /// The schema the item definitions are written against.
    /// </summary>
    /// <remarks>
    /// Named by the first line of the game's own <c>costumes.juice</c>, which
    /// includes it. Reading it is what turns <c>myHideSlot</c> from a list into
    /// a set of overrides — see <see cref="Schema"/>.
    /// </remarks>
    public const string SchemaFile = "camel/game system data/fruit/items/items.fruit";

    /// <summary>
    /// The <c>myCostumeType</c> of a piece worn whatever else is on.
    /// </summary>
    /// <remarks>
    /// Hair, facial hair, eyewear and makeup, in the main; 141 of the 408
    /// entries. The other three values name an outfit, and two outfits are
    /// never worn at once — see <see cref="Outfit"/>.
    /// </remarks>
    public const string EveryOutfit = "All";

    private const string ItemExtension = ".mitem";
    private const string TypePrefix = "CostumeItem";
    private const string NoSlot = "None";

    /// <summary>
    /// The game's own menu names for the kinds it puts on that screen, and the
    /// order it puts them in.
    /// </summary>
    /// <remarks>
    /// Eight kinds map one-to-one onto the eight entries of the character
    /// screen. Anything else — the backstory set, which belongs to the
    /// flashbacks rather than to this screen — keeps its record type as its
    /// name, so a kind this build has never heard of still appears instead of
    /// being silently dropped.
    /// </remarks>
    private static readonly (string Kind, string Slot)[] MenuSlots =
    [
        ("Head", "Head"),
        ("Body", "Clothes"),
        ("Hands", "Hands"),
        ("StreetEyewear", "Eyewear"),
        ("StreetHair", "Hair"),
        ("StreetFacialHair", "Facial Hair"),
        ("StreetMakeup", "Base Makeup"),
        ("StreetMakeup2", "Accent Makeup"),
    ];

    /// <summary>
    /// A costume record: its type, its id, and its body up to the closing brace.
    /// </summary>
    /// <remarks>
    /// Anchored hard against the left margin, which is what separates a record
    /// from the indented references inside a <c>myExtraPieces</c> block.
    /// </remarks>
    [GeneratedRegex(
        @"^(?<type>CostumeItem\w*)[ \t]+(?<id>\S+)\s*<[^>]*>\s*\{(?<body>.*?)^\}",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Record { get; }

    /// <summary>The model a record draws. Quotes are backslash-escaped in the file.</summary>
    [GeneratedRegex(@"MMAFile\s*=\s*\\?""(?<path>[^""\\]+\.mmb)", RegexOptions.CultureInvariant)]
    private static partial Regex Model { get; }

    /// <summary>
    /// The displayed name, which sits inside the localisation blob as
    /// <c>text = "..."</c>.
    /// </summary>
    [GeneratedRegex(@"text\s*=\s*\\?""(?<text>[^""\\]*)", RegexOptions.CultureInvariant)]
    private static partial Regex Displayed { get; }

    /// <summary>The block naming the records this one owns.</summary>
    [GeneratedRegex(@"myExtraPieces\s*\{(?<pieces>[^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex ExtraPieces { get; }

    /// <summary>
    /// One reference inside that block: a type, a label, and the id it names.
    /// </summary>
    /// <remarks>
    /// The label is quoted in some files and bare in others — the editor's
    /// placeholder against a name somebody typed — and half the hairstyles are
    /// written each way. Accepting only one form leaves 222 of the 471 regions
    /// unclaimed, which puts them back in the list as entries of their own.
    /// </remarks>
    [GeneratedRegex(
        @"CostumeItem\w*\s+(?:""[^""]*""|\S+)\s*<[^>]*>\s*=\s*(?<id>\S+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex PieceReference { get; }

    /// <summary>
    /// One <c>myHideSlot</c> row: which of the ten, and what it names.
    /// </summary>
    /// <remarks>
    /// The number matters. A row is an <b>override of the schema's default for
    /// that slot</b>, so <c>myHideSlot5 None</c> does not mean an empty row —
    /// it means "whatever the class hides in slot 5, this one does not".
    /// Reading the rows as a plain list, which is what they look like, gets the
    /// polarity backwards: a headpiece hides every hair category by default and
    /// names the ones it allows.
    /// </remarks>
    [GeneratedRegex(@"myHideSlot(?<slot>\d+)\s+(?<kind>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex HideSlot { get; }

    /// <summary>The colours an item ships with, as ids into the tint table.</summary>
    [GeneratedRegex(@"myDefaultTint\d\s+(?<uid>[0-9A-Fa-f]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultTint { get; }

    /// <summary>
    /// Which outfit a class or a record belongs to.
    /// </summary>
    /// <remarks>
    /// Declared per class and overridden per record, exactly as
    /// <see cref="HideSlot"/> is. 23 records override it and every one of them
    /// moves a shared piece into the hero outfit — nine eyewear and fourteen
    /// accent makeup, which are parts of a costume rather than choices of their
    /// own.
    /// </remarks>
    [GeneratedRegex(@"myCostumeType\s+(?<outfit>\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex CostumeType { get; }

    /// <summary>One class in the schema: its name, what it extends, and its body.</summary>
    [GeneratedRegex(
        @"^class\s+(?<name>\w+)\s*(?::\s*(?<base>\w+))?\s*\{(?<body>.*?)^\}",
        RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex SchemaClass { get; }

    /// <summary>One record as read, before ownership decides which are entries.</summary>
    private sealed record Parsed(
        string Kind,
        string Id,
        string Name,
        string? ModelPath,
        string SourcePath,
        ImmutableArray<string> Owns,
        Dictionary<int, string> Hides,
        ImmutableArray<string> Tints,
        string? Outfit);

    /// <summary>
    /// What the schema declares per class: the hide slots, and the outfit.
    /// </summary>
    /// <remarks>
    /// Both are defaults a record may write over, and both are inherited, so
    /// they are resolved together in one walk rather than in two that could
    /// disagree about what a class extends.
    /// </remarks>
    private sealed record Defaults(
        Dictionary<string, Dictionary<int, string>> Hides,
        Dictionary<string, string> Outfits);

    /// <summary>
    /// Everything wearable the archives describe, in the order the game's own
    /// menu offers it.
    /// </summary>
    public static Result<ImmutableArray<CostumeItem>> Read(
        ContentSources content, ImmutableArray<SdfPathEntry> paths)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (paths.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported(
                "The item list needs the archive's paths, and none were supplied.");
        }

        List<Parsed> records = [];
        bool anyFile = false;

        foreach (SdfPathEntry entry in paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            if (!path.StartsWith(ItemFolder, StringComparison.Ordinal) ||
                !path.EndsWith(ItemExtension, StringComparison.Ordinal))
            {
                continue;
            }

            anyFile = true;

            Result<byte[]?> read = content.Read(path);
            if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
            {
                return refusal;
            }

            if (bytes is not null)
            {
                Collect(Encoding.UTF8.GetString(bytes), path, records);
            }
        }

        if (!anyFile)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"There are no item definitions under {ItemFolder}, so there is nothing wearable to list."));
        }

        // The schema last, because it only refines what the records already
        // said. Absent, the catalogue is what it was before this file was known
        // about rather than a refusal: a mod folder need not carry one.
        Result<byte[]?> schemaFile = content.Read(SchemaFile);
        if (!schemaFile.TryGetValue(out byte[]? schemaBytes, out Refusal? schemaRefusal))
        {
            return schemaRefusal;
        }

        Defaults schema = schemaBytes is null
            ? new Defaults([], [])
            : Schema(Encoding.UTF8.GetString(schemaBytes));

        return Result.Ok(Assemble(records, schema));
    }

    /// <summary>
    /// One thing worn, and which of its variants to draw.
    /// </summary>
    /// <param name="Item">The entry chosen in a slot.</param>
    /// <param name="Variant">
    /// The model to draw it as, or null for <see cref="CostumeItem.Default"/>.
    /// </param>
    public readonly record struct CostumeWorn(CostumeItem Item, CostumePiece? Variant = null);

    /// <summary>One model to draw, and whether it replaces what is under it.</summary>
    public readonly record struct CostumeDrawn(string ModelPath, bool Replaces);

    /// <summary>What became of one worn entry, and what decided it.</summary>
    /// <param name="Item">The entry as it was chosen.</param>
    /// <param name="Piece">The model that will be drawn, or null for none.</param>
    /// <param name="Replaces">Whether it takes the character's own parts off underneath.</param>
    /// <param name="Blocked">
    /// Which of this entry's own kinds another piece hid. Empty means nothing
    /// interfered; every one of them beside a null <paramref name="Piece"/> is an
    /// outfit that leaves the entry nowhere to go.
    /// </param>
    public readonly record struct CostumeOutcome(
        CostumeItem Item,
        CostumePiece? Piece,
        bool Replaces,
        ImmutableArray<string> Blocked);

    /// <summary>
    /// The models to draw for a set of chosen pieces: one per piece, minus
    /// whatever another piece covers over entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One model per entry, never several — the variants are alternatives, and
    /// drawing them together is the same hairstyle six times in one place.
    /// </para>
    /// <para>
    /// A piece that names a whole slot removes what is worn there: a helmet
    /// over eyewear, a mask over makeup. A piece never hides itself, which
    /// costs nothing to guarantee and would otherwise be an entry that vanishes
    /// when chosen.
    /// </para>
    /// </remarks>
    public static ImmutableArray<CostumeDrawn> Wear(IEnumerable<CostumeWorn> worn) =>
    [
        .. Explain(worn)
            .Where(outcome => outcome.Piece is not null)
            .Select(outcome => new CostumeDrawn(outcome.Piece!.ModelPath, outcome.Replaces)),
    ];

    /// <summary>
    /// The one outfit a set of pieces belongs to, or a refusal naming the two
    /// that cannot be worn together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The character has three outfits — <c>Hero</c>, <c>Street</c> and
    /// <c>Backstory</c> — and wears one at a time. Everything else is
    /// <see cref="EveryOutfit"/> and goes with whichever is on: hair, facial
    /// hair, eyewear, makeup. That is the game's own <c>myCostumeType</c>,
    /// declared per class and overridden by 23 records, not a reading of the
    /// slot names.
    /// </para>
    /// <para>
    /// <b>It matters because two outfits at once is a second body.</b> Each of
    /// the three has a head, a body and a pair of hands, and each replaces the
    /// character's own parts where it draws — so wearing a hero body over a
    /// street body puts 148 meshes on a chest joint that holds three, one suit
    /// inside another. The reports were "an extra floating pair of hands" and
    /// "the outfit is not visible from the reverse", which are the front and the
    /// back of that one fault.
    /// </para>
    /// <para>
    /// <b>Refused rather than resolved</b>, because nothing says which of the
    /// two the wearer meant. Dropping one silently is the failure this whole
    /// section exists to avoid: an outfit that vanishes with nothing saying why.
    /// </para>
    /// </remarks>
    public static Result<string> Outfit(IEnumerable<CostumeItem> worn)
    {
        ArgumentNullException.ThrowIfNull(worn);

        CostumeItem? first = null;
        foreach (CostumeItem item in worn)
        {
            if (!item.IsExclusive)
            {
                continue;
            }

            if (first is null)
            {
                first = item;
                continue;
            }

            if (!string.Equals(first.Outfit, item.Outfit, StringComparison.Ordinal))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{first.Name}' belongs to the {first.Outfit} outfit and '{item.Name}' to the {item.Outfit} one, and a character wears one outfit at a time. Worn together they draw two bodies in one place, so take one of them off."));
            }
        }

        return Result.Ok(first?.Outfit ?? EveryOutfit);
    }

    /// <summary>
    /// The same decision as <see cref="Wear"/>, with what it left out and why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the implementation and <see cref="Wear"/> is a view over it</b>,
    /// so the account cannot drift from what actually gets drawn. Two
    /// implementations of one rule is how an explanation comes to describe
    /// something the tool no longer does.
    /// </para>
    /// <para>
    /// It exists because every one of these decisions is invisible in the
    /// output: a hairstyle that produced no model and a hairstyle nobody chose
    /// look identical in a GLB, and the reports that prompted it were all of the
    /// form "it vanished and nothing said why".
    /// </para>
    /// </remarks>
    public static ImmutableArray<CostumeOutcome> Explain(IEnumerable<CostumeWorn> worn)
    {
        ArgumentNullException.ThrowIfNull(worn);

        List<CostumeWorn> chosen = [.. worn];
        List<CostumeOutcome> outcomes = [];

        foreach (CostumeWorn one in chosen)
        {
            HashSet<string> hidden = new(StringComparer.Ordinal);
            foreach (CostumeWorn other in chosen)
            {
                if (!ReferenceEquals(other.Item, one.Item))
                {
                    hidden.UnionWith(other.Item.Hides);
                }
            }

            // A variant asked for is drawn even where the outfit rules it out:
            // the pane offers it, and silently dropping what somebody chose is
            // worse than showing a combination the game would not.
            //
            // **Whether the entry survives is Fits' answer and nothing else's.**
            // There used to be a check above this for the entry's own kind being
            // hidden, which dropped it outright. It read every headpiece as
            // removing every hairstyle, because the schema gives each one
            // `myHideSlot1 StreetHair` -- the parent category, whose meaning is
            // "not the whole hairstyle", not "no hair at all". The six cuts
            // beneath it are separately named, and a headpiece that leaves one
            // standing means it to be worn. The check made a nonsense of that
            // and of the four hats that really do hide every cut, which Fits
            // already answers by running out of variants.
            CostumePiece? draw = one.Variant ?? Fits(one.Item, hidden);

            // Only this entry's own kinds, not everything the outfit hides. What
            // a headpiece does to eyewear is not why a hairstyle lost a cut.
            List<string> blocked = [];
            if (hidden.Contains(one.Item.Kind))
            {
                blocked.Add(one.Item.Kind);
            }

            foreach (CostumePiece variant in one.Item.Variants)
            {
                if (hidden.Contains(variant.Kind) && !blocked.Contains(variant.Kind, StringComparer.Ordinal))
                {
                    blocked.Add(variant.Kind);
                }
            }

            blocked.Sort(StringComparer.Ordinal);

            outcomes.Add(new CostumeOutcome(one.Item, draw, one.Item.Replaces, [.. blocked]));
        }

        return [.. outcomes];
    }

    /// <summary>
    /// The variant to draw, or null where the outfit leaves room for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A headpiece states which cuts of a hairstyle may be worn under it, and
    /// with the schema's defaults applied that is a real answer rather than a
    /// hint: of the 90, <b>32 leave exactly one cut standing and four leave
    /// none</b>.
    /// </para>
    /// <para>
    /// Where several survive — 31 leave two — the whole-head version wins if it
    /// is among them, else the first declared, which is the file's own order and
    /// so is the same on every run. That is the only guess left here, it is
    /// between cuts the game has already said are all allowed, and the choice
    /// stays in front of the user in the Hair slot.
    /// </para>
    /// </remarks>
    private static CostumePiece? Fits(CostumeItem item, HashSet<string> hidden)
    {
        CostumePiece preferred = item.Default;
        if (!hidden.Contains(preferred.Kind))
        {
            return preferred;
        }

        foreach (CostumePiece variant in item.Variants)
        {
            if (!hidden.Contains(variant.Kind))
            {
                return variant;
            }
        }

        // Nothing it can be drawn as survives. Four headpieces cover the head
        // completely, and drawing hair anyway is drawing it through a helmet.
        return null;
    }

    /// <summary>The models to draw, each entry as the variant it defaults to.</summary>
    public static ImmutableArray<CostumeDrawn> Wear(IEnumerable<CostumeItem> worn)
    {
        ArgumentNullException.ThrowIfNull(worn);
        return Wear(worn.Select(item => new CostumeWorn(item)));
    }

    /// <summary>The slots present, in the order a list should offer them.</summary>
    public static ImmutableArray<string> Slots(ImmutableArray<CostumeItem> items)
    {
        if (items.IsDefaultOrEmpty)
        {
            return [];
        }

        List<string> slots = [];
        foreach (CostumeItem item in items)
        {
            if (!slots.Contains(item.Slot, StringComparer.Ordinal))
            {
                slots.Add(item.Slot);
            }
        }

        return [.. slots];
    }

    /// <summary>
    /// Turns the records into entries: a record owned by another is that one's
    /// piece, and everything left that draws something is an entry.
    /// </summary>
    private static ImmutableArray<CostumeItem> Assemble(
        List<Parsed> records, Defaults schema)
    {
        Dictionary<string, Parsed> byId = new(StringComparer.Ordinal);
        foreach (Parsed record in records)
        {
            byId[record.Id] = record;
        }

        HashSet<string> owned = new(StringComparer.Ordinal);
        foreach (Parsed record in records)
        {
            owned.UnionWith(record.Owns);
        }

        List<CostumeItem> items = [];
        foreach (Parsed record in records)
        {
            if (owned.Contains(record.Id))
            {
                continue;
            }

            ImmutableArray<CostumePiece> variants = Gather(record, byId);
            if (variants.IsEmpty)
            {
                continue;
            }

            // The record's own row wins, then the class default, then "worn
            // with everything" -- which is what an absent schema leaves, and is
            // the reading this build had before the field was known about.
            string outfit = record.Outfit
                ?? (schema.Outfits.TryGetValue(record.Kind, out string? declared) ? declared : EveryOutfit);

            items.Add(new CostumeItem(
                record.Name, SlotFor(record.Kind), record.Kind, variants,
                Hidden(record, schema.Hides), record.Tints, outfit, record.SourcePath));
        }

        items.Sort(static (left, right) =>
        {
            int slot = Order(left.Slot).CompareTo(Order(right.Slot));
            if (slot != 0)
            {
                return slot;
            }

            slot = string.CompareOrdinal(left.Slot, right.Slot);
            return slot != 0 ? slot : string.CompareOrdinal(left.Name, right.Name);
        });

        return [.. items];
    }

    /// <summary>
    /// What an item covers up: the schema's defaults for its class, with the
    /// record's own rows written over them.
    /// </summary>
    /// <remarks>
    /// This is the whole difference between "a headpiece hides the two hair
    /// cuts it names" and the truth, which is that it hides all seven and names
    /// the ones it allows. Of the 90 headpieces, 32 leave exactly one cut
    /// standing, four leave none at all, and 24 hide eyewear.
    /// </remarks>
    private static ImmutableArray<string> Hidden(
        Parsed record, Dictionary<string, Dictionary<int, string>> schema)
    {
        Dictionary<int, string> slots =
            schema.TryGetValue(record.Kind, out Dictionary<int, string>? byClass)
                ? new Dictionary<int, string>(byClass)
                : [];

        foreach ((int slot, string kind) in record.Hides)
        {
            slots[slot] = kind;
        }

        List<string> hidden = [];
        foreach (string kind in slots.Values)
        {
            if (!string.Equals(kind, NoSlot, StringComparison.Ordinal) &&
                !hidden.Contains(kind, StringComparer.Ordinal))
            {
                hidden.Add(kind);
            }
        }

        hidden.Sort(StringComparer.Ordinal);
        return [.. hidden];
    }

    /// <summary>
    /// The schema's defaults per class, with inheritance resolved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Keyed by the class name without its <c>CostumeItem</c> prefix, which is
    /// the same key an item's kind uses.
    /// </para>
    /// <para>
    /// <b>An absent schema means no defaults, not an error.</b> A mod folder
    /// need not carry one, and the catalogue is still worth having without it —
    /// it reads then exactly as it did before the file was known about. Without
    /// it every entry is <see cref="EveryOutfit"/>, which is the reading this
    /// build had before the field was known about.
    /// </para>
    /// </remarks>
    private static Defaults Schema(string text)
    {
        Dictionary<string, (string? Base, Dictionary<int, string> Hides, string? Outfit)> declared = [];
        foreach (Match found in SchemaClass.Matches(text))
        {
            string body = found.Groups["body"].Value;

            Dictionary<int, string> own = [];
            foreach (Match row in HideSlot.Matches(body))
            {
                own[int.Parse(row.Groups["slot"].Value, CultureInfo.InvariantCulture)] =
                    row.Groups["kind"].Value;
            }

            Match outfit = CostumeType.Match(body);
            declared[found.Groups["name"].Value] = (
                found.Groups["base"].Success ? found.Groups["base"].Value : null,
                own,
                outfit.Success ? outfit.Groups["outfit"].Value : null);
        }

        Defaults resolved = new([], []);

        (Dictionary<int, string> Hides, string? Outfit) Walk(string name, int depth)
        {
            // Guarded rather than trusted: a cycle in a declaration this code
            // did not write would otherwise be a hang.
            if (depth > declared.Count ||
                !declared.TryGetValue(name, out (string? Base, Dictionary<int, string> Hides, string? Outfit) one))
            {
                return ([], null);
            }

            (Dictionary<int, string> above, string? outfit) =
                one.Base is null ? ([], null) : Walk(one.Base, depth + 1);

            Dictionary<int, string> slots = new(above);
            foreach ((int slot, string kind) in one.Hides)
            {
                slots[slot] = kind;
            }

            return (slots, one.Outfit ?? outfit);
        }

        foreach (string name in declared.Keys)
        {
            if (name.StartsWith(TypePrefix, StringComparison.Ordinal) && name.Length > TypePrefix.Length)
            {
                (Dictionary<int, string> hides, string? outfit) = Walk(name, 0);
                string kind = name[TypePrefix.Length..];
                resolved.Hides[kind] = hides;
                if (outfit is not null)
                {
                    resolved.Outfits[kind] = outfit;
                }
            }
        }

        return resolved;
    }

    /// <summary>
    /// Every model an entry can be drawn as: its own, then the ones it owns,
    /// in the order the file declares them.
    /// </summary>
    /// <remarks>
    /// Transitive and guarded against revisiting, because one child record owns
    /// a variant of its own. A cycle in authored data would otherwise be a hang
    /// rather than a wrong list.
    /// </remarks>
    private static ImmutableArray<CostumePiece> Gather(Parsed record, Dictionary<string, Parsed> byId)
    {
        List<CostumePiece> pieces = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<Parsed> pending = new();
        pending.Enqueue(record);
        seen.Add(record.Id);

        while (pending.Count > 0)
        {
            Parsed current = pending.Dequeue();
            if (current.ModelPath is not null)
            {
                pieces.Add(new CostumePiece(current.Kind, current.ModelPath));
            }

            foreach (string id in current.Owns)
            {
                if (seen.Add(id) && byId.TryGetValue(id, out Parsed? piece))
                {
                    pending.Enqueue(piece);
                }
            }
        }

        return [.. pieces];
    }

    private static string SlotFor(string kind)
    {
        foreach ((string named, string slot) in MenuSlots)
        {
            if (string.Equals(named, kind, StringComparison.Ordinal))
            {
                return slot;
            }
        }

        return kind;
    }

    private static int Order(string slot)
    {
        for (int i = 0; i < MenuSlots.Length; i++)
        {
            if (string.Equals(MenuSlots[i].Slot, slot, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return MenuSlots.Length;
    }

    private static void Collect(string text, string sourcePath, List<Parsed> into)
    {
        foreach (Match record in Record.Matches(text))
        {
            string body = record.Groups["body"].Value;

            string kind = record.Groups["type"].Value[TypePrefix.Length..];
            if (kind.Length == 0)
            {
                continue;
            }

            Match model = Model.Match(body);
            string? path = model.Success ? SdfIndex.NormalizePath(model.Groups["path"].Value) : null;

            List<string> owns = [];
            Match block = ExtraPieces.Match(body);
            if (block.Success)
            {
                foreach (Match piece in PieceReference.Matches(block.Groups["pieces"].Value))
                {
                    owns.Add(piece.Groups["id"].Value);
                }
            }

            if (path is null && owns.Count == 0)
            {
                continue;
            }

            Dictionary<int, string> hides = [];
            foreach (Match hide in HideSlot.Matches(body))
            {
                hides[int.Parse(hide.Groups["slot"].Value, CultureInfo.InvariantCulture)] =
                    hide.Groups["kind"].Value;
            }

            // The displayed name, or the id where the localisation blob carries
            // none. An unnamed row is still a row somebody may want, and hiding
            // it would make the list quietly incomplete.
            Match displayed = Displayed.Match(body);
            string name = displayed.Success && displayed.Groups["text"].Value.Length > 0
                ? displayed.Groups["text"].Value
                : record.Groups["id"].Value;

            ImmutableArray<string> tints =
            [
                .. DefaultTint.Matches(body).Select(m => m.Groups["uid"].Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal),
            ];

            Match outfit = CostumeType.Match(body);

            into.Add(new Parsed(
                kind, record.Groups["id"].Value, name, path, sourcePath, [.. owns], hides, tints,
                outfit.Success ? outfit.Groups["outfit"].Value : null));
        }
    }
}
