using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Re-cutting a record's curved coverage when its geometry is replaced.
/// </summary>
/// <remarks>
/// <para>
/// The engine trims fragments inside a part's triangles using per-vertex Bezier
/// selectors, so a part is not the polygon its triangles describe — it is that
/// polygon with curves cut out of it. Two of the four fields addressing them are
/// derived from the vertex count, which makes this the fifth stale derived field
/// the project has had to chase, and the first whose staleness was visible in
/// game before it was visible here.
/// </para>
/// <para>
/// The distinction these tests exist to hold: a <b>reshape</b> keeps every
/// vertex, so each keeps its selector and the curve travels with the points; a
/// <b>redraw</b> replaces the triangles and must lose the old outline, or the
/// new shape is trimmed by the shape it replaced.
/// </para>
/// </remarks>
public sealed class CoverageTests
{
    [Fact]
    public void A_record_declares_the_coverage_its_vertex_count_needs()
    {
        Mode3Constant constant = Constant().WithCoverageSlice(signBase: 5, vertices: 40);

        // 40 vertices: two words of sign at one bit each, three of selector at
        // two bits each, and the selector array begins where the signs end.
        Assert.Equal(5u, constant.CoverageSignBase);
        Assert.Equal(2u, constant.CoverageSignWords);
        Assert.Equal(7u, constant.CoverageBitsBase);
        Assert.Equal(3u, constant.CoverageBitsWords);
        Assert.True(constant.CoverageAgreesWith(40));

        // The check is word-granular and says so: 41 vertices need the same two
        // and three words, so it cannot see an off-by-one. It catches what it
        // exists to catch -- a record whose ranges were sized for the 400
        // vertices it used to have and now holds 12 -- and claiming more of it
        // than that would be claiming a guard nothing has.
        Assert.True(constant.CoverageAgreesWith(41));
        Assert.False(constant.CoverageAgreesWith(64));
        Assert.False(constant.CoverageAgreesWith(4));
    }

    [Fact]
    public void A_redrawn_record_loses_the_curves_of_the_part_it_replaced()
    {
        Mode3Cameldata file = Two(first: 4, second: 4, firstSelector: 1, firstSign: 0);

        Mode3Cameldata edited = Recut(file, [4, 4], [4, 4], [true, false]);

        // Every vertex of the redrawn record reads neutral, and the record
        // beside it is untouched -- which is the whole point, since a model
        // may hold one of each.
        for (int vertex = 0; vertex < 4; vertex++)
        {
            Assert.Equal(Mode3Cameldata.NeutralCoverageSelector, Selector(edited, 0, vertex));
            Assert.Equal(Mode3Cameldata.NeutralCoverageSign, Sign(edited, 0, vertex));
            Assert.Equal(3u, Selector(edited, 1, vertex));
            Assert.Equal(1u, Sign(edited, 1, vertex));
        }
    }

    [Fact]
    public void A_reshaped_record_keeps_every_selector_it_had()
    {
        Mode3Cameldata file = Two(first: 4, second: 4, firstSelector: 1, firstSign: 0);

        Mode3Cameldata edited = Recut(file, [4, 4], [4, 4], [false, false]);

        for (int vertex = 0; vertex < 4; vertex++)
        {
            Assert.Equal(1u, Selector(edited, 0, vertex));
            Assert.Equal(0u, Sign(edited, 0, vertex));
        }
    }

    [Fact]
    public void A_grown_record_takes_the_slack_and_pushes_the_next_one_along()
    {
        Mode3Cameldata file = Two(first: 4, second: 4, firstSelector: 1, firstSign: 0);

        // 40 vertices needs two sign words and three selector words where four
        // needed one and one, so the second record's slice moves from 2 to 5.
        Mode3Cameldata edited = Recut(file, [4, 4], [40, 4], [false, false]);

        Assert.Equal(0u, edited.Constants[0].CoverageSignBase);
        Assert.Equal(5u, edited.Constants[1].CoverageSignBase);
        Assert.True(edited.Constants[0].CoverageAgreesWith(40));
        Assert.True(edited.Constants[1].CoverageAgreesWith(4));

        // The kept vertices keep their selectors and the new ones are neutral,
        // because there is nothing else they could hold.
        Assert.Equal(1u, Selector(edited, 0, 3));
        Assert.Equal(Mode3Cameldata.NeutralCoverageSelector, Selector(edited, 0, 4));

        // The buffer and the header count are one fact stated twice.
        Assert.Equal(edited.BezierBytes.Length, edited.BezierWordCount * sizeof(uint));
        Assert.True(CameldataWriter.Write(edited).IsSuccess);
    }

