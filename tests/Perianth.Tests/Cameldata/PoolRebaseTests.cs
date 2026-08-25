using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Resizing a record's slice of the pools.
/// </summary>
/// <remarks>
/// The arithmetic import's third rung stands on, and the kind that is wrong
/// silently: a base index off by one produces a file that loads and draws
/// another part's geometry. So the tests here are mostly properties rather than
/// worked examples, because a property holds for inputs nobody thought to write
/// down.
/// </remarks>
public sealed class PoolRebaseTests
{
    [Fact]
    public void Resizing_by_nothing_returns_the_file_it_read()
    {
        // The identity property. It fails on any error that is not
        // proportional to the change, which is most of them: a mis-sized packed
        // stream, a dropped UV0 slot, a base written from the wrong cursor.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata same = Ok(file.Rebased([2, 2], [2, 2]));

        Assert.Equal(file.Xy, same.Xy);
        Assert.Equal(file.Z, same.Z);
        Assert.Equal(file.Uv0, same.Uv0);
        Assert.Equal(file.PackedZ, same.PackedZ);
        Assert.Equal(
            file.Constants.Select(c => (c.XyBase, c.ZBase, c.Uv0Base)),
            same.Constants.Select(c => (c.XyBase, c.ZBase, c.Uv0Base)));
    }

    [Fact]
    public void Growing_twice_by_one_is_growing_once_by_two()
    {
        // Composition. An operation that is a shift composes; one that quietly
        // depends on where it started does not, and this is the cheapest way to
        // tell the two apart.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata once = Ok(file.Rebased([2, 2], [4, 2]));
        Mode3Cameldata twice = Ok(Ok(file.Rebased([2, 2], [3, 2])).Rebased([3, 2], [4, 2]));

        Assert.Equal(once.Xy, twice.Xy);
        Assert.Equal(once.PackedZ, twice.PackedZ);
        Assert.Equal(once.Uv0, twice.Uv0);
        Assert.Equal(
            once.Constants.Select(c => c.XyBase), twice.Constants.Select(c => c.XyBase));
    }

