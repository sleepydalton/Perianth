using System;
using System.Collections.Immutable;
using System.Text;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Deriving a new item from a shipped one.
/// </summary>
/// <remarks>
/// The fixtures are invented, as the repository requires — the grammar is what
/// is under test, and no shipped name, uid or path belongs here. The shapes they
/// use are the measured ones: a costume class name carrying its slot, a
/// <c>myModel</c> naming the same file for both nodes, and a localisation blob
/// whose guid is what the display name actually resolves through.
/// </remarks>
public sealed class ItemEditTests
{
    // The identifiers below are invented -- repeated nibbles, and the hex
    // digits counted up and back down. A uid is 128 opaque bits, so a fixture
    // needs one that is well-formed rather than one that is real, and the
    // game's own belong here no more than its textures do.
    //
    // This line is read by the content scan, which cannot tell an invented
    // identifier from a real one and so takes the claim from whoever wrote it.
    //
    // scan-ok: identifiers here are invented

    private const string Template =
        "include \"made/up/items.fruit\"\n" +
        "\n" +
        "CostumeItemStreetHairLow made_up_template_hair_low < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyUIName \"contextComment = \\\"made_up\\\", description = \\\"made_up\\\", enabled = true, guid = #AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, lineVersion = 0, maxLength = 7, text = \\\"Old Hat\\\"\"\n" +
        "\tmyModel \"[\\\"prefab:Skeleton\\\"] = { MMAFile = \\\"made/up/old.mmb\\\", }, [\\\"prefab:UberMeshCamel\\\"] = { ModelFile = \\\"made/up/old.mmb\\\", }\"\n" +
        "\tmyDefaultTint1 BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\n" +
        "}\n";

    private static SourceFile File(string text) =>
        SourceFile.FromMemory("made-up.mitem", Encoding.UTF8.GetBytes(text));

    private static string Derived(string name, string model, string? display = null) =>
        Encoding.UTF8.GetString(
            ItemEdit.Derive(File(Template), name, model, display).Value.Item.Span);

