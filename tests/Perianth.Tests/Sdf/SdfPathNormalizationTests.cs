using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Sdf;

/// <summary>
/// Checks canonicalization, including that it does no work when there is none
/// to do.
/// </summary>
public sealed class SdfPathNormalizationTests
{
    [Fact]
    public void Case_is_folded_and_separators_unified()
    {
        Assert.Equal("camel/chr/a.mmb", SdfIndex.NormalizePath(@"Camel\CHR\A.MMB"));
    }

    [Fact]
    public void A_path_already_canonical_is_returned_unchanged_and_uncopied()
    {
        // Not a micro-optimization: every path in the shipped container is
        // already in this form, so searching, selecting and resolving each
        // normalized 486,543 paths into 486,543 identical copies. Returning the
        // original took resolving one character from 689ms to 401ms and
        // preparing the search index from 435ms to 168ms.
        string canonical = "camel/baked/assets/characters/npc/cartman/chr_cartman.mmb";

        Assert.Same(canonical, SdfIndex.NormalizePath(canonical));
    }

    [Fact]
    public void A_path_needing_either_change_is_still_rewritten()
    {
        Assert.NotSame("a/B.mmb", SdfIndex.NormalizePath("a/B.mmb"));
        Assert.NotSame(@"a\b.mmb", SdfIndex.NormalizePath(@"a\b.mmb"));
        Assert.Equal("a/b.mmb", SdfIndex.NormalizePath("a/B.mmb"));
        Assert.Equal("a/b.mmb", SdfIndex.NormalizePath(@"a\b.mmb"));
    }

    [Fact]
    public void An_empty_path_normalizes_to_itself()
    {
        Assert.Equal(string.Empty, SdfIndex.NormalizePath(string.Empty));
    }
}
