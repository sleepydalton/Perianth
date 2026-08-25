using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;

namespace Perianth.Core.Content;

/// <summary>
/// A new item derived from one the game ships, and the localisation row that
/// gives it a name.
/// </summary>
/// <param name="Item">The new <c>.mitem</c>, ready to write.</param>
/// <param name="Uid">Its uid — what a vendor, recipe or loot table must name.</param>
/// <param name="NameGuid">The guid its display name is keyed by, when one was set.</param>
/// <param name="DisplayName">The text that guid should resolve to.</param>
/// <param name="ExtraPieces">
/// How many variants the template claimed. <b>A record is not an entry</b>: a
/// hairstyle is one menu item and up to seven records, and the ones named here
/// are what actually draws. Copying such a parent gives a new entry whose cuts
/// are still the template's, so repointing the parent's model changes little —
/// which is invisible in the file and worth saying out loud.
/// </param>
public sealed record ItemDerivation(
    ReadOnlyMemory<byte> Item, string Uid, string? NameGuid, string? DisplayName, int ExtraPieces);

/// <summary>One thing a recipe consumes, and how many of it.</summary>
/// <param name="ItemUid">The component or item spent.</param>
/// <param name="Count">How many, which every shipped ingredient states.</param>
public readonly record struct CraftIngredient(string ItemUid, int Count);

/// <summary>
/// Makes a new item by copying a shipped one and changing the few fields that
/// distinguish it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The declared class is the slot</b>, so the choice of template is the
/// choice of slot and this never sets one: 26 classes account for all 3,038
/// shipped items and the costume ones name their slot outright (Roadmap §10.89).
/// A caller wanting a hat copies a hat.
/// </para>
/// <para>
/// Everything the template said that is not named here is carried over exactly,
/// because <see cref="JuiceDocument"/> splices rather than rebuilds. That is what
/// keeps this small against a schema of 887 classes: the fields this does not
/// understand are fields it does not have to.
/// </para>
/// <para>
/// <b>A name is not the item's to give.</b> The display name resolves through
/// <c>menus.locpack</c> — a CSV of <c>GUID,0,Text</c> — keyed by the guid inside
/// the item's own <c>myUIName</c>. The <c>text =</c> the item carries is the
/// authoring copy, so editing it alone would produce a rename that silently does
/// nothing. <see cref="AddLocalisation"/> writes the row that makes it real, and
/// <see cref="Derive"/> reports the guid rather than pretending the item is
/// enough on its own.
/// </para>
/// </remarks>
public static class ItemEdit
{
    /// <summary>The folder every item definition lives in, flat.</summary>
    private const string ItemFolder = "camel/game system data/juice/items/";

