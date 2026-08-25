using System;
using System.Linq;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// What the New pane says is not known to work.
/// </summary>
/// <remarks>
/// This was one paragraph for all three kinds, saying the game has never been
/// seen to load a file it did not ship. That is wrong in both directions: the
/// model, its materials and a graph object each go to a path of their own, and
/// a reference by path resolves to a new path — proven in game (Roadmap
/// §10.110). Only the declaration is open, and which declaration differs, so
/// the three cannot share a sentence. A pane that overstates its doubts talks
/// an author out of work that would have worked.
/// </remarks>
public sealed class NewViewModelTests
{
    [Fact]
    public void Each_kind_says_what_is_open_about_that_kind()
    {
        NewViewModel pane = new();

        string[] said = [.. pane.Kinds.Select(kind =>
        {
            pane.Kind = kind;
            return pane.Caveat;
        })];

        Assert.Equal(said.Length, said.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A prop's graph object is named by path, so it is on the proven side. What
    /// is open is placing it, and that is not an unknown: a written layer has
    /// twice been installed and honoured by nothing. Telling a prop author that
    /// something has not been tried yet would understate a thing that is known
    /// to have gone wrong.
    /// </summary>
    [Fact]
    public void A_prop_is_warned_about_the_thing_that_has_actually_gone_wrong()
    {
        NewViewModel pane = new() { Kind = "Prop" };

        Assert.Contains("will not draw", pane.Caveat, StringComparison.Ordinal);
        Assert.Contains("backup", pane.Caveat, StringComparison.Ordinal);
        Assert.DoesNotContain("may not yet work", pane.Caveat, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other two need the game to find a declaration it was never shipped,
    /// which is the one thing correct authoring cannot settle. The pane says the
    /// outcome and not the mechanism — an author does not need to know that the
    /// game builds its list by asking a folder.
    /// </summary>
    [Theory]
    [InlineData("Costume piece")]
    [InlineData("Character")]
    public void A_registry_kind_says_the_declaration_is_the_open_part(string kind)
    {
        NewViewModel pane = new() { Kind = kind };

        Assert.Contains("may not yet work", pane.Caveat, StringComparison.Ordinal);
    }
}
