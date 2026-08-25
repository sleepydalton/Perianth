using System;
using Perianth.Pipeline;
using Xunit;

namespace Perianth.Tests.Pipeline;

/// <summary>
/// The naming rules behind making something new.
/// </summary>
/// <remarks>
/// The sequence itself needs the archives and is covered by the gated suite; what
/// is here is the part that decides where files go, which is a rule rather than a
/// resource. It matters because an item's file name is <em>how the game finds
/// it</em> (Roadmap §10.95), so a name that reaches disk differently from the one
/// declared inside produces a file nothing ever asks for.
/// </remarks>
public sealed class NewAssetTests
{
    [Theory]
    [InlineData("My Cool Hat", "my_cool_hat")]
    [InlineData("hat", "hat")]
    [InlineData("Hat 2", "hat_2")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("odd!!name", "odd__name")]
    [InlineData("--trimmed--", "trimmed")]
    public void A_typed_name_becomes_something_a_file_can_be_called(string typed, string stem) =>
        Assert.Equal(stem, NewAsset.Stem(typed));

    [Fact]
    public void A_name_with_nothing_usable_in_it_reduces_to_nothing()
    {
        // The pane's guard: an empty stem means there is nothing to write, and
        // it must be caught before a file called ".mmb" is proposed.
        Assert.Empty(NewAsset.Stem("!!!"));
        Assert.Empty(NewAsset.Stem("   "));
    }

    [Fact]
    public void A_model_and_its_companions_share_one_stem()
    {
        // They are found by name beside each other, so a model whose cameldata
        // is called something else has no vertex positions at all.
        string model = NewAsset.ModelPath("My Cool Hat");

        Assert.EndsWith("/my_cool_hat.mmb", model, StringComparison.Ordinal);
        Assert.StartsWith("camel/", model, StringComparison.Ordinal);
    }

    [Fact]
    public void New_art_goes_in_a_folder_of_its_own()
    {
        // So nothing written here can collide with a shipped file, whatever it
        // is called.
        Assert.Contains("/perianth/", NewAsset.ModelPath("anything"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "camel/graph objects/actor/my_thing.mgraphobject")]
    [InlineData(false, "camel/graph objects/prop/my_thing.mgraphobject")]
    public void A_graph_object_goes_where_its_kind_lives(bool actor, string expected) =>
        Assert.Equal(expected, NewAsset.GraphPath("My Thing", actor));
}
