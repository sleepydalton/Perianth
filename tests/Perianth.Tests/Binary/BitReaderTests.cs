using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Binary;

public sealed class BitReaderTests
{
    [Fact]
    public void The_specification_vectors_for_a_cross_word_read_hold()
    {
        // This vector stops being a transcribed number here. Until now nothing
        // could check that the recorded 0x78 and 0x0F were the right answers;
        // now the reader that has to produce them does.
        JsonElement group = SpecVectors.Group("packed_z_cross_word");

        List<uint> words = [];
        foreach (JsonElement word in group.GetProperty("words").EnumerateArray())
        {
            words.Add(uint.Parse(word.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        BitReader reader = new(words.ToArray());
        foreach (JsonElement read in group.GetProperty("cases").EnumerateArray())
        {
            Assert.True(reader.TryRead(
                read.GetProperty("bit_offset").GetInt32(),
                read.GetProperty("bit_count").GetInt32(),
                out uint value));
            Assert.Equal(read.GetProperty("value").GetUInt32(), value);
        }
    }

    [Fact]
    public void The_first_bit_read_becomes_the_low_order_bit_of_the_result()
    {
        // Getting this backwards produces plausible values everywhere rather
        // than an obvious failure, so it is pinned on its own.
        BitReader reader = new([0b1011u]);

        Assert.True(reader.TryRead(0, 1, out uint first));
        Assert.Equal(1u, first);

        Assert.True(reader.TryRead(1, 1, out uint second));
        Assert.Equal(1u, second);

        Assert.True(reader.TryRead(2, 1, out uint third));
        Assert.Equal(0u, third);

        Assert.True(reader.TryRead(0, 4, out uint all));
        Assert.Equal(0b1011u, all);
    }

    [Fact]
    public void A_full_width_read_keeps_every_bit()
    {
        BitReader reader = new([0xFFFF_FFFFu, 0x0000_0000u]);

        Assert.True(reader.TryRead(0, 32, out uint whole));
        Assert.Equal(uint.MaxValue, whole);

        // Straddling with the full width too: half ones, half zeroes.
        Assert.True(reader.TryRead(16, 32, out uint straddled));
        Assert.Equal(0x0000_FFFFu, straddled);
    }

    [Fact]
    public void A_read_reaching_past_the_last_word_is_refused()
    {
        BitReader reader = new([0xFFFF_FFFFu]);

        Assert.Equal(32, reader.BitCount);
        Assert.True(reader.TryRead(24, 8, out _));
        Assert.False(reader.TryRead(25, 8, out uint value));
        Assert.Equal(0u, value);
        Assert.False(reader.TryRead(32, 1, out _));
    }

    [Fact]
    public void A_negative_offset_is_refused()
    {
        BitReader reader = new([0xFFFF_FFFFu]);

        Assert.False(reader.TryRead(-1, 4, out _));
    }

    [Fact]
    public void An_empty_word_array_has_nothing_to_read()
    {
        BitReader reader = new([]);

        Assert.Equal(0, reader.BitCount);
        Assert.False(reader.TryRead(0, 1, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(-1)]
    public void A_width_outside_one_to_thirty_two_is_a_fault(int bitCount)
    {
        // A width is derived from five stored bits and can only be 1..32, so
        // anything else is a bug here rather than a property of a file.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            BitReader reader = new([0xFFFF_FFFFu]);
            reader.TryRead(0, bitCount, out _);
        });
    }
}
