using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Reading the wearable-item catalogue, against invented items.
/// </summary>
/// <remarks>
/// The ids, names and model paths here are made up. What these exercise is the
/// shape of the grammar — a record that owns others, and a record that states
/// what it covers — which does not care what anything is called. The record
/// type names are the grammar's own vocabulary and are what <c>myHideSlot</c>
/// speaks, so those are real.
/// </remarks>
public sealed class CostumeCatalogueTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-costume-").FullName;
    private readonly List<string> _paths = [];

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_hairstyle_is_one_entry_offering_every_way_it_can_be_drawn()
    {
        // The shape that made this worth doing: the game's menu offers one
        // hairstyle, and the files hold a parent and one record per way of
        // cutting it. Four entries here would be three the game never offers.
        Write("hair", Hairstyle("style", "Long Bob"));

        CostumeItem item = Assert.Single(Read());

        Assert.Equal("Long Bob", item.Name);
        Assert.Equal("Hair", item.Slot);
        Assert.Equal(
            [
                "assets/style_bangs.mmb",
                "assets/style_full.mmb",
                "assets/style_high.mmb",
                "assets/style_skull.mmb",
            ],
            item.Variants.Select(piece => piece.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_one_way_of_drawing_a_piece_reaches_the_file()
    {
        // The variants are alternatives, cut for different amounts of headwear,
        // and they occupy the same space. Drawing them together is the same
        // hairstyle four times in one place, which is what a costume looked
        // like when this was read as regions worn at once.
        Write("hair", Hairstyle("style", "Long Bob"));

        Assert.Equal(["assets/style_full.mmb"], CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath));
    }

    [Fact]
    public void The_whole_hairstyle_is_the_one_drawn_by_default()
    {
        // Full is the version for a bare head, which is the only case the files
        // can settle. 80 of the 81 hairstyles have one.
        Write("hair", Hairstyle("style", "Long Bob"));

        CostumeItem item = Assert.Single(Read());
        Assert.Equal("StreetHairFull", item.Default.Kind);
    }

    [Fact]
    public void A_hairstyle_with_no_whole_version_still_has_a_default()
    {
        // One hairstyle in the archives offers only the closest cut. Falling
        // back to the first declared keeps the answer the same on every run.
        Write("hair",
            Record("CostumeItemStreetHair", "style", name: "Crop",
                owns: [("CostumeItemStreetHairSkull", "style_skull")]),
            Record("CostumeItemStreetHairSkull", "style_skull", model: "assets/style_skull.mmb"));

        CostumeItem item = Assert.Single(Read());
        Assert.Equal("StreetHairSkull", item.Default.Kind);
    }

    [Fact]
    public void The_variant_asked_for_is_the_one_drawn()
    {
        // Which cut the game picks for a given hat is not in these files, so
        // the choice is the caller's and this is where it is honoured.
        Write("hair", Hairstyle("style", "Long Bob"));

        CostumeItem item = Assert.Single(Read());
        CostumePiece skull = item.Variants.Single(v => v.Kind == "StreetHairSkull");

        Assert.Equal(
            ["assets/style_skull.mmb"],
            CostumeCatalogue.Wear([new CostumeCatalogue.CostumeWorn(item, skull)]).Select(d => d.ModelPath));
    }

    [Fact]
    public void A_parent_that_draws_nothing_itself_is_still_an_entry()
    {
        // 42 of the 81 hairstyles own pieces without naming a model of their
        // own. Taking only records that name a model drops exactly those, which
        // is half the hair list gone while the list still looks populated.
        Write("hair", Hairstyle("style", "Long Bob"));

        CostumeItem item = Assert.Single(Read());

        Assert.Equal(4, item.Variants.Length);
        Assert.DoesNotContain(item.Variants, piece => piece.Kind == "StreetHair");
    }

    [Fact]
    public void A_record_owned_by_another_is_never_an_entry_of_its_own()
    {
        Write("hair", Hairstyle("style", "Long Bob"));

        // Structural rather than a list of type names to keep current: what
        // makes a record a piece is that something claims it.
        Assert.DoesNotContain(Read(), item => item.Kind.StartsWith("StreetHair", StringComparison.Ordinal)
            && item.Kind.Length > "StreetHair".Length);
    }

    [Fact]
    public void An_unclaimed_piece_is_an_entry_rather_than_something_lost()
    {
        Write("hair", Record("CostumeItemStreetHairTop", "loose", model: "assets/loose.mmb"));

        CostumeItem item = Assert.Single(Read());
        Assert.Equal("StreetHairTop", item.Kind);
    }

    [Fact]
    public void The_slots_are_the_ones_the_game_s_own_menu_shows()
    {
        Write("kit",
            Record("CostumeItemStreetMakeup2", "accent", model: "assets/accent.mmb"),
            Record("CostumeItemBody", "coat", model: "assets/coat.mmb"),
            Record("CostumeItemHead", "hat", model: "assets/hat.mmb"));

        // The names, and the order the screen puts them in — not the record
        // types, and not alphabetical.
        Assert.Equal(["Head", "Clothes", "Accent Makeup"], CostumeCatalogue.Slots(Read()));
    }

    [Fact]
    public void A_kind_this_build_has_never_heard_of_keeps_its_own_name()
    {
        // The backstory set is not on that screen at all. Naming it by its
        // record type is worse than a menu name and far better than dropping it.
        Write("kit", Record("CostumeItemBackstoryHead", "flashback", model: "assets/flashback.mmb"));

        Assert.Equal(["BackstoryHead"], CostumeCatalogue.Slots(Read()));
    }

    [Fact]
    public void Excluding_a_variant_does_not_choose_between_the_rest()
    {
        // A headpiece names about two variants of six, so it cannot be what
        // selects one of the four left. Reading it as a selector is what drew a
        // hairstyle several times over, so it is deliberately inert here: the
        // hat and the hairstyle's default, and nothing else.
        Write("kit",
            Hairstyle("style", "Long Bob"),
            Record("CostumeItemHead", "hat", model: "assets/hat.mmb",
                hides: ["StreetHairHigh", "StreetHairBangs"]));

        Assert.Equal(
            ["assets/hat.mmb", "assets/style_full.mmb"],
            CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_cut_the_headpiece_rules_out_is_not_the_one_drawn()
    {
        // Seven headpieces in the archives exclude the whole-head version, and
        // drawing it anyway is drawing the one combination the game says does
        // not go together. Which of the rest it would pick is not recorded
        // anywhere, so the first that survives is taken and the choice stays in
        // front of the user.
        Write("kit",
            Hairstyle("style", "Long Bob"),
            Record("CostumeItemHead", "hood", model: "assets/hood.mmb",
                hides: ["StreetHairFull", "StreetHairBangs"]));

        Assert.Equal(
            ["assets/hood.mmb", "assets/style_high.mmb"],
            CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_headpiece_that_leaves_no_cut_standing_is_worn_without_hair()
    {
        // Four of the 90 cover the head completely. Drawing hair anyway is
        // drawing it through a helmet, which is what the report was about.
        Write("kit",
            Hairstyle("style", "Long Bob"),
            Record("CostumeItemHead", "helm", model: "assets/helm.mmb",
                hides: ["StreetHairFull", "StreetHairBangs", "StreetHairHigh", "StreetHairSkull"]));

        Assert.Equal(["assets/helm.mmb"], CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath));
    }

    [Fact]
    public void A_cut_asked_for_by_name_is_drawn_even_where_the_outfit_rules_it_out()
    {
        // The pane offers every cut. Silently dropping the one somebody picked
        // is worse than showing a combination the game would not.
        Write("kit",
            Hairstyle("style", "Long Bob"),
            Record("CostumeItemHead", "helm", model: "assets/helm.mmb",
                hides: ["StreetHairFull", "StreetHairBangs", "StreetHairHigh", "StreetHairSkull"]));

        ImmutableArray<CostumeItem> items = Read();
        CostumeItem hair = items.Single(i => i.Slot == "Hair");
        CostumePiece full = hair.Variants.Single(v => v.Kind == "StreetHairFull");
        CostumeItem helm = items.Single(i => i.Slot == "Head");

        Assert.Equal(
            ["assets/helm.mmb", "assets/style_full.mmb"],
            CostumeCatalogue.Wear(
                [new CostumeCatalogue.CostumeWorn(helm), new CostumeCatalogue.CostumeWorn(hair, full)])
                .Select(d => d.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_hide_row_is_an_override_of_the_schema_default_not_a_list_entry()
    {
        // The finding this all turned on. A headpiece hides every hair category
        // by default and names the ones it allows, so `myHideSlot6 None` means
        // "the skull cut is what goes under this one". Read as a plain list the
        // row means nothing at all, the whole-head cut stays visible, and the
        // hair is drawn through the hat.
        Write("kit",
            Hairstyle("style", "Long Bob"),
            Record("CostumeItemHead", "cap", model: "assets/cap.mmb", slots: [(6, "None")]));
        WriteSchema(
            "class CostumeItemBase\n{\n\tItemCategory myHideSlot1 None\n}\n" +
            "class CostumeItemHead : CostumeItemBase\n{\n" +
            "\tItemCategory myHideSlot2 StreetHairBangs\n" +
            "\tItemCategory myHideSlot3 StreetHairFull\n" +
            "\tItemCategory myHideSlot4 StreetHairHigh\n" +
            "\tItemCategory myHideSlot6 StreetHairSkull\n}\n");

        // Skull, not Full -- and Full is what the list reading gives.
        Assert.Equal(
            ["assets/cap.mmb", "assets/style_skull.mmb"],
            CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void The_schema_s_defaults_reach_a_class_through_the_one_it_extends()
    {
        // Head takes myHideSlot8 from the base and adds its own, and an item
        // that says nothing inherits both.
        Write("kit",
            Record("CostumeItemStreetEyewear", "specs", model: "assets/specs.mmb"),
            Record("CostumeItemHead", "helm", model: "assets/helm.mmb"));
        WriteSchema(
            "class CostumeItemBase\n{\n\tItemCategory myHideSlot8 StreetEyewear\n}\n" +
            "class CostumeItemHead : CostumeItemBase\n{\n\tItemCategory myHideSlot1 StreetHair\n}\n");

        Assert.Equal(["assets/helm.mmb"], CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath));
    }

    [Fact]
    public void No_schema_at_all_leaves_the_rows_meaning_what_they_say()
    {
        // A mod folder need not carry one, and the catalogue is still worth
        // having: the rows are then read as written, which is what this did
        // before the schema was known about.
        Write("kit",
            Record("CostumeItemStreetEyewear", "specs", model: "assets/specs.mmb"),
            Record("CostumeItemHead", "helm", model: "assets/helm.mmb"));

        Assert.Equal(
            ["assets/helm.mmb", "assets/specs.mmb"],
            CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Covering_a_whole_slot_removes_the_entry()
    {
        Write("kit",
            Record("CostumeItemStreetEyewear", "specs", model: "assets/specs.mmb"),
            Record("CostumeItemHead", "helmet", model: "assets/helmet.mmb", hides: ["StreetEyewear"]));

        Assert.Equal(["assets/helmet.mmb"], CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath));
    }

    [Fact]
    public void A_piece_never_hides_itself()
    {
        // Costs nothing to guarantee, and would otherwise be an entry that
        // vanishes the moment it is chosen.
        Write("kit", Record("CostumeItemHead", "hat", model: "assets/hat.mmb", hides: ["Head"]));

        Assert.Equal(["assets/hat.mmb"], CostumeCatalogue.Wear(Read()).Select(d => d.ModelPath));
    }

    [Fact]
    public void A_garment_is_worn_instead_of_the_body_and_the_rest_on_top_of_it()
    {
        // The distinction that decides whether the character keeps its head.
        // Face paint and a hairstyle sit on the character; a suit is worn
        // instead of what is under it, and leaving both draws underwear
        // through it.
        Write("kit",
            Record("CostumeItemHead", "hat", model: "assets/hat.mmb"),
            Record("CostumeItemBody", "suit", model: "assets/suit.mmb"),
            Record("CostumeItemHands", "gloves", model: "assets/gloves.mmb"),
            Record("CostumeItemStreetMakeup", "paint", model: "assets/paint.mmb"),
            Record("CostumeItemStreetFacialHair", "beard", model: "assets/beard.mmb"),
            Record("CostumeItemStreetEyewear", "specs", model: "assets/specs.mmb"),
            Hairstyle("style", "Long Bob"));

        Dictionary<string, bool> replaces = Read().ToDictionary(i => i.Slot, i => i.Replaces);

        Assert.True(replaces["Head"]);
        Assert.True(replaces["Clothes"]);
        Assert.True(replaces["Hands"]);
        Assert.False(replaces["Hair"]);
        Assert.False(replaces["Eyewear"]);
        Assert.False(replaces["Facial Hair"]);
        Assert.False(replaces["Base Makeup"]);
    }

    [Fact]
    public void A_record_that_draws_nothing_and_owns_nothing_is_left_out()
    {
        // Most of the file is something else entirely — a consumable, a
        // starting inventory — so this is the ordinary case, not a fault.
        Write("kit",
            Record("CostumeItemTuningData", "tuning"),
            Record("CostumeItemHead", "hat", model: "assets/hat.mmb"));

        CostumeItem item = Assert.Single(Read());
        Assert.Equal("Head", item.Slot);
    }

    [Fact]
    public void Ownership_that_runs_two_deep_is_followed_all_the_way()
    {
        // A shape the archives really hold: one region record owns a piece of
        // its own. Following only the first level would leave that model out
        // of a hairstyle that looks complete.
        Write("hair",
            Record("CostumeItemStreetHair", "a", owns: [("CostumeItemStreetHairTop", "b")]),
            Record("CostumeItemStreetHairTop", "b", model: "assets/b.mmb",
                owns: [("CostumeItemStreetHairSkull", "c")]),
            Record("CostumeItemStreetHairSkull", "c", model: "assets/c.mmb"));

        CostumeItem item = Assert.Single(Read());
        Assert.Equal(
            ["assets/b.mmb", "assets/c.mmb"],
            item.Variants.Select(p => p.ModelPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Ownership_that_leads_back_on_itself_stops_rather_than_running_forever()
    {
        // Authored data, so a cycle is possible. Reaching this assertion at all
        // is the test: without the guard against revisiting, the walk never
        // returns and the whole suite hangs rather than failing. Both records
        // are claimed by the other, so neither leads a list -- an odd outcome,
        // and a far better one than not finishing.
        Write("hair",
            Record("CostumeItemStreetHair", "a", model: "assets/a.mmb", owns: [("CostumeItemStreetHairTop", "b")]),
            Record("CostumeItemStreetHairTop", "b", model: "assets/b.mmb", owns: [("CostumeItemStreetHair", "a")]));

        Assert.Empty(Read());
    }

    [Fact]
    public void No_item_definitions_at_all_is_refused_rather_than_answered_with_nothing()
    {
        using ContentSources content = new(_root, sdfRoot: null);

        Result<ImmutableArray<CostumeItem>> read = CostumeCatalogue.Read(
            content, [new SdfPathEntry("camel/elsewhere/thing.mmb", 0, IsDirectory: false)]);

        Assert.False(read.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal!.Kind);
    }

    // --- fixtures ------------------------------------------------------------

    /// <summary>A parent and the variants it owns, as the hair records are shaped.</summary>
    private static string Hairstyle(string id, string name) => string.Concat(
        Record("CostumeItemStreetHair", id, name: name, owns:
        [
            ("CostumeItemStreetHairBangs", id + "_bangs"),
            ("CostumeItemStreetHairFull", id + "_full"),
            ("CostumeItemStreetHairHigh", id + "_high"),
            ("CostumeItemStreetHairSkull", id + "_skull"),
        ]),
        Record("CostumeItemStreetHairBangs", id + "_bangs", model: $"assets/{id}_bangs.mmb"),
        Record("CostumeItemStreetHairFull", id + "_full", model: $"assets/{id}_full.mmb"),
        Record("CostumeItemStreetHairHigh", id + "_high", model: $"assets/{id}_high.mmb"),
        Record("CostumeItemStreetHairSkull", id + "_skull", model: $"assets/{id}_skull.mmb"));

    private static string Record(
        string type,
        string id,
        string? name = null,
        string? model = null,
        string[]? hides = null,
        (int Slot, string Kind)[]? slots = null,
        (string Type, string Id)[]? owns = null)
    {
        List<string> lines = [$"{type} {id} < uid={id.GetHashCode(StringComparison.Ordinal):X32} >", "{"];

        if (name is not null)
        {
            lines.Add($"\tmyUIName \"enabled = true, lineVersion = 0, text = \\\"{name}\\\"\"");
        }

        if (model is not null)
        {
            lines.Add($"\tmyModel \"[\\\"prefab:Skeleton\\\"] = {{ MMAFile = \\\"{model}\\\", }}\"");
        }

        for (int i = 0; i < (hides?.Length ?? 0); i++)
        {
            lines.Add($"\tmyHideSlot{i + 1} {hides![i]}");
        }

        foreach ((int slot, string kind) in slots ?? [])
        {
            lines.Add($"\tmyHideSlot{slot} {kind}");
        }

        // The slots the game leaves empty are written out too, as the word
        // "None" rather than as an absent line.
        lines.Add("\tmyHideSlot9 None");

        if (owns is not null)
        {
            lines.Add("\tmyExtraPieces");
            lines.Add("\t{");
            lines.AddRange(owns.Select((piece, i) =>
                $"\t\t{piece.Type} \"New {piece.Type} ({i})\" < uid={i:X32} > = {piece.Id}"));
            lines.Add("\t}");
        }

        lines.Add("}");
        return string.Join('\n', lines) + "\n\n";
    }

    private void WriteSchema(string text)
    {
        string full = Path.Combine(
            _root, CostumeCatalogue.SchemaFile.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        _paths.Add(CostumeCatalogue.SchemaFile);
    }

    private void Write(string file, params string[] records)
    {
        string virtualPath = CostumeCatalogue.ItemFolder + file + ".mitem";
        string full = Path.Combine(_root, virtualPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Concat(records));
        _paths.Add(virtualPath);
    }

    private ImmutableArray<CostumeItem> Read()
    {
        using ContentSources content = new(_root, sdfRoot: null);
        Result<ImmutableArray<CostumeItem>> read = CostumeCatalogue.Read(
            content, [.. _paths.Select((p, i) => new SdfPathEntry(p, i, IsDirectory: false))]);

        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal!.Message);
        return read.Value;
    }
}
