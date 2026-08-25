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
/// Re-cutting a model's packed depth index at a wider scale.
/// </summary>
/// <remarks>
/// <para>
/// The half of the depth work a re-base cannot do. A record's slice of the depth
/// pool grows by a shift, but the index that reads it is a property of the whole
/// model: every constant carries the width and the packed stream is addressed at
/// it, so a part needing more planes than its index can name widens all of them.
/// </para>
/// <para>
/// The failure to be careful about is silent. An index re-cut at the wrong offset
/// still loads, and paints every part after it at another part's depth.
/// </para>
/// </remarks>
public sealed class ZIndexWidthTests
{
    [Fact]
    public void Widening_to_the_width_it_already_has_returns_the_file_it_read()
    {
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata same = Ok(file.Widened(1));

        Assert.Equal(file.PackedZ, same.PackedZ);
        Assert.Equal(
            file.Constants.Select(c => c.PackedFlags), same.Constants.Select(c => c.PackedFlags));
    }

    [Fact]
    public void Every_index_says_the_same_thing_at_the_new_scale()
    {
        // The whole operation, and the one thing that must not drift. Four slots
        // read 1, 0, 1, 1 at one bit each. At four bits each they sit at bits 0,
        // 4, 8 and 12, which is 0x1101 read from the low bit up.
        Mode3Cameldata file = Read(Two());

        Mode3Cameldata wide = Ok(file.Widened(4));

        Assert.Equal([0x1101u], wide.PackedZ);
        Assert.All(wide.Constants, c => Assert.Equal(4, c.ZBitWidth));
    }

    [Fact]
    public void The_stream_is_re_cut_at_every_slot_and_not_only_the_first()
    {
        // A stream long enough to cross a word boundary at the new width, so an
        // implementation writing at the old offsets -- or reading a slot from
        // the wrong word -- disagrees somewhere other than at slot zero.
        Mode3Cameldata file = Read(Ladder());

        Mode3Cameldata wide = Ok(file.Widened(16));

        for (int slot = 0; slot < 8; slot++)
        {
            Assert.Equal((uint)(slot % 4), Field(wide.PackedZ, slot, 16));
        }

        // Eight sixteen-bit fields are four words, where four two-bit fields
        // were one. Nothing else in the file has changed size.
        Assert.Equal(4, wide.PackedZ.Length);
        Assert.Equal(file.Xy, wide.Xy);
        Assert.Equal(file.Z, wide.Z);
    }

    [Fact]
    public void Nothing_but_the_width_moves()
    {
        // Widening changes the scale the stream is read at and nothing else: no
        // base moves, no pool changes length. A re-base is what moves those, and
        // conflating the two would make each one's test unable to see the other.
        Mode3Cameldata file = Read(Unified());

        Mode3Cameldata wide = Ok(file.Widened(8));

        Assert.Equal(file.Xy, wide.Xy);
        Assert.Equal(file.Z, wide.Z);
        Assert.Equal(file.Uv0, wide.Uv0);
        Assert.Equal(
            file.Constants.Select(c => (c.XyBase, c.ZBase, c.Uv0Base)),
            wide.Constants.Select(c => (c.XyBase, c.ZBase, c.Uv0Base)));
    }

    [Fact]
    public void The_flags_the_width_shares_a_word_with_survive()
    {
        // The width is five bits inside a word carrying the unified-UV0
        // selector, the UV scale index and eight bits upward that have no name
        // here. Rebuilding the word from the fields with names would drop the
        // rest, which is the same mistake the writer refuses to make.
        Mode3Cameldata file = Read(new CameldataBuilder
        {
            Mode = 3,
            ConstantCount = 1,
            XyBases = [0],
            ZBases = [0],
            Uv0Bases = [0],
            Xy = [new(1, 1)],
            Z = [0f],
            Uv0 = [0],
            PackedZ = [0u],
            PackedFlags = 0x205,        // unified, UV scale 2, width 1, and bit 9
        });

        Mode3Cameldata wide = Ok(file.Widened(16));

        Assert.True(wide.Constants[0].UsesUnifiedUv0);
        Assert.Equal(2, wide.Constants[0].Uv0ScaleIndex);
        Assert.Equal(16, wide.Constants[0].ZBitWidth);
        Assert.Equal(0x200u, wide.Constants[0].PackedFlags & ~0xFFu);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(24)]
    public void A_width_the_engine_cannot_read_refuses(int width)
    {
        // The field spells thirty-two widths and the shader reads six. It loads
        // one word per index and adds no padding, so a width that does not
        // divide 32 truncates every field that straddles a boundary. Twelve and
        // twenty-four are the trap: the shader's mask table gets them right, and
        // the load does not.
        Mode3Cameldata file = Read(Two());

        Refusal refusal = Refused(file.Widened(width));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("does not divide 32", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrowing_refuses()
    {
        // Not caution: a narrower field cannot hold every index a wider one did,
        // and truncating one repaints a part onto another part's plane. There is
        // nothing to gain by it, so refusing removes a check rather than adding
        // one -- every value fits a wider field by construction.
        Mode3Cameldata file = Read(Ladder());

        Refusal refusal = Refused(file.Widened(1));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("Only widening", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_mixing_widths_refuses()
    {
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

        Refusal refusal = Refused(file.Widened(8));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("two scales at once", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Widening_is_what_lets_a_record_hold_more_depths_than_its_index_could_name()
    {
        // The two doors together, which is the point of building this one. A
        // one-bit index names two depths; the same file widened to four names
        // sixteen, and the re-base that refused now goes through.
        Mode3Cameldata file = Read(Two());

        Refused(file.Rebased([2, 2], [2, 2], [5, 1]));

        Mode3Cameldata grown = Ok(Ok(file.Widened(4)).Rebased([2, 2], [2, 2], [5, 1]));

        Assert.Equal(6, grown.Z.Length);
        Assert.Equal(5u, grown.Constants[1].ZBase);
        Assert.Equal(20f, grown.Z[5]);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 8)]
    [InlineData(256, 8)]
    [InlineData(257, 16)]
    public void The_narrowest_width_that_fits_is_a_readable_one(int depths, int expected)
    {
        Assert.Equal(expected, Mode3Constant.NarrowestZBitWidth(depths));
        Assert.True(Mode3Constant.IsReadableZBitWidth(expected));
    }

    /// <summary>One field, read the way the shader reads it.</summary>
    private static uint Field(ImmutableArray<uint> words, int slot, int width)
    {
        uint value = 0;
        for (int bit = 0; bit < width; bit++)
        {
            long at = ((long)slot * width) + bit;
            value |= ((words[(int)(at / 32)] >> (int)(at % 32)) & 1u) << bit;
        }

        return value;
    }

    /// <summary>Eight slots reading 0, 1, 2, 3, 0, 1, 2, 3 at two bits each.</summary>
    private static CameldataBuilder Ladder() => new()
    {
        Mode = 3,
        ConstantCount = 1,
        XyBases = [0],
        ZBases = [0],
        Xy = [.. Enumerable.Range(0, 8).Select(i => new Vector2(i, i))],
        Z = [0f, 1f, 2f, 3f],
        Uv0 = [],
        PackedZ = [0b11_10_01_00_11_10_01_00u],
        PackedFlags = 1 << 3,
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
