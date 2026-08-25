using System;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// The wearable-item catalogue, against the game's own definitions.
/// </summary>
public sealed class CostumeCatalogueConformanceTests
{
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    /// <summary>What the game's character screen offers, in the order it offers it.</summary>
    private static readonly string[] MenuSlots =
    [
        "Head", "Clothes", "Hands", "Eyewear", "Hair", "Facial Hair", "Base Makeup", "Accent Makeup",
    ];

    [Fact]
    public void The_catalogue_lists_the_wearable_items_with_their_slots()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // 407 entries assembled from 3,038 definitions. Pinned loosely: an exact
        // count would break on a patched install for no reason, while an order
        // of magnitude is what a broken parse gets wrong.
        Assert.InRange(items.Length, 300, 600);

        // Every entry must be usable: something to draw, a slot, and a name.
        Assert.All(items, item =>
        {
            Assert.NotEmpty(item.Variants);
            Assert.All(item.Variants, piece => Assert.EndsWith(".mmb", piece.ModelPath, StringComparison.Ordinal));
            Assert.NotEmpty(item.Slot);
            Assert.NotEmpty(item.Name);
        });

        // 463 of the records carry a localised name and the rest have no name
        // field at all -- they are minimal records, not a parse failure, so
        // falling back to the id is the ordinary outcome for many of them. What
        // this pins is that the extraction works at all: a broken pattern would
        // fall back for every one.
        int displayed = items.Count(item => !item.Name.StartsWith("eqp_", StringComparison.Ordinal));
        Assert.True(displayed > items.Length / 3, $"only {displayed} of {items.Length} carry a displayed name");
    }

    [Fact]
    public void The_slots_are_the_eight_the_character_screen_shows()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        ImmutableArray<string> slots = CostumeCatalogue.Slots(items);

        // Eight record types map one-to-one onto the eight menu entries, and
        // this is where that claim is checked against the archives rather than
        // against a fixture. Anything after them is a kind that is not on that
        // screen at all -- the backstory set -- and is allowed.
        Assert.Equal(MenuSlots, slots.Take(MenuSlots.Length));
    }

    [Fact]
    public void A_hairstyle_is_one_entry_rather_than_one_per_way_of_cutting_it()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // Variants are not entries: each is claimed by the hairstyle that owns
        // it. Without this the Hair list is six times too long and holds none
        // of the entries the game actually offers.
        //
        // One of the 471 is left out of its own hairstyle's list by the game's
        // own data, so it leads a list of one. Adopting it by the shape of its
        // id would be a guessed association, which this project does not do.
        int loose = items.Count(item =>
            item.Kind.StartsWith("StreetHair", StringComparison.Ordinal) &&
            item.Kind.Length > "StreetHair".Length);
        Assert.True(loose <= 1, $"{loose} hair regions are entries of their own");

        ImmutableArray<CostumeItem> hair = [.. items.Where(item => item.Slot == "Hair")];
        Assert.InRange(hair.Length, 50, 150);

        // 73 hairstyles offer the full six-variant set.
        Assert.True(
            hair.Count(item => item.Variants.Length >= 6) > 50,
            $"only {hair.Count(item => item.Variants.Length >= 6)} hairstyles offer six variants");

        // 80 of the 81 have a whole-head version, which is what a bare head
        // wears and so what the default must be.
        Assert.True(
            hair.Count(item => item.Default.Kind == "StreetHairFull") > 70,
            "the whole-head version is not the usual default");
    }

    [Fact]
    public void A_whole_costume_draws_one_model_for_each_thing_worn()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // The regression this exists to stop. Every slot at once, including a
        // hairstyle with six variants: one model each, never a variant more.
        // Read as regions worn together, this drew eleven.
        CostumeItem[] outfit = [.. CostumeCatalogue.Slots(items)
            .Select(slot => items.First(item => item.Slot == slot))];

        Assert.True(outfit.Any(item => item.Variants.Length >= 6), "no multi-variant piece in the outfit");

        // Never more than one model per thing worn -- fewer only where one
        // piece covers another's whole slot, which is its own test.
        Assert.All(items, item => Assert.Single(CostumeCatalogue.Wear([item])));
        Assert.True(
            CostumeCatalogue.Wear(outfit).Length <= outfit.Length,
            $"{CostumeCatalogue.Wear(outfit).Length} models for {outfit.Length} things worn");
    }

    [Fact]
    public void A_piece_that_names_a_whole_slot_removes_what_is_worn_there()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // The premise for reading myHideSlot at all -- a handful of records
        // name a whole slot rather than a hair variant. If the archives ever
        // stop holding one this must fail rather than let Wear pass vacuously.
        var slots = items.Select(item => item.Kind).ToHashSet(StringComparer.Ordinal);
        CostumeItem cover = items.First(item => item.Hides.Any(slots.Contains));
        string hidden = cover.Hides.First(slots.Contains);
        CostumeItem covered = items.First(item => item.Kind == hidden);

        Assert.Equal(CostumeCatalogue.Wear([cover]), CostumeCatalogue.Wear([cover, covered]));
        Assert.NotEmpty(CostumeCatalogue.Wear([covered]));
    }

    [Fact]
    public void A_headpiece_states_which_cuts_may_be_worn_under_it()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        CostumeItem[] heads = [.. items.Where(item => item.Slot == "Head")];
        string[] cuts =
        [
            "StreetHairBangs", "StreetHairFull", "StreetHairHigh",
            "StreetHairLow", "StreetHairSkull", "StreetHairTop",
        ];

        // The schema gives every headpiece a default that hides all seven hair
        // categories, so a headpiece that hides none of them means the schema
        // was not read -- which is the whole difference between this and the
        // reading it replaced, and would otherwise pass unnoticed.
        Assert.All(heads, head => Assert.Contains(head.Hides, cuts.Contains));

        // 32 of the 90 leave exactly one cut standing and four leave none.
        // Pinned as bands: exact counts would break on a patched install.
        int decided = heads.Count(head => cuts.Count(c => !head.Hides.Contains(c)) == 1);
        int bald = heads.Count(head => !cuts.Any(c => !head.Hides.Contains(c)));
        Assert.InRange(decided, 20, 50);
        Assert.InRange(bald, 1, 15);

        // And the headpieces that cover the head completely draw no hair.
        CostumeItem helm = heads.First(head => !cuts.Any(c => !head.Hides.Contains(c)));
        CostumeItem hair = items.First(item => item.Slot == "Hair" && item.Variants.Length >= 6);

        Assert.Equal(CostumeCatalogue.Wear([helm]), CostumeCatalogue.Wear([helm, hair]));
    }

    [Fact]
    public void Some_headpieces_cover_the_eyewear_worn_with_them()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // 24 of the 90, and none of it visible without the schema -- the rows
        // that say so are the ones written as `None`. This is the same finding
        // as the hair one, reaching a slot nobody was looking at.
        int covering = items.Count(item =>
            item.Slot == "Head" && item.Hides.Contains("StreetEyewear"));

        Assert.InRange(covering, 10, 60);
    }

    [Fact]
    public void Every_listed_model_is_one_the_archives_hold()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to run the catalogue against the archives");
            return;
        }

        using SdfContentSource source = new(root);
        ImmutableArray<SdfPathEntry> paths = source.Paths().Value;
        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        ImmutableArray<CostumeItem> items = CostumeCatalogue.Read(content, paths).Value;

        // An item naming a model that is not there would be offered and then
        // refuse on export, which is the worst moment to find out.
        var held = paths.Select(p => SdfIndex.NormalizePath(p.Path))
            .ToHashSet(StringComparer.Ordinal);

        string[] missing = [.. items.SelectMany(i => i.Variants).Select(p => p.ModelPath)
            .Distinct(StringComparer.Ordinal)
            .Where(p => !held.Contains(p)).Take(5)];

        Assert.True(missing.Length == 0, "models named but absent: " + string.Join(", ", missing));
    }

    /// <summary>The catalogue, or a default array when the archives are not here.</summary>
    [Fact]
    public void The_archives_hold_three_outfits_that_are_never_worn_together()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        string[] outfits = [.. items.Select(item => item.Outfit).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.Equal(["All", "Backstory", "Hero", "Street"], outfits);

        // Nine entries belong to an outfit other than Hero: two street bodies,
        // two street hands, and one backstory head, body and pair of hands. Each
        // draws a whole garment, so any two of the three outfits worn at once is
        // one suit inside another.
        Assert.Equal(2, items.Count(item => string.Equals(item.Outfit, "Street", StringComparison.Ordinal) && item.Slot == "StreetBody"));
        Assert.Equal(3, items.Count(item => string.Equals(item.Outfit, "Backstory", StringComparison.Ordinal)));

        // The shared pieces are the bulk of the list and go with whatever is
        // worn -- hair, facial hair, most eyewear, most makeup. A rule that made
        // these exclusive would refuse an ordinary outfit.
        Assert.InRange(items.Count(item => !item.IsExclusive), 100, 200);

        // And the guard itself, on the real names the reports used.
        CostumeItem street = items.First(item => item.Slot == "StreetHands");
        CostumeItem hero = items.First(item => item.Slot == "Hands");
        CostumeItem hair = items.First(item => item.Slot == "Hair");

        Assert.True(CostumeCatalogue.Outfit([street, hair]).IsSuccess);
        Assert.True(CostumeCatalogue.Outfit([hero, hair]).IsSuccess);
        Assert.True(CostumeCatalogue.Outfit([street, hero]).IsRefused);
    }

    [Fact]
    public void Some_records_name_an_outfit_their_class_does_not()
    {
        ImmutableArray<CostumeItem> items = Catalogue();
        if (items.IsDefault)
        {
            return;
        }

        // 23 of them, all moving a shared piece into the hero outfit: nine
        // eyewear and fourteen accent makeup, which belong to a costume rather
        // than being choices of their own. Read from the class alone every one
        // of these is worn with everything, so this is the override doing work.
        Assert.NotEmpty(items.Where(item =>
            item.Slot is "Eyewear" or "Accent Makeup" && item.IsExclusive));
    }

    private static ImmutableArray<CostumeItem> Catalogue()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to run the catalogue against the archives");
            return default;
        }

        using SdfContentSource source = new(root);
        ImmutableArray<SdfPathEntry> paths = source.Paths().Value;
        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        Result<ImmutableArray<CostumeItem>> read = CostumeCatalogue.Read(content, paths);
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal!.Message);
        return read.Value;
    }
}
