using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks the prepared search: what it keeps, what it counts, and that keeping
/// only a few gives the same few as ordering all of them.
/// </summary>
public sealed class ArchiveSearchTests
{
    private static ImmutableArray<SdfPathEntry> Index(params string[] paths) =>
        [.. paths.Select((path, ordinal) => new SdfPathEntry(path, ordinal + 1, IsDirectory: false))];

    [Fact]
    public void A_model_is_offered_before_data_the_tool_cannot_open()
    {
        // The ordering that matters. Alphabetically the .juice file wins, and in
        // the shipped archive that effect buries chr_cartman.mmb at position 528
        // of 2,291 — outside any cap a list can afford.
        ArchiveSearch search = new(Index(
            "aaa/town_actor_chr_cartman.juice",
            "zzz/deep/chr_cartman.mmb",
            "mmm/chr_cartman.editordata"));

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found = search.Best("cartman", limit: 3);

        Assert.Equal(
            ["zzz/deep/chr_cartman.mmb", "mmm/chr_cartman.editordata", "aaa/town_actor_chr_cartman.juice"],
            found.Value.Best.Select(entry => entry.Path));
    }

    [Fact]
    public void A_match_in_the_name_beats_a_match_in_the_folder()
    {
        ArchiveSearch search = new(Index("cartman/other.mmb", "npc/chr_cartman.mmb"));

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found = search.Best("cartman", limit: 2);

        Assert.Equal("npc/chr_cartman.mmb", found.Value.Best[0].Path);
    }

    [Fact]
    public void The_capped_result_is_the_head_of_the_whole_ordering()
    {
        // What makes a cap honest: the few shown are the few that would have
        // been first anyway, not whichever the faster algorithm happened to keep.
        ImmutableArray<SdfPathEntry> paths = Index(
            "a/one.mmb", "b/two.mmb", "c/three.dds", "d/four.anim", "e/five.juice", "f/six.mmb");

        ArchiveSearch search = new(paths);

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> capped = search.Best(".", limit: 3);
        Result<ImmutableArray<SdfPathEntry>> full = ArchiveExtraction.Find(paths, ".");

        Assert.Equal(
            full.Value.Take(3).Select(entry => entry.Path),
            capped.Value.Best.Select(entry => entry.Path));
    }

    [Fact]
    public void The_total_counts_every_match_not_only_the_kept_ones()
    {
        // A cap the caller cannot see is a cap that loses their file, so the
        // count has to be of everything that matched.
        ArchiveSearch search = new(Index("a.mmb", "b.mmb", "c.mmb", "d.mmb"));

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found = search.Best(".mmb", limit: 2);

        Assert.Equal(2, found.Value.Best.Length);
        Assert.Equal(4, found.Value.Total);
    }

    [Fact]
    public void No_limit_returns_everything()
    {
        ArchiveSearch search = new(Index("a.mmb", "b.mmb", "c.mmb"));

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found = search.Best(".mmb", limit: 0);

        Assert.Equal(3, found.Value.Best.Length);
        Assert.Equal(3, found.Value.Total);
    }

    [Fact]
    public void Searching_is_case_insensitive_and_accepts_either_separator()
    {
        ArchiveSearch search = new(Index("chr/Cartman.mmb"));

        Assert.Single(search.Best("CARTMAN", limit: 10).Value.Best);
        Assert.Single(search.Best(@"chr\cartman", limit: 10).Value.Best);
    }

    [Fact]
    public void Empty_text_is_refused_rather_than_matching_everything()
    {
        ArchiveSearch search = new(Index("a.mmb"));

        Assert.True(search.Best(string.Empty, limit: 10).IsRefused);
    }

    [Fact]
    public void An_empty_index_searches_without_complaint()
    {
        ArchiveSearch search = new([]);

        Assert.Equal(0, search.Count);
        Assert.Empty(search.Best("anything", limit: 10).Value.Best);
    }
}
