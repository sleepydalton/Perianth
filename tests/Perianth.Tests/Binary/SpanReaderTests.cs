using System;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Binary;

public sealed class SpanReaderTests
{
    // 0xA5 | 0x1234 | -32768 | 0x12345678 | 1.0f, each little-endian.
    private static ReadOnlySpan<byte> Fields =>
    [
        0xA5,
        0x34, 0x12,
        0x00, 0x80,
        0x78, 0x56, 0x34, 0x12,
        0x00, 0x00, 0x80, 0x3F,
    ];

    [Fact]
    public void Each_width_reads_little_endian_and_advances_by_its_own_size()
    {
        SpanReader reader = new(Fields);

        Assert.True(reader.TryReadByte(out byte u8));
        Assert.Equal(0xA5, u8);
        Assert.Equal(1, reader.Position);

        Assert.True(reader.TryReadUInt16(out ushort u16));
        Assert.Equal(0x1234, u16);
        Assert.Equal(3, reader.Position);

        Assert.True(reader.TryReadInt16(out short s16));
        Assert.Equal(short.MinValue, s16);
        Assert.Equal(5, reader.Position);

        Assert.True(reader.TryReadUInt32(out uint u32));
        Assert.Equal(0x12345678u, u32);
        Assert.Equal(9, reader.Position);

        Assert.True(reader.TryReadSingle(out float f32));
        Assert.Equal(1.0f, f32);
        Assert.Equal(13, reader.Position);

        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void A_read_that_runs_past_the_end_leaves_the_position_untouched()
    {
        SpanReader reader = new([0x01, 0x02, 0x03]);
        Assert.True(reader.TryReadUInt16(out _));
        Assert.Equal(2, reader.Position);

        Assert.False(reader.TryReadUInt32(out uint value));
        Assert.Equal(0u, value);
        Assert.Equal(2, reader.Position);

        // The cursor is still where the grammar left it, so the next read is the
        // one the grammar intended rather than one shifted by a failed attempt.
        Assert.True(reader.TryReadByte(out byte tail));
        Assert.Equal(0x03, tail);
    }

    [Fact]
    public void A_non_finite_float_is_returned_bit_for_bit_rather_than_refused()
    {
        // Which floats must be finite is grammar knowledge, and an unknown field
        // held for a future writer has to survive whatever was stored.
        SpanReader reader = new([0x00, 0x00, 0xC0, 0x7F, 0x00, 0x00, 0x80, 0xFF]);

        Assert.True(reader.TryReadSingle(out float quiet));
        Assert.True(float.IsNaN(quiet));
        Assert.Equal(unchecked((int)0x7FC00000), BitConverter.SingleToInt32Bits(quiet));

        Assert.True(reader.TryReadSingle(out float infinite));
        Assert.Equal(float.NegativeInfinity, infinite);
    }

    [Fact]
    public void Reading_bytes_hands_back_the_exact_window_and_advances()
    {
        SpanReader reader = new([0x10, 0x20, 0x30, 0x40]);
        Assert.True(reader.TrySkip(1));
        Assert.True(reader.TryReadBytes(2, out ReadOnlySpan<byte> window));

        Assert.Equal(2, window.Length);
        Assert.Equal(0x20, window[0]);
        Assert.Equal(0x30, window[1]);
        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void Slicing_at_an_absolute_offset_does_not_move_the_cursor()
    {
        SpanReader reader = new([0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80]);
        Assert.True(reader.TrySeek(6));

        Assert.True(reader.TrySlice(offset: 2, count: 2, stride: 2, out ReadOnlySpan<byte> window));
        Assert.Equal(4, window.Length);
        Assert.Equal(0x30, window[0]);
        Assert.Equal(6, reader.Position);
    }

    [Fact]
    public void A_slice_whose_elements_do_not_fit_is_refused()
    {
        SpanReader reader = new([0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80]);

        Assert.True(reader.TrySlice(offset: 2, count: 3, stride: 2, out _));
        Assert.False(reader.TrySlice(offset: 2, count: 4, stride: 2, out ReadOnlySpan<byte> window));
        Assert.True(window.IsEmpty);
    }

    [Fact]
    public void Seeking_to_the_end_is_allowed_and_seeking_past_it_is_not()
    {
        SpanReader reader = new([0x01, 0x02, 0x03, 0x04]);

        Assert.True(reader.TrySeek(4));
        Assert.Equal(0, reader.Remaining);
        Assert.False(reader.TryReadByte(out _));

        Assert.False(reader.TrySeek(5));
        Assert.Equal(4, reader.Position);

        Assert.False(reader.TrySeek(-1));
        Assert.Equal(4, reader.Position);
    }

    [Fact]
    public void Skipping_backwards_or_past_the_end_is_refused_and_moves_nothing()
    {
        SpanReader reader = new([0x01, 0x02, 0x03, 0x04]);
        Assert.True(reader.TrySkip(2));

        Assert.False(reader.TrySkip(3));
        Assert.Equal(2, reader.Position);

        Assert.False(reader.TrySkip(-1));
        Assert.Equal(2, reader.Position);

        Assert.True(reader.TrySkip(2));
        Assert.Equal(4, reader.Position);
    }

    [Fact]
    public void A_reader_over_an_empty_buffer_refuses_every_read()
    {
        SpanReader reader = new([]);

        Assert.Equal(0, reader.Length);
        Assert.False(reader.TryReadByte(out _));
        Assert.True(reader.TrySeek(0));
        Assert.True(reader.TrySlice(offset: 0, count: 0, stride: 1, out ReadOnlySpan<byte> window));
        Assert.True(window.IsEmpty);
    }
}
