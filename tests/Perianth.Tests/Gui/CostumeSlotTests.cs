using System.Linq;
using System.Collections.Immutable;
using Perianth.Core.Content;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// Which cut of a hairstyle a slot asks for.
/// </summary>
/// <remarks>
/// A headpiece states which cuts may be worn under it and the catalogue works
/// out which one that leaves. Naming a cut in the pane <b>overrides</b> that,
/// deliberately — somebody who asks for the whole hairstyle under a helmet
/// should get it. So what the pane asks for by itself decides whether the rule
/// runs at all, and it used to name the whole-head cut, which meant every hat
/// was worn over the full hairstyle and the rule never ran once.
/// </remarks>
public sealed class CostumeSlotTests
{
    [Fact]
    public void A_freshly_chosen_piece_names_no_cut_of_its_own()
    {
        CostumeSlot slot = new("Hair");
        CostumeChoice hair = new(Style());
        slot.Items.Add(hair);

        slot.Chosen = hair;

        Assert.NotNull(slot.Variant);
        Assert.Null(slot.Variant!.Piece);
    }

    [Fact]
    public void The_cuts_are_still_offered_underneath_it()
    {
        CostumeSlot slot = new("Hair");
        CostumeChoice hair = new(Style());
        slot.Items.Add(hair);

        slot.Chosen = hair;

        Assert.True(slot.HasVariants);
        Assert.Equal(3, slot.Variants.Count);
        Assert.Equal(["Full", "Skull"], slot.Variants.Skip(1).Select(v => v.Label));
    }

    [Fact]
    public void A_piece_with_one_way_of_being_drawn_offers_no_choice()
    {
        CostumeSlot slot = new("Clothes");
        CostumeChoice body = new(new CostumeItem(
            "Suit", "Clothes", "Body", [new CostumePiece("Body", "suit.mmb")], [], []));
        slot.Items.Add(body);

        slot.Chosen = body;

        Assert.False(slot.HasVariants);
    }

    private static CostumeItem Style() => new(
        "Afro Puffs", "Hair", "StreetHair",
        [
            new CostumePiece("StreetHairFull", "afro_full.mmb"),
            new CostumePiece("StreetHairSkull", "afro_skull.mmb"),
        ],
        [], []);
}