    /// <summary>Where an item of a given declared name must be written.</summary>
    /// <remarks>
    /// <para>
    /// <b>Not a convention — a lookup.</b> The executable holds
    /// <c>camel/game system data/juice/items/%s.mitem</c> and no wildcard beside
    /// it, so an item is loaded by having its name turned into a path. The
    /// corpus agrees: 3,037 of 3,038 files are named exactly their declaration,
    /// lower-cased, and the one exception also replaces a space. So a file put
    /// anywhere else, or under any other stem, is a definition nothing will
    /// ever ask for.
    /// </para>
    /// <para>
    /// Lower-cased because the archives are, and because the loose-file loader's
    /// override index is case-insensitive (Roadmap §6.14) — so the lower-case
    /// tree an extraction writes is what a mod should match.
    /// </para>
    /// </remarks>
    public static string ProposePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return ItemFolder + name.ToLowerInvariant() + ".mitem";
    }

    /// <summary>The field naming the model, on 857 of 857 items that have one.</summary>
    private const string ModelField = "myModel";

    /// <summary>The field carrying the localisation blob.</summary>
    private const string NameField = "myUIName";

    /// <summary>Copies a template item under a new name, model and display name.</summary>
    /// <param name="template">A shipped item of the slot wanted.</param>
    /// <param name="name">The new declaration name, which is also the file's stem.</param>
    /// <param name="modelPath">The archive path of the new <c>.mmb</c>.</param>
    /// <param name="displayName">The name to show, or null to keep the template's.</param>
    public static Result<ItemDerivation> Derive(
        SourceFile template, string name, string modelPath, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(modelPath);

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A new item needs a name, which is also its file name.");
        }

        if (modelPath.Length == 0)
        {
            return Refusal.Unsupported("A new item needs a model path; an item drawing nothing is not one.");
        }

        Result<JuiceDocument> read = JuiceDocument.Read(template);
        if (!read.IsSuccess)
        {
            return read.Refusal;
        }

        JuiceDocument document = read.Value;

        // Refused rather than added. A template with no model is one of the
        // 2,181 items that are not worn — a component or a recipe — and copying
        // it to make a costume piece is a mistake this can see and the caller
        // cannot, once the file is written.
        if (!document.TryGetField(ModelField, out JuiceField model) || model.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{document.DeclaredClass}' carries no {ModelField}, so it is not a template for a worn item."));
        }

        string uid = MintUid(name);
        Result<JuiceDocument> renamed = document.WithDeclaration(name, uid);
        if (!renamed.IsSuccess)
        {
            return renamed.Refusal;
        }

        Result<JuiceDocument> repointed = renamed.Value.WithField(ModelField, ModelValue(modelPath));
        if (!repointed.IsSuccess)
        {
            return repointed.Refusal;
        }

        int pieces = CountExtraPieces(document);

        if (displayName is null)
        {
            return Result.Ok(new ItemDerivation(repointed.Value.Bytes, uid, null, null, pieces));
        }

        Result<JuiceDocument> named = WithDisplayName(repointed.Value, name, displayName);
        return named.IsSuccess
            ? Result.Ok(new ItemDerivation(
                named.Value.Bytes, uid, MintUid(name + " name"), displayName, pieces))
            : named.Refusal;
    }

    /// <summary>Adds a row to a locpack, keeping its declared row count in step.</summary>
    /// <remarks>
    /// The file is a CSV with two header lines — a version and a row count — then
    /// <c>GUID,0,Text</c> rows, CRLF throughout. The count is the part worth
    /// getting right: a row appended without it is a file that disagrees with
    /// itself, which is the same fault as the vertex count the MMB tail restates
    /// (Roadmap §10.67), and it would be just as invisible until something read
    /// the end of the table.
    /// </remarks>
    public static Result<ReadOnlyMemory<byte>> AddLocalisation(
        SourceFile locpack, string key, string text)
    {
        ArgumentNullException.ThrowIfNull(locpack);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(text);

        if (!JuiceDocument.IsUid(key))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A localisation key is 32 upper-case hex digits and '{key}' is not."));
        }

        if (text.Contains('\r', StringComparison.Ordinal)
            || text.Contains('\n', StringComparison.Ordinal))
        {
            return Refusal.Unsupported("A localisation row is one line, so its text cannot contain a newline.");
        }

        string body = Encoding.Latin1.GetString(locpack.Bytes);
        int firstBreak = body.IndexOf("\r\n", StringComparison.Ordinal);
        int secondBreak = firstBreak < 0
            ? -1
            : body.IndexOf("\r\n", firstBreak + 2, StringComparison.Ordinal);
        if (secondBreak < 0)
        {
            return Refusal.Malformed("The locpack has no version and count header.");
        }

        string countLine = body[(firstBreak + 2)..secondBreak];
        int comma = countLine.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0
            || !int.TryParse(countLine[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out int rows))
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The locpack's second line is '{countLine}', which is not a row count."));
        }

        if (body.Contains(key + ",", StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The locpack already carries a row for {key}."));
        }

        string appended = body.EndsWith("\r\n", StringComparison.Ordinal) ? string.Empty : "\r\n";
        StringBuilder built = new(body.Length + text.Length + 64);
        built.Append(body[..(firstBreak + 2)]);
        built.Append(CultureInfo.InvariantCulture, $"{rows + 1}{countLine[comma..]}\r\n");
        built.Append(body[(secondBreak + 2)..]);
        built.Append(appended);
        built.Append(CultureInfo.InvariantCulture, $"{key},0,{text}\r\n");

        return Result.Ok<ReadOnlyMemory<byte>>(Encoding.Latin1.GetBytes(built.ToString()));
    }

    /// <summary>The block a vendor's stock list lives in.</summary>
    private const string StockField = "myVendorItemList";

    /// <summary>Puts an item on a named vendor's shelf, from a given story state.</summary>
    /// <remarks>
    /// <para>
    /// This is the <em>"where and when"</em> half of making an item obtainable,
    /// and it is why deriving an item is not enough on its own: nothing in an
    /// item says where it comes from, because shops name items rather than the
    /// other way round (Roadmap §10.91). Four routes exist — shop, crafting,
    /// starting inventory and loot table — and this is the shop.
    /// </para>
    /// <para>
    /// The vendor must be named. One file holds forty of them, so editing
    /// whichever came first would be a guess rather than an operation.
    /// </para>
    /// <para>
    /// The entry's index is the count already present. Every one of the 30
    /// shipped stock lists numbers its entries 0..n-1 in order, so appending at
    /// n keeps the file in the shape the game's own tools write, and the count
    /// is read from the block rather than assumed.
    /// </para>
    /// </remarks>
    /// <param name="vendors">A <c>.mvendorconfig</c>.</param>
    /// <param name="vendor">Which shop, by its declared name.</param>
    /// <param name="itemUid">The item to stock.</param>
    /// <param name="gameState">The story state it appears from.</param>
    public static Result<ReadOnlyMemory<byte>> Stock(
        SourceFile vendors, string vendor, string itemUid, string gameState)
    {
        ArgumentNullException.ThrowIfNull(vendors);
        ArgumentNullException.ThrowIfNull(vendor);
        ArgumentNullException.ThrowIfNull(itemUid);
        ArgumentNullException.ThrowIfNull(gameState);

        if (!JuiceDocument.IsUid(itemUid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"An item uid is 32 upper-case hex digits and '{itemUid}' is not."));
        }

        if (!IsGameState(gameState))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{gameState}' is not a story state. The game's own list is: {string.Join(", ", GameStates)}."));
        }

        Result<JuiceDocument> read = JuiceDocument.Read(vendors, vendor);
        if (!read.IsSuccess)
        {
            return read.Refusal;
        }

        JuiceDocument document = read.Value;
        if (!document.TryGetField(StockField, out JuiceField stock) || !stock.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{vendor}' carries no {StockField}, so it is not a shop."));
        }

        string body = Encoding.Latin1.GetString(document.Bytes.Span);
        if (Lists(body, stock, itemUid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{vendor}' already stocks {itemUid}, and stocking it twice would list it twice."));
        }

        int index = CountEntries(body, stock);
        string entry = string.Create(
            CultureInfo.InvariantCulture,
            $"\t\tVendorItem {index}\r\n\t\t{{\r\n\t\t\tmyItem {itemUid}\r\n\t\t\tmyGameState {gameState}\r\n\t\t}}\r\n");

        // The shipped file uses CRLF; matching whatever it already uses keeps the
        // edit invisible to a diff of everything else.
        if (!body.Contains("\r\n", StringComparison.Ordinal))
        {
            entry = entry.Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        Result<JuiceDocument> stocked = document.WithBlockEntry(StockField, entry);
        return stocked.IsSuccess
            ? Result.Ok(stocked.Value.Bytes)
            : stocked.Refusal;
    }

    /// <summary>The block a starting-inventory setting lists items in.</summary>
    private const string GrantField = "myItemList";

    /// <summary>Grants an item outright, through a named starting-inventory setting.</summary>
    /// <remarks>
    /// <para>
    /// The largest of the four routes by a distance: 271 of 408 costume entries
    /// reach the player this way, against 64 sold and 45 crafted (Roadmap
    /// §10.92). It is also the route missions use — a mission's
    /// <c>myRecommendedItems</c> is a list of *names of settings*, not of items,
    /// so granting through a setting is how a reward is given.
    /// </para>
    /// <para>
    /// The settings are nested and carry no uid at all, which is why
    /// <see cref="JuiceDocument"/> had to stop insisting on one.
    /// </para>
    /// </remarks>
    /// <param name="inventory">A <c>starting_inventory.juice</c>.</param>
    /// <param name="setting">Which setting, by its declared name.</param>
    /// <param name="itemUid">The item to grant.</param>
    /// <param name="count">How many, which the shipped entries always state.</param>
    public static Result<ReadOnlyMemory<byte>> Grant(
        SourceFile inventory, string setting, string itemUid, int count = 1)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(itemUid);

        if (!JuiceDocument.IsUid(itemUid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"An item uid is 32 upper-case hex digits and '{itemUid}' is not."));
        }

        if (count < 1)
        {
            return Refusal.Unsupported("Granting fewer than one of something is not granting it.");
        }

        Result<JuiceDocument> read = JuiceDocument.Read(inventory, setting);
        if (!read.IsSuccess)
        {
            return read.Refusal;
        }

        JuiceDocument document = read.Value;
        if (!document.TryGetField(GrantField, out JuiceField list) || !list.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{setting}' carries no {GrantField}, so it grants no items."));
        }

        string body = Encoding.Latin1.GetString(document.Bytes.Span);
        if (Lists(body, list, itemUid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{setting}' already grants {itemUid}."));
        }

        string entry = string.Create(
            CultureInfo.InvariantCulture,
            $"\t\t\t\tStartingItemSetting {itemUid}\n\t\t\t\t{{\n\t\t\t\t\tmyItem {itemUid}\n\t\t\t\t\tmyCount {count}\n\t\t\t\t}}\n");

        if (body.Contains("\r\n", StringComparison.Ordinal))
        {
            entry = entry.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        Result<JuiceDocument> granted = document.WithBlockEntry(GrantField, entry);
        return granted.IsSuccess
            ? Result.Ok(granted.Value.Bytes)
            : granted.Refusal;
    }

    /// <summary>The block a recipe lists what it consumes in.</summary>
    private const string IngredientsField = "myIngredients";

    /// <summary>The field naming what a recipe produces.</summary>
    private const string ResultField = "myResult";

    /// <summary>The field naming the recipe's own item — the one the player holds.</summary>
    private const string RecipeItemField = "myItem";

    /// <summary>Makes a crafting recipe by copying one the game ships.</summary>
    /// <remarks>
    /// <para>
    /// The third route, and 45 of 408 costume entries reach the player through
    /// it (Roadmap §10.92). Note that it does not stand alone: <b>a recipe is
    /// itself an item</b>, held by the player, so the recipe's <c>myItem</c>
    /// names a second <c>.mitem</c> which must in turn be sold, granted or
    /// dropped. Crafting composes with the other three rather than replacing
    /// them, which is exactly how the shipped ones work.
    /// </para>
    /// <para>
    /// A recipe is a whole declaration rather than an entry in a list, so this
    /// copies one entire and changes four things: its name, what it consumes,
    /// what it produces, and which item is the recipe. The sixteen lines it does
    /// not mention — the price, the masterly level, the four empty upgrade slots
    /// — come across verbatim, which is the same rule the binary writers keep.
    /// </para>
    /// <para>
    /// The template is named, because the file holds 117 of them and the one
    /// copied decides everything not listed above.
    /// </para>
    /// </remarks>
    /// <param name="recipes">A <c>recipes.juice</c>.</param>
    /// <param name="template">The recipe to copy, by its declared name.</param>
    /// <param name="name">The new recipe's declared name.</param>
    /// <param name="recipeItemUid">The item that <em>is</em> the recipe.</param>
    /// <param name="resultUid">The item crafting it produces.</param>
    /// <param name="ingredients">What it consumes, which replaces the template's.</param>
    public static Result<ReadOnlyMemory<byte>> Craft(
        SourceFile recipes,
        string template,
        string name,
        string recipeItemUid,
        string resultUid,
        System.Collections.Immutable.ImmutableArray<CraftIngredient> ingredients)
    {
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(recipeItemUid);
        ArgumentNullException.ThrowIfNull(resultUid);

        if (!JuiceDocument.IsUid(recipeItemUid) || !JuiceDocument.IsUid(resultUid))
        {
            return Refusal.Unsupported(
                "A recipe names two items by uid — itself and what it makes — and both are 32 upper-case hex digits.");
        }

        if (ingredients.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported("A recipe with no ingredients costs nothing to craft, which is not a recipe.");
        }

        foreach (CraftIngredient ingredient in ingredients)
        {
            if (!JuiceDocument.IsUid(ingredient.ItemUid))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"An ingredient's uid is 32 upper-case hex digits and '{ingredient.ItemUid}' is not."));
            }

            if (ingredient.Count < 1)
            {
                return Refusal.Unsupported("An ingredient consumed fewer than once is not an ingredient.");
            }
        }

        Result<JuiceDocument> read = JuiceDocument.Read(recipes, template);
        if (!read.IsSuccess)
        {
            return read.Refusal;
        }

        JuiceDocument whole = read.Value;
        string body = Encoding.Latin1.GetString(whole.Bytes.Span);
        if (body.Contains(resultUid, StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{resultUid} is already made by a recipe in this file, and a second one would craft it twice."));
        }

        // The copy is re-read on its own so that the ranges are the copy's, not
        // the file's. Splicing through the whole file's index would work and
        // would put every edit at an offset measured from a different document.
        SourceFile copy = SourceFile.FromMemory(
            recipes.Path,
            whole.Bytes.Slice(whole.DeclarationRange.Offset, whole.DeclarationRange.Length).ToArray());

        Result<JuiceDocument> derived = JuiceDocument.Read(copy);
        if (!derived.IsSuccess)
        {
            return derived.Refusal;
        }

        bool crlf = body.Contains("\r\n", StringComparison.Ordinal);
        StringBuilder listed = new();
        for (int i = 0; i < ingredients.Length; i++)
        {
            listed.Append(CultureInfo.InvariantCulture,
                $"\t\tIngredient {i}\n\t\t{{\n\t\t\tmyItem {ingredients[i].ItemUid}\n\t\t\tmyCount {ingredients[i].Count}\n\t\t}}\n");
        }

        Result<JuiceDocument> built = derived.Value.WithName(name);
        if (built.IsSuccess) { built = built.Value.WithField(RecipeItemField, recipeItemUid); }
        if (built.IsSuccess) { built = built.Value.WithField(ResultField, resultUid); }
        if (built.IsSuccess)
        {
            string entries = listed.ToString();
            built = built.Value.WithBlockContents(
                IngredientsField,
                crlf ? entries.Replace("\n", "\r\n", StringComparison.Ordinal) : entries);
        }

        if (!built.IsSuccess)
        {
            return built.Refusal;
        }

        string separator = body.EndsWith('\n') ? string.Empty : crlf ? "\r\n" : "\n";
        byte[] appended = new byte[whole.Bytes.Length + separator.Length + built.Value.Bytes.Length];
        whole.Bytes.Span.CopyTo(appended);
        Encoding.Latin1.GetBytes(separator).CopyTo(appended, whole.Bytes.Length);
        built.Value.Bytes.Span.CopyTo(appended.AsSpan(whole.Bytes.Length + separator.Length));

        return Result.Ok<ReadOnlyMemory<byte>>(appended);
    }

    /// <summary>The block a loot table lists its entries in.</summary>
    private const string LootField = "myLootEntries";

    /// <summary>Adds an item to a named loot table.</summary>
    /// <remarks>
    /// <para>
    /// The fourth route, and the widest: 338 of 408 costume entries appear in
    /// some table, across 2,211 of them (Roadmap §10.92). A table is a
    /// container the game opens — a chest, a drawer, a mailbox — so this is
    /// "put it in that chest".
    /// </para>
    /// <para>
    /// The entry is written as an <b>independent</b> drop: weight <c>-1</c> and
    /// exclusion group <c>None</c>, which is how 3,412 of 4,173 shipped entries
    /// are written and means the item is rolled on its own rather than competing
    /// with the table's other contents. The weighted shape — a weight and a
    /// shared exclusion group, so exactly one of a set is chosen — would change
    /// what the entries already there do, and adding an item is not a licence to
    /// alter the table's existing odds.
    /// </para>
    /// </remarks>
    /// <param name="lootTables">A loot table file.</param>
    /// <param name="table">Which table, by its declared name.</param>
    /// <param name="itemUid">The item to drop.</param>
    /// <param name="chance">The probability it appears, 0 to 1.</param>
    /// <param name="quantityMin">The fewest dropped.</param>
    /// <param name="quantityMax">The most dropped.</param>
    public static Result<ReadOnlyMemory<byte>> Drop(
        SourceFile lootTables,
        string table,
        string itemUid,
        double chance = 1.0,
        int quantityMin = 1,
        int quantityMax = 1)
    {
        ArgumentNullException.ThrowIfNull(lootTables);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(itemUid);

        if (!JuiceDocument.IsUid(itemUid))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"An item uid is 32 upper-case hex digits and '{itemUid}' is not."));
        }

        if (!double.IsFinite(chance) || chance <= 0 || chance > 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A drop chance runs from just above 0 to 1, and {chance} does not. A chance of zero is not a drop."));
        }

        if (quantityMin < 1 || quantityMax < quantityMin)
        {
            return Refusal.Unsupported("A drop is between one and at least that many, in that order.");
        }

        Result<JuiceDocument> read = JuiceDocument.Read(lootTables, table);
        if (!read.IsSuccess)
        {
            return read.Refusal;
        }

        JuiceDocument document = read.Value;
        if (!document.TryGetField(LootField, out JuiceField entries) || !entries.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{table}' carries no {LootField}, so it is not a loot table."));
        }

        // No duplicate check here, unlike stocking and granting. The game's own
        // loot tables list the same item twice in 9 of theirs, with different
        // chances and quantities, so a second entry is an authored thing rather
        // than a mistake this can recognise.
        string body = Encoding.Latin1.GetString(document.Bytes.Span);
        int index = CountEntries(body, entries);
        string entry = string.Create(
            CultureInfo.InvariantCulture,
            $"\t\tLootEntry {index}\n\t\t{{\n\t\t\tmyExclusionGroup None\n\t\t\tmyWeight -1\n\t\t\tmyChance {chance.ToString("0.0###########", CultureInfo.InvariantCulture)}\n\t\t\tmyQuantityMin {quantityMin}\n\t\t\tmyQuantityMax {quantityMax}\n\t\t\tmyItem {itemUid}\n\t\t}}\n");

        if (body.Contains("\r\n", StringComparison.Ordinal))
        {
            entry = entry.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        Result<JuiceDocument> dropped = document.WithBlockEntry(LootField, entry);
        return dropped.IsSuccess
            ? Result.Ok(dropped.Value.Bytes)
            : dropped.Refusal;
    }

    /// <summary>The story states a vendor entry may name.</summary>
    /// <remarks>
    /// Transcribed from the game's own <c>GameProgressionEnum</c>. The
    /// deprecated fourth day is included because the file may still hold it and
    /// this is a list of what parses, not of what anyone should choose.
    /// </remarks>
    public static readonly System.Collections.Immutable.ImmutableArray<string> GameStates =
    [
        "Day_1", "Day_2", "Day_3", "DEPRECATED_USE_POLICESTATE_INSTEAD_Day_4",
        "NAMBLA", "Police_State", "Election", "Post_Game", "NONE",
        "A1S1_LOTR", "A1S2_SuperStart", "A1S3_MetRandy", "A1S4_DefeatMallRandy",
    ];

    private static bool IsGameState(string value)
    {
        foreach (string candidate in GameStates)
        {
            if (candidate.Equals(value, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The field a parent entry names its variants in.</summary>
    private const string PiecesField = "myExtraPieces";

    /// <summary>
    /// How many variants a template claims, counted by line rather than parsed.
    /// </summary>
    /// <remarks>
    /// The entries are one per line and are not brace blocks — each is a class,
    /// a name, a uid and <c>= </c> the record it points at — so the block's
    /// non-empty lines are its members. This is a count for a warning, not a
    /// reading of what the variants are.
    /// </remarks>
    private static int CountExtraPieces(JuiceDocument document)
    {
        if (!document.TryGetField(PiecesField, out JuiceField pieces) || !pieces.IsBlock)
        {
            return 0;
        }

        string body = Encoding.Latin1.GetString(
            document.Bytes.Span[pieces.BlockStart..pieces.BlockEnd]);

        int count = 0;
        foreach (string line in body.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether a block already names something, looking only inside it.</summary>
    /// <remarks>
    /// The scope is the point. Asking the whole file would contradict the game's
    /// own data: 98 of 269 stocked items are sold in more than one shop — one in
    /// twenty-one of them — and 93 of 472 granted items are listed by more than
    /// one starting-inventory setting. No shop or setting lists one twice, so
    /// the container is where the duplicate is a mistake.
    /// </remarks>
    private static bool Lists(string body, JuiceField block, string uid) =>
        body.AsSpan(block.BlockStart, block.BlockEnd - block.BlockStart)
            .Contains(uid, StringComparison.Ordinal);

    private static int CountEntries(string body, JuiceField block)
    {
        int count = 0;
        int depth = 0;

        // Walked backwards from the block's close to its open, counting the
        // braces that sit directly inside it. Counting the word "VendorItem"
        // would work today and break on the first block holding anything else.
        for (int i = block.BlockEnd - 1; i >= 0; i--)
        {
            char c = body[i];
            if (c == '}')
            {
                depth++;
            }
            else if (c == '{')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
                if (depth == 0)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The uid a given name always mints to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deterministic, because determinism is the product: the same request must
    /// produce the same bytes, and a random uid would make a mod unreproducible
    /// and its patches unstable. Derived from SHA-256 of the name, which spreads
    /// far more finely than the shipped uids do — those share only 22 distinct
    /// leading prefixes across 3,532 values, so they are minted from a machine
    /// and a clock rather than from content, and imitating that would be
    /// imitating an accident.
    /// </para>
    /// <para>
    /// The consumer says the same thing more strongly than the corpus can. A uid
    /// is two 64-bit words, and the registry hashes and compares both of them
    /// whole; no half is masked, shifted or interpreted, and the one value with
    /// a meaning of its own is all zeroes. So the only properties a minted uid
    /// needs are the right width, uniqueness and not being zero — which a digest
    /// gives by construction.
    /// </para>
    /// </remarks>
    public static string MintUid(string seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(digest.AsSpan(0, JuiceDocument.UidDigits / 2));
    }

    /// <summary>The <c>myModel</c> value naming one model for both nodes.</summary>
    /// <remarks>
    /// One shape covers every item that has this field — 857 of 857 name
    /// <c>prefab:Skeleton</c> with <c>MMAFile</c> and <c>prefab:UberMeshCamel</c>
    /// with <c>ModelFile</c>, both pointing at the same <c>.mmb</c>. Because the
    /// census found no second shape, this builds rather than edits, and there is
    /// no branch here that nothing would exercise.
    /// </remarks>
    private static string ModelValue(string modelPath) => string.Create(
        CultureInfo.InvariantCulture,
        $"\"[\\\"prefab:Skeleton\\\"] = {{ MMAFile = \\\"{modelPath}\\\", }}, [\\\"prefab:UberMeshCamel\\\"] = {{ ModelFile = \\\"{modelPath}\\\", }}\"");

    /// <summary>
    /// Sets a declaration's shown name, minting the guid it resolves through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Public and neutrally named because <b>items are not the only things with
    /// one</b>: an <c>.mnpc</c> carries a <c>myUIName</c> of exactly the same
    /// shape, so a character's name is set the same way and through the same
    /// locpack. One implementation rather than two that could drift.
    /// </para>
    /// <para>
    /// The guid must change. Reusing the template's would make two things share
    /// a name, so that renaming either renamed both — and once the guid moves,
    /// the metadata around it describes an entry that no longer exists, which is
    /// why the blob is rebuilt rather than patched.
    /// </para>
    /// </remarks>
    public static Result<JuiceDocument> WithUiName(
        JuiceDocument document, string name, string displayName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(displayName);

        return WithDisplayName(document, name, displayName);
    }

    private static Result<JuiceDocument> WithDisplayName(
        JuiceDocument document, string name, string displayName)
    {
        if (!document.TryGetField(NameField, out JuiceField field) || field.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{document.DeclaredClass}' carries no {NameField}, so it has no name to change."));
        }

        // The blob is rebuilt rather than patched in place. Its guid must change
        // — reusing the template's would make both items share a name, and
        // changing one would change the other — and once the guid moves, the
        // metadata around it describes an entry that no longer exists.
        string guid = MintUid(name + " name");
        return document.WithField(NameField, string.Create(
            CultureInfo.InvariantCulture,
            $"\"contextComment = \\\"{name}\\\", description = \\\"{name}\\\", enabled = true, guid = #{guid}, lineVersion = 0, maxLength = {displayName.Length}, text = \\\"{displayName}\\\"\""));
    }
}
