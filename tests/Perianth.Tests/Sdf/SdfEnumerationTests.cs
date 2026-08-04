using System;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Sdf;

/// <summary>
/// Checks the full walk of the filename index against trees whose shape is
/// known, since the container itself offers no listing to compare against.
/// </summary>
/// <remarks>
/// The corpus check in <see cref="SdfConformanceTests"/> is the breadth half of
/// this: it asserts the walk accounts for the real index, which is what a
/// drifted cursor breaks and what these fixtures are too small to show.
/// </remarks>
public sealed class SdfEnumerationTests
{
    /// <summary>
    /// Three paths under one prefix, branching twice.
    /// </summary>
    /// <remarks>
    /// Shaped so that the alternate child of the outer branch is reached only
    /// after the inner branch has extended the path: a walk that fails to
    /// restore the prefix in force where the alternate was found spells
    /// <c>c/alphaesh.mmb</c> rather than <c>c/mesh.mmb</c>, and nothing shallower
    /// than two nested branches distinguishes the two.
    /// </remarks>
    private static byte[] ThreePaths()
    {
        SdfIndexBuilder index = new();
        index.Literal("c/");
        int outer = index.Branch('m');
        index.Literal("a");
        int inner = index.Branch('q');
        index.Literal("lpha.mmb");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);

