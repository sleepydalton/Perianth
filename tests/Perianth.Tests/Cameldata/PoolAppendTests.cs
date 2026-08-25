using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Giving a model a record it did not have.
/// </summary>
/// <remarks>
/// <para>
/// The first half of "add a part": rungs one to three changed what a record
/// owned, and this makes one exist. It is a much smaller operation than a
/// re-base because a record's slice ends where the <em>next</em> record's base
/// begins, so a record appended at the end takes a base equal to the old pool
/// length — precisely where the previously-last record's slice already ended.
/// Nothing moves.
/// </para>
/// <para>
/// That is also the thing most worth testing. The failure this operation has is
/// not a crash: it is a file that loads while some earlier part quietly reads
/// the new part's coordinates. So the central test is that every record already
/// present reads exactly what it read before, byte for byte, and the rest are
/// the refusals that stop a caller building a record the shader cannot address.
/// </para>
/// </remarks>
public sealed class PoolAppendTests
{
    [Fact]
    public void Appending_leaves_every_existing_record_reading_what_it_read()
    {
        // The property the whole operation exists to keep. Checked on the pools
        // themselves rather than through an assembler, so it holds regardless of
        // what any later stage does with them.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            NewConstant(file), [new(9, 9), new(8, 8)], [30f], [0u, 0u]));

        Assert.Equal(3, grown.Constants.Length);
        for (int record = 0; record < file.Constants.Length; record++)
        {
            Assert.Equal(file.Constants[record].XyBase, grown.Constants[record].XyBase);
            Assert.Equal(file.Constants[record].ZBase, grown.Constants[record].ZBase);
            Assert.Equal(file.Constants[record].Uv0Base, grown.Constants[record].Uv0Base);
        }

        // Every pool keeps its old contents as a prefix, so no existing base can
        // now point at anything different.
        Assert.Equal(file.Xy, grown.Xy.Take(file.Xy.Length));
        Assert.Equal(file.Z, grown.Z.Take(file.Z.Length));
    }

    [Fact]
    public void The_existing_depth_indices_survive_the_rebuilt_stream()
    {
        // The packed stream is rebuilt rather than extended, because the new
        // record's first field begins mid-word wherever the last one left off.
        // Rebuilding must reproduce every existing slot exactly; this reads them
        // back one at a time rather than comparing words, so a width or an
        // offset that is wrong shows as the slot it broke.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            NewConstant(file), [new(9, 9)], [30f], [0u]));

        // Two() packs 0b1101 at one bit per slot: slots 0..3 read 1, 0, 1, 1.
        Assert.Equal([1u, 0u, 1u, 1u], ReadSlots(file, 4));
        Assert.Equal([1u, 0u, 1u, 1u, 0u], ReadSlots(grown, 5));
    }