    [Fact]
    public void The_slot_comes_from_the_template_and_is_never_set()
    {
        string text = Derived("brand_new_hair_low", "made/up/new.mmb");

        Assert.StartsWith(
            "include \"made/up/items.fruit\"\n\nCostumeItemStreetHairLow brand_new_hair_low <",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_model_nodes_are_repointed_together()
    {
        string text = Derived("brand_new_hair_low", "made/up/new.mmb");

        Assert.Contains("MMAFile = \\\"made/up/new.mmb\\\"", text, StringComparison.Ordinal);
        Assert.Contains("ModelFile = \\\"made/up/new.mmb\\\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("old.mmb", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fields_the_operation_does_not_understand_survive_exactly()
    {
        string text = Derived("brand_new_hair_low", "made/up/new.mmb");

        Assert.Contains(
            "\tmyDefaultTint1 BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\n}\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_uid_is_minted_from_the_name_and_never_the_templates()
    {
        ItemDerivation first = ItemEdit.Derive(File(Template), "brand_new", "made/up/new.mmb").Value;
        ItemDerivation again = ItemEdit.Derive(File(Template), "brand_new", "made/up/new.mmb").Value;
        ItemDerivation other = ItemEdit.Derive(File(Template), "different", "made/up/new.mmb").Value;

        Assert.True(JuiceDocument.IsUid(first.Uid));
        Assert.Equal(first.Uid, again.Uid);          // determinism is the product
        Assert.NotEqual(first.Uid, other.Uid);
        Assert.NotEqual("0123456789ABCDEF0123456789ABCDEF", first.Uid);
    }

    [Fact]
    public void A_display_name_moves_the_guid_as_well_as_the_text()
    {
        ItemDerivation made = ItemEdit
            .Derive(File(Template), "brand_new", "made/up/new.mmb", "Brand New Hat").Value;
        string text = Encoding.UTF8.GetString(made.Item.Span);

        // Keeping the template's guid would make both items share one name, so
        // that a later edit to either changed both.
        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", text, StringComparison.Ordinal);
        Assert.Contains("text = \\\"Brand New Hat\\\"", text, StringComparison.Ordinal);
        // The guid it reports must be the guid it wrote, or a caller would key
        // the localisation row to something the item never mentions.
        Assert.Equal(ItemEdit.MintUid("brand_new name"), made.NameGuid);
        Assert.Contains($"guid = #{made.NameGuid}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_display_name_nothing_claims_one()
    {
        ItemDerivation made = ItemEdit.Derive(File(Template), "brand_new", "made/up/new.mmb").Value;

        Assert.Null(made.NameGuid);
        Assert.Null(made.DisplayName);
        Assert.Contains("text = \\\"Old Hat\\\"", Encoding.UTF8.GetString(made.Item.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_that_wears_nothing_is_refused()
    {
        Result<ItemDerivation> result = ItemEdit.Derive(
            File("ComponentItem made_up < uid=0123456789ABCDEF0123456789ABCDEF >\n{\n\tmyMaxStackable 5\n}\n"),
            "brand_new",
            "made/up/new.mmb");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Theory]
    [InlineData("", "made/up/new.mmb")]
    [InlineData("brand_new", "")]
    public void An_empty_name_or_model_is_refused(string name, string model) =>
        Assert.False(ItemEdit.Derive(File(Template), name, model).IsSuccess);

    private const string Locpack =
        "1,,\r\n2,,\r\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA,0,First\r\nBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB,0,Second\r\n";

    private static SourceFile Pack(string text) =>
        SourceFile.FromMemory("made-up.locpack", Encoding.Latin1.GetBytes(text));

    [Fact]
    public void A_localisation_row_is_appended_and_the_count_kept_in_step()
    {
        string text = Encoding.Latin1.GetString(ItemEdit
            .AddLocalisation(Pack(Locpack), "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Third").Value.Span);

        Assert.Equal(
            "1,,\r\n3,,\r\nAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA,0,First\r\n" +
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB,0,Second\r\nCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC,0,Third\r\n",
            text);
    }

    [Fact]
    public void A_key_the_pack_already_carries_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit
            .AddLocalisation(Pack(Locpack), "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Replacement");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_row_cannot_carry_a_newline()
    {
        Assert.False(ItemEdit
            .AddLocalisation(Pack(Locpack), "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Two\r\nLines")
            .IsSuccess);
    }

    [Fact]
    public void A_pack_without_a_count_header_refuses()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit
            .AddLocalisation(Pack("1,,\r\n"), "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Third");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    private const string Vendors =
        "VendorConfig \"Made Up Shop\" < uid=0123456789ABCDEF0123456789ABCDEF >\r\n" +
        "{\r\n" +
        "\tmyVendorItemList\r\n" +
        "\t{\r\n" +
        "\t\tVendorItem 0\r\n\t\t{\r\n\t\t\tmyItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\r\n\t\t\tmyGameState Day_1\r\n\t\t}\r\n" +
        "\t}\r\n" +
        "\tmyVendorGroup \"made up\"\r\n" +
        "}\r\n" +
        "VendorConfig \"Other Shop\" < uid=FEDCBA9876543210FEDCBA9876543210 >\r\n" +
        "{\r\n\tmyVendorItemList\r\n\t{\r\n\t}\r\n}\r\n";

    private static SourceFile VendorFile() =>
        SourceFile.FromMemory("made-up.mvendorconfig", Encoding.Latin1.GetBytes(Vendors));

    private static string Stocked(string vendor, string uid, string state) =>
        Encoding.Latin1.GetString(ItemEdit.Stock(VendorFile(), vendor, uid, state).Value.Span);

    [Fact]
    public void An_item_is_appended_to_the_named_shop_with_the_next_index()
    {
        string text = Stocked("Made Up Shop", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Day_2");

        Assert.Contains(
            "\t\tVendorItem 1\r\n\t\t{\r\n\t\t\tmyItem CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\r\n\t\t\tmyGameState Day_2\r\n\t\t}\r\n\t}",
            text,
            StringComparison.Ordinal);

        // The entry already there, and the field after the block, are untouched.
        Assert.Contains("VendorItem 0", text, StringComparison.Ordinal);
        Assert.Contains("\tmyVendorGroup \"made up\"\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_shop_in_the_same_file_is_left_alone()
    {
        string text = Stocked("Made Up Shop", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Day_1");

        Assert.Contains(
            "VendorConfig \"Other Shop\" < uid=FEDCBA9876543210FEDCBA9876543210 >\r\n{\r\n\tmyVendorItemList\r\n\t{\r\n\t}\r\n}\r\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_shop_takes_index_zero()
    {
        string text = Stocked("Other Shop", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Day_1");

        Assert.Contains("VendorItem 0\r\n\t\t{\r\n\t\t\tmyItem CCCC", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shop_the_file_does_not_declare_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result =
            ItemEdit.Stock(VendorFile(), "No Such Shop", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Day_1");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void Stocking_the_same_item_twice_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result =
            ItemEdit.Stock(VendorFile(), "Made Up Shop", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Day_1");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void Another_shop_may_stock_what_this_one_already_sells()
    {
        // 98 of the 269 items the game sells are on more than one shop's shelf,
        // one of them on twenty-one, so the duplicate a stock refuses is one
        // within the shop.
        string text = Stocked("Other Shop", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "Day_1");

        Assert.Equal(2, Occurrences(text, "myItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void A_story_state_the_game_does_not_have_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result =
            ItemEdit.Stock(VendorFile(), "Made Up Shop", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", "Day_9");

        Assert.False(result.IsSuccess);
        Assert.Contains("Day_1", result.Refusal.Message, StringComparison.Ordinal);
    }

    private const string Inventory =
        "StartingInventory starting_inventory\n" +
        "{\n\tmyStartingInventorySettings\n\t{\n" +
        "\t\tStartingInventorySetting CraftSettings\n\t\t{\n\t\t\tmyItemList\n\t\t\t{\n\t\t\t}\n\t\t}\n" +
        "\t\tStartingInventorySetting CostumeSettings\n\t\t{\n\t\t\tmyItemList\n\t\t\t{\n" +
        "\t\t\t\tStartingItemSetting made_up_old\n\t\t\t\t{\n\t\t\t\t\tmyItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n\t\t\t\t\tmyCount 1\n\t\t\t\t}\n" +
        "\t\t\t}\n\t\t}\n\t}\n}\n";

    private static SourceFile InventoryFile() =>
        SourceFile.FromMemory("made-up.juice", Encoding.Latin1.GetBytes(Inventory));

    [Fact]
    public void An_item_is_granted_through_a_named_setting()
    {
        string text = Encoding.Latin1.GetString(ItemEdit
            .Grant(InventoryFile(), "CostumeSettings", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 2)
            .Value.Span);

        Assert.Contains(
            "\t\t\t\tStartingItemSetting CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\n\t\t\t\t{\n" +
            "\t\t\t\t\tmyItem CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\n\t\t\t\t\tmyCount 2\n\t\t\t\t}\n\t\t\t}",
            text,
            StringComparison.Ordinal);

        // The setting that was already there, and the empty one before it, stay put.
        Assert.Contains("StartingItemSetting made_up_old", text, StringComparison.Ordinal);
        Assert.Contains("StartingInventorySetting CraftSettings\n\t\t{\n\t\t\tmyItemList\n\t\t\t{\n\t\t\t}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_setting_the_file_does_not_declare_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit
            .Grant(InventoryFile(), "NoSuchSettings", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void Granting_the_same_item_twice_is_refused()
    {
        Assert.False(ItemEdit
            .Grant(InventoryFile(), "CostumeSettings", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
            .IsSuccess);
    }

    [Fact]
    public void Another_setting_may_grant_what_this_one_already_does()
    {
        // 93 of the 472 items the game grants are listed by more than one
        // setting, so the duplicate a grant refuses is one within the setting.
        string text = Encoding.Latin1.GetString(ItemEdit
            .Grant(InventoryFile(), "CraftSettings", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
            .Value.Span);

        Assert.Equal(2, Occurrences(text, "myItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    [Fact]
    public void Granting_none_of_something_is_refused() =>
        Assert.False(ItemEdit
            .Grant(InventoryFile(), "CostumeSettings", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 0)
            .IsSuccess);

    private const string Recipes =
        "include \"camel/game system data/fruit/items/recipe.fruit\"\r\n" +
        "\r\n" +
        "RecipeItemTuningData \"Made Up Recipe_Tuning\"\r\n" +
        "{\r\n" +
        "\tmySellPrice 300.0\r\n" +
        "\tmyKeyItem FALSE\r\n" +
        "\tmyItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\r\n" +
        "\tmyIngredients\r\n\t{\r\n" +
        "\t\tIngredient 0\r\n\t\t{\r\n\t\t\tmyItem DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD\r\n\t\t\tmyCount 3\r\n\t\t}\r\n" +
        "\t}\r\n" +
        "\tmyMasterlyLevel 2\r\n" +
        "\tmyResult BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\r\n" +
        "\tmyOneTimeUse TRUE\r\n" +
        "}\r\n" +
        "\r\n";

    private static readonly ImmutableArray<CraftIngredient> OneScrap =
        [new CraftIngredient("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", 4)];

    private static SourceFile RecipeFile() =>
        SourceFile.FromMemory("made-up.juice", Encoding.Latin1.GetBytes(Recipes));

    private static Result<ReadOnlyMemory<byte>> Crafted(
        ImmutableArray<CraftIngredient> ingredients) => ItemEdit.Craft(
            RecipeFile(),
            "Made Up Recipe_Tuning",
            "Brand New Recipe_Tuning",
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            ingredients);

    [Fact]
    public void A_recipe_is_appended_whole_and_the_template_is_left_alone()
    {
        string text = Encoding.Latin1.GetString(Crafted(OneScrap).Value.Span);

        // The template survives byte for byte at the head of the file, which is
        // the whole claim: a copy is taken, not an edit made in place.
        Assert.StartsWith(Recipes, text, StringComparison.Ordinal);

        Assert.Contains(
            "RecipeItemTuningData \"Brand New Recipe_Tuning\"\r\n{\r\n" +
            "\tmySellPrice 300.0\r\n\tmyKeyItem FALSE\r\n" +
            "\tmyItem CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("\tmyResult FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_the_operation_does_not_understand_come_across_verbatim()
    {
        // A recipe carries sixteen fields and this names four. The rest are the
        // reason a template is copied rather than a declaration built.
        string text = Encoding.Latin1.GetString(Crafted(OneScrap).Value.Span);

        Assert.Equal(2, Occurrences(text, "\tmyMasterlyLevel 2\r\n"));
        Assert.Equal(2, Occurrences(text, "\tmyOneTimeUse TRUE\r\n"));
        Assert.Equal(2, Occurrences(text, "\tmySellPrice 300.0\r\n"));
    }

    [Fact]
    public void The_templates_ingredients_are_replaced_rather_than_added_to()
    {
        string text = Encoding.Latin1.GetString(Crafted(
            [new CraftIngredient("EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", 4),
             new CraftIngredient("11111111111111111111111111111111", 1)]).Value.Span);

        Assert.Contains(
            "\tmyIngredients\r\n\t{\r\n" +
            "\t\tIngredient 0\r\n\t\t{\r\n\t\t\tmyItem EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE\r\n\t\t\tmyCount 4\r\n\t\t}\r\n" +
            "\t\tIngredient 1\r\n\t\t{\r\n\t\t\tmyItem 11111111111111111111111111111111\r\n\t\t\tmyCount 1\r\n\t\t}\r\n" +
            "\t}\r\n",
            text,
            StringComparison.Ordinal);

        // Once, in the template it was copied from — not twice.
        Assert.Equal(1, Occurrences(text, "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD"));
    }

    [Fact]
    public void A_recipe_with_no_ingredients_is_refused() =>
        Assert.False(Crafted([]).IsSuccess);

    [Fact]
    public void A_result_the_file_already_crafts_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit.Craft(
            RecipeFile(),
            "Made Up Recipe_Tuning",
            "Brand New Recipe_Tuning",
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            OneScrap);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_template_the_file_does_not_declare_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit.Craft(
            RecipeFile(),
            "No Such Recipe",
            "Brand New Recipe_Tuning",
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            OneScrap);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void Renaming_a_declaration_that_states_no_uid_is_refused()
    {
        // A recipe has no uid, so its uid range is empty and sits at offset
        // zero. Splicing into it would write the digits over the head of the
        // file, and the result would still parse.
        JuiceDocument document = JuiceDocument.Read(RecipeFile(), "Made Up Recipe_Tuning").Value;

        Assert.False(document.HasUid);
        Assert.False(document.WithDeclaration("other", "FEDCBA9876543210FEDCBA9876543210").IsSuccess);
        Assert.True(document.WithName("other").IsSuccess);
    }

    private const string Loot =
        "include \"camel/game system data/fruit/items/loot.fruit\"\r\n" +
        "\r\n" +
        "LootTable Loot_Made_Up_Chest < uid=0123456789ABCDEF0123456789ABCDEF >\r\n" +
        "{\r\n\tmyLootEntries\r\n\t{\r\n" +
        "\t\tLootEntry 0\r\n\t\t{\r\n\t\t\tmyExclusionGroup None\r\n\t\t\tmyWeight -1\r\n\t\t\tmyChance 1.0\r\n" +
        "\t\t\tmyQuantityMin 1\r\n\t\t\tmyQuantityMax 1\r\n\t\t\tmyItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\r\n\t\t}\r\n" +
        "\t}\r\n\tmyTimefart1 \"\"\r\n}\r\n" +
        "LootTable Loot_Other_Chest < uid=FEDCBA9876543210FEDCBA9876543210 >\r\n" +
        "{\r\n\tmyLootEntries\r\n\t{\r\n\t}\r\n}\r\n";

    private static SourceFile LootFile() =>
        SourceFile.FromMemory("made-up.juice", Encoding.Latin1.GetBytes(Loot));

    [Fact]
    public void A_drop_is_appended_to_the_named_table_with_the_next_index()
    {
        string text = Encoding.Latin1.GetString(ItemEdit
            .Drop(LootFile(), "Loot_Made_Up_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 0.25, 2, 5)
            .Value.Span);

        Assert.Contains(
            "\t\tLootEntry 1\r\n\t\t{\r\n\t\t\tmyExclusionGroup None\r\n\t\t\tmyWeight -1\r\n" +
            "\t\t\tmyChance 0.25\r\n\t\t\tmyQuantityMin 2\r\n\t\t\tmyQuantityMax 5\r\n" +
            "\t\t\tmyItem CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC\r\n\t\t}\r\n\t}\r\n",
            text,
            StringComparison.Ordinal);

        Assert.Contains("\tmyTimefart1 \"\"\r\n", text, StringComparison.Ordinal);
        Assert.Contains("LootEntry 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_whole_chance_is_written_as_the_shipped_files_write_it()
    {
        // Every shipped chance is spelled with a decimal point — "1.0", never
        // "1" — and a field that reads as an integer where a float is expected
        // is the kind of thing that loads and then behaves oddly.
        string text = Encoding.Latin1.GetString(ItemEdit
            .Drop(LootFile(), "Loot_Other_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")
            .Value.Span);

        Assert.Contains("\t\t\tmyChance 1.0\r\n", text, StringComparison.Ordinal);
        Assert.Contains("LootEntry 0\r\n\t\t{", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_other_table_in_the_same_file_is_left_alone()
    {
        string text = Encoding.Latin1.GetString(ItemEdit
            .Drop(LootFile(), "Loot_Made_Up_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")
            .Value.Span);

        Assert.Contains(
            "LootTable Loot_Other_Chest < uid=FEDCBA9876543210FEDCBA9876543210 >\r\n{\r\n\tmyLootEntries\r\n\t{\r\n\t}\r\n}\r\n",
            text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void A_chance_outside_nought_to_one_is_refused(double chance) =>
        Assert.False(ItemEdit
            .Drop(LootFile(), "Loot_Made_Up_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", chance)
            .IsSuccess);

    [Fact]
    public void A_quantity_range_that_runs_backwards_is_refused() =>
        Assert.False(ItemEdit
            .Drop(LootFile(), "Loot_Made_Up_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", 1.0, 5, 2)
            .IsSuccess);

    [Fact]
    public void A_table_the_file_does_not_declare_is_refused()
    {
        Result<ReadOnlyMemory<byte>> result = ItemEdit
            .Drop(LootFile(), "Loot_No_Such_Chest", "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void One_table_may_drop_the_same_item_twice()
    {
        // Not a duplicate check that was forgotten. Nine of the game's own loot
        // tables list one item twice, with different chances and quantities, so
        // a second entry is authored rather than a mistake.
        string text = Encoding.Latin1.GetString(ItemEdit
            .Drop(LootFile(), "Loot_Made_Up_Chest", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 0.5)
            .Value.Span);

        Assert.Equal(2, Occurrences(text, "myItem AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }

    private static int Occurrences(string text, string needle)
    {
        int count = 0;
        for (int at = text.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