        index.PatchBranch(inner, index.Position);
        index.Literal("rray.mmb");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);

        index.PatchBranch(outer, index.Position);
        index.Literal("mesh.mmb");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);

        return index.Build();
    }

    [Fact]
    public void Every_path_the_tree_spells_is_returned()
    {
        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate(ThreePaths());

        Assert.False(walked.IsRefused, walked.IsRefused ? walked.Refusal.Message : null);
        Assert.Equal(
            ["c/alpha.mmb", "c/array.mmb", "c/mesh.mmb"],
            walked.Value.Select(entry => entry.Path));
    }

    [Fact]
    public void The_walk_and_the_descent_reach_the_same_terminals()
    {
        // The two readers share a grammar but not a direction, so agreeing on
        // every path is what says the walk explores the branch the descent would
        // have chosen — in both directions, since it must take both children.
        byte[] table = ThreePaths();
        ImmutableArray<SdfPathEntry> walked = SdfIndex.Enumerate(table).Value;

        foreach (SdfPathEntry entry in walked)
        {
            Result<SdfEntry?> found = SdfIndex.Lookup(table, entry.Path, SdfIndexLayout.V16);

            Assert.False(found.IsRefused, found.IsRefused ? found.Refusal.Message : null);
            Assert.NotNull(found.Value);
            Assert.Equal(entry.Path, found.Value!.Path);
        }
    }

    [Fact]
    public void A_terminal_found_by_the_walk_decodes_where_it_sits()
    {
        byte[] table = ThreePaths();
        SdfPathEntry first = SdfIndex.Enumerate(table).Value[0];

        Result<SdfEntry> direct = SdfIndex.ReadEntryAt(table, first.NodeOffset, first.Path, SdfIndexLayout.V16);
        Result<SdfEntry?> descended = SdfIndex.Lookup(table, first.Path, SdfIndexLayout.V16);

        Assert.False(direct.IsRefused, direct.IsRefused ? direct.Refusal.Message : null);

        // Field by field: the record's synthesized equality compares the chunk
        // array by reference, so it would pass here whatever the chunks held.
        SdfEntry expected = descended.Value!;
        Assert.Equal(expected.Path, direct.Value.Path);
        Assert.Equal(expected.TotalSize, direct.Value.TotalSize);
        Assert.Equal(expected.ResidentIndex, direct.Value.ResidentIndex);
        Assert.Equal(expected.ReadAheadBlocks, direct.Value.ReadAheadBlocks);
        Assert.Equal(expected.FileMetadata, direct.Value.FileMetadata);
        Assert.Equal(expected.Tag, direct.Value.Tag);
        Assert.Equal(expected.Chunks, direct.Value.Chunks);
    }

    [Fact]
    public void A_chunkless_terminal_is_reported_as_a_directory()
    {
        SdfIndexBuilder index = new();
        index.Literal("camel/");
        index.Terminal(chunkCount: 0);

        ImmutableArray<SdfPathEntry> walked = SdfIndex.Enumerate(index.Build()).Value;

        SdfPathEntry only = Assert.Single(walked);
        Assert.Equal("camel/", only.Path);
        Assert.True(only.IsDirectory);
    }

    [Fact]
    public void The_case_the_index_spells_is_preserved()
    {
        // Lookup folds case to compare; a listing that folded it too would
        // report paths the container does not spell, and there is nowhere else
        // to recover the real spelling from.
        SdfIndexBuilder index = new();
        index.Literal("Camel/Chr.MMB");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);

        ImmutableArray<SdfPathEntry> walked = SdfIndex.Enumerate(index.Build()).Value;

        Assert.Equal("Camel/Chr.MMB", Assert.Single(walked).Path);
    }

    [Fact]
    public void A_path_substituting_terminal_is_refused_rather_than_listed()
    {
        // The constructed path is not the one the terminal names, so listing it
        // would report a path the index does not hold. Lookup refuses these for
        // the same reason.
        SdfIndexBuilder index = new();
        index.Literal("camel/a.mmb");
        index.Terminal(chunkCount: 1, pathPatch: true).Chunk(decodedSize: 4, archiveOffset: 0);

        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate(index.Build());

        Assert.True(walked.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, walked.Refusal.Kind);
        Assert.Contains("path-substitution", walked.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_cyclic_tree_is_refused_rather_than_walked_forever()
    {
        // A branch whose alternate returns to the root. The descent's per-path
        // visited set cannot catch this, because the walk legitimately revisits
        // nodes; only the whole-walk budget does.
        SdfIndexBuilder index = new();
        int patch = index.Branch('m');
        index.Literal("a.mmb");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);
        index.PatchBranch(patch, 0);

        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate(index.Build());

        Assert.True(walked.IsRefused);
        Assert.Equal(RefusalKind.Malformed, walked.Refusal.Kind);
        Assert.Contains("cyclic", walked.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_literal_is_refused()
    {
        SdfIndexBuilder index = new();
        index.Literal("camel/");
        byte[] table = index.Build();

        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate(table.AsSpan(0, table.Length - 2).ToArray());

        Assert.True(walked.IsRefused);
        Assert.Equal(RefusalKind.Malformed, walked.Refusal.Kind);
    }

    [Fact]
    public void A_branch_leaving_the_table_is_refused()
    {
        SdfIndexBuilder index = new();
        int patch = index.Branch('m');
        index.Literal("a.mmb");
        index.Terminal(chunkCount: 1).Chunk(decodedSize: 4, archiveOffset: 0);
        index.PatchBranch(patch, 0x7FFF);

        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate(index.Build());

        Assert.True(walked.IsRefused);
        Assert.Equal(RefusalKind.Malformed, walked.Refusal.Kind);
    }

    [Fact]
    public void An_empty_table_holds_no_paths_and_is_not_an_error()
    {
        Result<ImmutableArray<SdfPathEntry>> walked = SdfIndex.Enumerate([]);

        Assert.False(walked.IsRefused);
        Assert.Empty(walked.Value);
    }

    [Fact]
    public void Asking_a_non_terminal_to_decode_is_refused()
    {
        byte[] table = ThreePaths();

        // Offset zero is the root literal, not a terminal.
        Result<SdfEntry> decoded = SdfIndex.ReadEntryAt(table, 0, "c/", SdfIndexLayout.V16);

        Assert.True(decoded.IsRefused);
        Assert.Equal(RefusalKind.Malformed, decoded.Refusal.Kind);
    }
}