    [Fact]
    public void A_buffer_whose_slices_do_not_tile_refuses_rather_than_being_re_cut()
    {
        Mode3Cameldata file = Two(first: 4, second: 4, firstSelector: 1, firstSign: 0);
        Mode3Constant moved = file.Constants[1].WithCoverageSlice(signBase: 9, vertices: 4);
        Mode3Cameldata gapped = Rebuilt(file, [file.Constants[0], moved]);

        Result<Mode3Cameldata> recut = gapped.WithCoverage([4, 4], [4, 4], [false, false]);

        Assert.True(recut.IsRefused);
        Assert.Contains("not a plain sequence of slices", recut.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_that_do_not_describe_the_vertices_given_refuse()
    {
        Mode3Cameldata file = Two(first: 4, second: 4, firstSelector: 1, firstSign: 0);

        // The caller says 40 vertices and the record's own ranges say 4. One of
        // the two is wrong and this cannot know which, so it refuses rather
        // than trusting either -- the same rule the ANIM writer follows for a
        // header count that has gone stale.
        Result<Mode3Cameldata> recut = file.WithCoverage([40, 4], [40, 4], [false, false]);

        Assert.True(recut.IsRefused);
        Assert.Contains("do not describe", recut.Refusal.Message, StringComparison.Ordinal);
    }

    private static Mode3Cameldata Recut(
        Mode3Cameldata file, int[] current, int[] wanted, bool[] neutralise)
    {
        Result<Mode3Cameldata> recut =
            file.WithCoverage([.. current], [.. wanted], [.. neutralise]);
        Assert.True(recut.IsSuccess, recut.IsRefused ? recut.Refusal.Message : string.Empty);
        return recut.Value;
    }

    private static uint Selector(Mode3Cameldata file, int record, int vertex)
    {
        Mode3Constant constant = file.Constants[record];
        return (Word(file, constant.CoverageBitsBase + (uint)(vertex / 16)) >> ((vertex % 16) * 2)) & 3;
    }

    private static uint Sign(Mode3Cameldata file, int record, int vertex)
    {
        Mode3Constant constant = file.Constants[record];
        return (Word(file, constant.CoverageSignBase + (uint)(vertex / 32)) >> (vertex % 32)) & 1;
    }

    private static uint Word(Mode3Cameldata file, uint index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(file.BezierBytes.Span[(int)(index * 4)..]);

    /// <summary>
    /// Two records, the first carrying a chosen selector and sign throughout and
    /// the second carrying a different pair, so a change to one is visible
    /// against the other.
    /// </summary>
    private static Mode3Cameldata Two(int first, int second, uint firstSelector, uint firstSign)
    {
        int firstWords = Mode3Constant.CoverageWordsFor(first);
        uint[] words = new uint[firstWords + Mode3Constant.CoverageWordsFor(second)];

        Fill(words, 0, first, firstSelector, firstSign);
        Fill(words, firstWords, second, 3, 1);

        byte[] bytes = new byte[words.Length * sizeof(uint)];
        for (int word = 0; word < words.Length; word++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(word * sizeof(uint)), words[word]);
        }

        Mode3Constant blank = Constant();
        return new Mode3Cameldata(
            "made-up.cameldata", 3, 0, words.Length, bytes,
            [blank.WithCoverageSlice(0, first), blank.WithCoverageSlice(firstWords, second)],
            [.. new Vector2[first + second]], [0f], [], [0u], default);
    }

    private static void Fill(uint[] words, int at, int vertices, uint selector, uint sign)
    {
        int signWords = Mode3Constant.CoverageSignWordsFor(vertices);
        for (int vertex = 0; vertex < vertices; vertex++)
        {
            words[at + signWords + (vertex / 16)] |= selector << ((vertex % 16) * 2);
            words[at + (vertex / 32)] |= sign << (vertex % 32);
        }
    }

    private static Mode3Cameldata Rebuilt(
        Mode3Cameldata file, ImmutableArray<Mode3Constant> constants) =>
        new(file.Path, file.HeaderWord, file.Flags, file.BezierWordCount, file.BezierBytes,
            constants, file.Xy, file.Z, file.Uv0, file.PackedZ, file.TrailingBytes);

    private static Mode3Constant Constant() => new(
        default, default, default, new byte[16], 0, 0, 0, 0, default, 1f, 1f, default);
}