    [Fact]
    public void A_record_that_grows_pushes_every_later_base_along()
    {
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [5, 2]));

        Assert.Equal(0u, grown.Constants[0].XyBase);
        Assert.Equal(5u, grown.Constants[1].XyBase);
        Assert.Equal(7, grown.Xy.Length);
    }

    [Fact]
    public void The_kept_vertices_keep_their_positions_and_their_depths()
    {
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [3, 2]));

        // The first record's own two, then a new empty slot, then the second's.
        Assert.Equal(file.Xy[0], grown.Xy[0]);
        Assert.Equal(file.Xy[1], grown.Xy[1]);
        Assert.Equal(default, grown.Xy[2]);
        Assert.Equal(file.Xy[2], grown.Xy[3]);
        Assert.Equal(file.Xy[3], grown.Xy[4]);
    }

    [Fact]
    public void The_Z_pool_does_not_move()
    {
        // Its entries are per depth, not per vertex, so a record gaining
        // vertices reads the depths it already had. A resize that shifted them
        // would repaint every part's plane.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [6, 9]));

        Assert.Equal(file.Z, grown.Z);
        Assert.Equal(
            file.Constants.Select(c => c.ZBase), grown.Constants.Select(c => c.ZBase));
    }

    [Fact]
    public void A_record_that_gains_a_depth_pushes_every_later_depth_base_along()
    {
        // The Z pool tiles as the XY pool does -- strictly ascending, sharing
        // nothing, gapless and exhaustive on every real file -- so growing one
        // record's depths moves the rest. Stated on the *second* record because
        // the first begins at zero either way, and a base that never has to move
        // proves nothing about whether it would.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [2, 2], [2, 1]));

        Assert.Equal(0u, grown.Constants[0].ZBase);
        Assert.Equal(2u, grown.Constants[1].ZBase);
        Assert.Equal(3, grown.Z.Length);

        // The first record keeps its own depth and gains an empty slot; the
        // second's depth travels with it rather than staying where it was.
        Assert.Equal(10f, grown.Z[0]);
        Assert.Equal(0f, grown.Z[1]);
        Assert.Equal(20f, grown.Z[2]);
    }

    [Fact]
    public void More_depths_than_the_index_can_address_refuses()
    {
        // The pool grows; the index that reads it does not. A one-bit index
        // names two entries however many the pool holds. Widening is a separate
        // call because it re-cuts the stream for every record in the model, so a
        // re-base will not do it quietly on one record's behalf -- it points at
        // the operation that will.
        Mode3Cameldata file = Read(Two());

        Refusal refusal = Refused(file.Rebased([2, 2], [2, 2], [3, 1]));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("can name", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Widen the model first", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resizing_the_points_alone_leaves_every_depth_where_it_was()
    {
        // The two pools are independent: a record gaining vertices reads the
        // depths it already had, because a depth entry is per plane rather than
        // per point.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [5, 2]));

        Assert.Equal(file.Z, grown.Z);
        Assert.Equal(
            file.Constants.Select(c => c.ZBase), grown.Constants.Select(c => c.ZBase));
    }

    [Fact]
    public void A_pool_the_records_do_not_tile_refuses()
    {
        // The whole operation rests on the slices being a plain sequence. A
        // file that is not laid out that way is refused rather than re-based
        // from a cursor that does not describe it.
        Mode3Cameldata file = Read(Two());

        Refusal refusal = Refused(file.Rebased([1, 2], [1, 2]));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("not a plain sequence", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Widths_that_do_not_account_for_the_pool_refuse()
    {
        Mode3Cameldata file = Read(Two());

        Refusal refusal = Refused(file.Rebased([2, 1], [2, 1]));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("XY slots and the pool holds", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_mixing_Z_index_widths_refuses()
    {
        // The shader reads a vertex's Z index at width times a global index, so
        // two widths address one stream on two scales and their fields are not
        // runs at all. No model in 1,594 does it; this build will not guess what
        // one would mean.
        Mode3Cameldata file = Read(new CameldataBuilder
        {
            Mode = 3,
            ConstantCount = 2,
            XyBases = [0, 2],
            ZBases = [0, 0],
            Xy = [new(1, 1), new(2, 2), new(3, 3), new(4, 4)],
            Z = [0f],
            Uv0 = [0, 0, 0, 0],
            PackedZ = [0u],
            PackedFlags = 0,
            PerConstantPackedFlags = [0, 1 << 3],
        });

        Refusal refusal = Refused(file.Rebased([2, 2], [3, 2]));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("two scales at once", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_moved_vertex_keeps_the_depth_it_read()
    {
        // The packed stream is addressed by a global slot, so a record that
        // moves must have its fields rewritten at the new offset. Writing them
        // where they used to be leaves every later part reading another part's
        // depths -- a file that loads and draws wrongly, which is the failure
        // this whole operation is careful about.
        //
        // Width is one bit. The four slots read 1, 0, 1, 1. Growing the first
        // record to three puts a zeroed slot third, so the stream becomes
        // 1, 0, 0, 1, 1 -- 0b11001 read from the low bit up.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [3, 2]));

        Assert.Equal(1, grown.Constants[0].ZBitWidth);
        Assert.Equal([0b11001u], grown.PackedZ);
    }

    [Fact]
    public void A_unified_UV0_record_takes_its_slots_and_its_base_with_it()
    {
        // Only the records that read the unified array are re-based; a record
        // deriving UV0 from position carries a base nothing reads, and moving
        // it would be motion with no meaning. 705 of 1,594 files have any.
        Mode3Cameldata file = Read(Unified());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [3, 2]));

        Assert.Equal(0u, grown.Constants[0].Uv0Base);
        Assert.Equal(3u, grown.Constants[1].Uv0Base);
        Assert.Equal(5, grown.Uv0.Length);

        // The second record's own two words, at their new place.
        Assert.Equal(0x3333u, grown.Uv0[3]);
        Assert.Equal(0x4444u, grown.Uv0[4]);
        Assert.Equal(0u, grown.Uv0[2]);
    }

    /// <summary>Two records that read the unified UV0 array.</summary>
    [Fact]
    public void A_record_that_gains_depth_restates_its_new_count()
    {
        // The flag word says how many depth planes the record owns, on
        // 5,399,482 shipped records of 5,399,482. A resize that moved the slice
        // and left the count behind produced a file that disagreed with itself,
        // and the game drew one texel of the texture over the whole part.
        Mode3Cameldata file = Read(Depthful());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [2, 2], [4, 1]));

        Assert.Equal(4, grown.Constants[0].DepthCount);
        Assert.Equal(1, grown.Constants[1].DepthCount);
        Assert.Equal(0u, grown.Constants[0].ZBase);
        Assert.Equal(4u, grown.Constants[1].ZBase);
    }

    [Fact]
    public void The_restated_depth_count_matches_the_slice_every_record_owns()
    {
        // The property rather than the example: whatever the resize, a record's
        // stated count is the distance to the next record's base.
        Mode3Cameldata file = Read(Depthful());

        Mode3Cameldata grown = Ok(file.Rebased([2, 2], [3, 2], [2, 5]));

        for (int record = 0; record < grown.Constants.Length; record++)
        {
            int start = (int)grown.Constants[record].ZBase;
            int end = record + 1 < grown.Constants.Length
                ? (int)grown.Constants[record + 1].ZBase
                : grown.Z.Length;
            Assert.Equal(end - start, grown.Constants[record].DepthCount);
        }
    }

    [Fact]
    public void Resizing_nothing_leaves_the_restated_count_alone()
    {
        Mode3Cameldata file = Read(Depthful());

        Mode3Cameldata same = Ok(file.Rebased([2, 2], [2, 2]));

        Assert.Equal(
            file.Constants.Select(c => c.DepthCount),
            same.Constants.Select(c => c.DepthCount));
    }

    /// <summary>Two records that state the one depth plane each of them owns.</summary>
    private static CameldataBuilder Depthful() => new()
    {
        Mode = 3,
        ConstantCount = 2,
        XyBases = [0, 2],
        ZBases = [0, 1],
        Xy = [new(1, 1), new(2, 2), new(3, 3), new(4, 4)],
        Z = [10f, 20f],
        Uv0 = [0, 0, 0, 0],
        PackedZ = [0b1101u],
        // Bits 3 to 7 are the Z width, less one: a width of 4 so a record may
        // own more than two planes. Bits 16 and up are the count itself.
        PerConstantPackedFlags = [(3u << 3) | (1u << 16), (3u << 3) | (1u << 16)],
    };

    private static CameldataBuilder Unified() => new()
    {
        Mode = 3,
        ConstantCount = 2,
        XyBases = [0, 2],
        ZBases = [0, 1],
        Uv0Bases = [0, 2],
        Xy = [new(1, 1), new(2, 2), new(3, 3), new(4, 4)],
        Z = [10f, 20f],
        Uv0 = [0x1111, 0x2222, 0x3333, 0x4444],
        PackedZ = [0b1101u],
        PackedFlags = 1,
    };

    /// <summary>Two records of two vertices each, sharing one Z-index width.</summary>
    private static CameldataBuilder Two() => new()
    {
        Mode = 3,
        ConstantCount = 2,
        XyBases = [0, 2],
        ZBases = [0, 1],
        Xy = [new(1, 1), new(2, 2), new(3, 3), new(4, 4)],
        Z = [10f, 20f],
        Uv0 = [0, 0, 0, 0],
        PackedZ = [0b1101u],
        PackedFlags = 0,
    };

    private static Mode3Cameldata Read(CameldataBuilder builder)
    {
        Result<CameldataFile> read = CameldataReader.Read(
            new SourceFile("test.cameldata", builder.Build()));
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal.Message);
        return Assert.IsType<Mode3Cameldata>(read.Value);
    }

    private static Mode3Cameldata Ok(Result<Mode3Cameldata> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.Message);
        return result.Value;
    }

    private static Refusal Refused(Result<Mode3Cameldata> result)
    {
        Assert.False(result.IsSuccess);
        return result.Refusal;
    }
}