    [Fact]
    public void The_new_record_takes_the_pools_ends_as_its_bases()
    {
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            NewConstant(file), [new(9, 9), new(8, 8)], [30f, 40f], [1u, 0u]));

        Mode3Constant added = grown.Constants[^1];
        Assert.Equal((uint)file.Xy.Length, added.XyBase);
        Assert.Equal((uint)file.Z.Length, added.ZBase);
        Assert.Equal([new Vector2(9, 9), new Vector2(8, 8)], grown.Xy.Skip(file.Xy.Length));
        Assert.Equal([30f, 40f], grown.Z.Skip(file.Z.Length));
        Assert.Equal([1u, 0u], ReadSlots(grown, 6).Skip(4));
    }

    [Fact]
    public void The_bases_a_caller_supplies_are_ignored()
    {
        // Where a slice goes is this operation's decision. A caller that had to
        // compute three pool offsets correctly is a caller that could get them
        // wrong, and a wrong one aims a part at another part's coordinates.
        Mode3Cameldata file = Read(Two());
        Mode3Constant misleading = NewConstant(file) with
        {
            XyBase = 999,
            ZBase = 999,
            Uv0Base = 999,
        };

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            misleading, [new(9, 9)], [30f], [0u]));

        Assert.Equal((uint)file.Xy.Length, grown.Constants[^1].XyBase);
        Assert.Equal((uint)file.Z.Length, grown.Constants[^1].ZBase);
    }

    [Fact]
    public void A_unified_record_takes_the_UV0_arrays_end()
    {
        Mode3Cameldata file = Read(Unified());

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            NewConstant(file) with { PackedFlags = 1 },
            [new(9, 9), new(8, 8)], [30f], [0u, 0u], [0xAAAAu, 0xBBBBu]));

        Assert.Equal((uint)file.Uv0.Length, grown.Constants[^1].Uv0Base);
        Assert.Equal(file.Uv0, grown.Uv0.Take(file.Uv0.Length));
        Assert.Equal([0xAAAAu, 0xBBBBu], grown.Uv0.Skip(file.Uv0.Length));
    }

    [Fact]
    public void A_projected_record_leaves_the_UV0_array_alone()
    {
        // 86% of parts derive UV0 from position and read nothing from the array.
        // Growing it on their behalf would be motion with no meaning.
        Mode3Cameldata file = Read(Unified());

        Mode3Cameldata grown = Ok(file.WithAppendedRecord(
            NewConstant(file) with { PackedFlags = 0 }, [new(9, 9)], [30f], [0u]));

        Assert.Equal(file.Uv0, grown.Uv0);
    }

    [Fact]
    public void Appending_twice_is_appending_two()
    {
        // Composition, which an operation that quietly depends on where it
        // started fails and a plain append passes.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata twice = Ok(Ok(file.WithAppendedRecord(
                NewConstant(file), [new(9, 9)], [30f], [0u]))
            .WithAppendedRecord(NewConstant(file), [new(8, 8)], [40f], [0u]));

        Assert.Equal(4, twice.Constants.Length);
        Assert.Equal(4u, twice.Constants[2].XyBase);
        Assert.Equal(5u, twice.Constants[3].XyBase);
        Assert.Equal(2u, twice.Constants[2].ZBase);
        Assert.Equal(3u, twice.Constants[3].ZBase);
        Assert.Equal([1u, 0u, 1u, 1u, 0u, 0u], ReadSlots(twice, 6));
    }

    [Fact]
    public void A_record_that_draws_nothing_refuses()
    {
        Mode3Cameldata file = Read(Two());

        Assert.Contains("draws nothing", Refused(file.WithAppendedRecord(
            NewConstant(file), [], [30f], [])).Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_with_no_depth_refuses()
    {
        Mode3Cameldata file = Read(Two());

        Assert.Contains("no plane", Refused(file.WithAppendedRecord(
            NewConstant(file), [new(9, 9)], [], [0u])).Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void One_depth_index_per_vertex_or_it_refuses()
    {
        Mode3Cameldata file = Read(Two());

        Assert.Contains("one index per vertex", Refused(file.WithAppendedRecord(
                NewConstant(file), [new(9, 9), new(8, 8)], [30f], [0u])).Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_depth_index_past_the_record_refuses()
    {
        // Truncating it instead would aim the vertex at another record's depth,
        // which is the silent-wrong-file failure this whole area guards.
        Mode3Cameldata file = Read(Two());

        Assert.Contains("selects depth 1 of 1", Refused(file.WithAppendedRecord(
            NewConstant(file), [new(9, 9)], [30f], [1u])).Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void More_depths_than_the_index_can_name_refuses()
    {
        // Width one addresses two depths. A third is unreachable, and writing it
        // would leave a record whose own vertices cannot select it.
        Mode3Cameldata file = Read(Two());

        Assert.Contains("which can name 2", Refused(file.WithAppendedRecord(
                NewConstant(file), [new(9, 9)], [30f, 40f, 50f], [0u])).Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_width_the_shader_misreads_refuses()
    {
        // {1, 2, 4, 8, 16, 32} and nothing else: the shader loads one word per
        // index with no padding, so a field straddling a boundary loses the bits
        // past it. Width 3 is the trap -- the mask table reads it correctly and
        // the load does not.
        Mode3Cameldata file = Read(Two());

        Assert.Contains("readable widths", Refused(file.WithAppendedRecord(
                NewConstant(file) with { PackedFlags = (2u & 0x1F) << 3 },
                [new(9, 9)], [30f], [0u])).Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_width_the_model_does_not_share_refuses()
    {
        // One scale per stream. Two would make the splice unreadable for every
        // record, not just the new one.
        Mode3Cameldata file = Read(Two());

        Assert.Contains(nameof(Mode3Cameldata.Widened), Refused(file.WithAppendedRecord(
                NewConstant(file) with { PackedFlags = (3u & 0x1F) << 3 },
                [new(9, 9)], [30f], [0u])).Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_unified_record_without_UV0_values_refuses()
    {
        Mode3Cameldata file = Read(Unified());

        Assert.Contains("one per vertex", Refused(file.WithAppendedRecord(
                NewConstant(file) with { PackedFlags = 1 }, [new(9, 9)], [30f], [0u])).Message,
            System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_projected_record_given_UV0_values_refuses()
    {
        // Accepting them would write an array nothing reads, and leave the
        // caller believing the part carries the coordinates they supplied.
        Mode3Cameldata file = Read(Two());

        Assert.Contains("nothing would read", Refused(file.WithAppendedRecord(
                NewConstant(file) with { PackedFlags = 0 },
                [new(9, 9)], [30f], [0u], [0x1234u])).Message,
            System.StringComparison.Ordinal);
    }

    /// <summary>Every depth index of the first <paramref name="slots"/> slots.</summary>
    private static ImmutableArray<uint> ReadSlots(Mode3Cameldata file, int slots)
    {
        int width = file.Constants[0].ZBitWidth;
        ImmutableArray<uint>.Builder read = ImmutableArray.CreateBuilder<uint>(slots);
        for (int slot = 0; slot < slots; slot++)
        {
            uint value = 0;
            for (int bit = 0; bit < width; bit++)
            {
                long at = ((long)slot * width) + bit;
                value |= ((file.PackedZ[(int)(at / 32)] >> (int)(at % 32)) & 1u) << bit;
            }

            read.Add(value);
        }

        return read.MoveToImmutable();
    }

    /// <summary>A constant shaped like the model's own, with deliberate bases.</summary>
    private static Mode3Constant NewConstant(Mode3Cameldata file) =>
        file.Constants[0] with { XyBase = 0, ZBase = 0, Uv0Base = 0 };

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
