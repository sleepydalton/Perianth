using System;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Binary;

/// <summary>
/// The packed Z-index writer, checked against the reader it must agree with.
/// </summary>
/// <remarks>
/// Round-tripping through the reader is the only check worth much here. The
/// convention — the low-order bit of the value is the first bit written — has no
/// consequence a test could see on its own: getting it backwards produces
/// plausible depths everywhere rather than a failure, and only disagreeing with
/// the reader shows it.
/// </remarks>
public sealed class BitWriterTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void Everything_written_at_a_width_reads_back_at_that_width(int width)
    {
        // Widths 1 to 32 are what five stored bits plus one can produce, and the
        // corpus uses 1, 2, 4, 8 and 16. Five is here because a width nothing
        // uses is exactly where an off-by-one hides.
        uint[] words = new uint[8];
        long bits = (long)words.Length * 32;
        int fields = (int)(bits / width);
        uint mask = width == 32 ? uint.MaxValue : (1u << width) - 1;

        for (int i = 0; i < fields; i++)
        {
            uint value = (uint)(i * 2654435761L) & mask;
            Assert.True(new BitWriter(words).TryWrite((long)i * width, width, value));
        }

        for (int i = 0; i < fields; i++)
        {
            Assert.True(new BitReader(words).TryRead((long)i * width, width, out uint read));
            Assert.Equal((uint)(i * 2654435761L) & mask, read);
        }
    }

    [Fact]
    public void Writing_one_field_leaves_its_neighbours_alone()
    {
        // The stream has no padding between fields, so a write that cleared a
        // whole word would take other vertices' depths with it -- and they belong
        // to the same part, so the damage would look like the edit.
        uint[] words = [0xFFFF_FFFF, 0xFFFF_FFFF];

        Assert.True(new BitWriter(words).TryWrite(12, 4, 0));

        Assert.True(new BitReader(words).TryRead(8, 4, out uint before));
        Assert.True(new BitReader(words).TryRead(16, 4, out uint after));
        Assert.Equal(0xFu, before);
        Assert.Equal(0xFu, after);
    }

    [Fact]
    public void A_field_crossing_a_word_boundary_round_trips()
    {
        uint[] words = new uint[2];

        Assert.True(new BitWriter(words).TryWrite(30, 8, 0xA5));

        Assert.True(new BitReader(words).TryRead(30, 8, out uint read));
        Assert.Equal(0xA5u, read);
    }

    [Fact]
    public void A_crossing_write_leaves_the_bits_beyond_it_alone()
    {
        uint[] words = [0, 0xFFFF_FFFF];

        Assert.True(new BitWriter(words).TryWrite(30, 8, 0));

        // Six bits of the field lie in the second word; everything above them
        // must survive.
        Assert.True(new BitReader(words).TryRead(38, 8, out uint beyond));
        Assert.Equal(0xFFu, beyond);
    }

    [Fact]
    public void A_value_too_wide_for_its_field_refuses_rather_than_being_truncated()
    {
        // Truncating would point a vertex at a different depth and still produce
        // a file that loads, which is the failure this project refuses to make
        // possible.
        uint[] words = new uint[1];

        Assert.False(new BitWriter(words).TryWrite(0, 4, 16));
        Assert.True(new BitWriter(words).TryWrite(0, 4, 15));
    }

    [Fact]
    public void A_field_running_past_the_end_refuses()
    {
        uint[] words = new uint[1];

        Assert.False(new BitWriter(words).TryWrite(30, 4, 0));
        Assert.False(new BitWriter(words).TryWrite(-1, 4, 0));
    }

    [Fact]
    public void A_width_the_grammar_cannot_produce_is_a_fault_rather_than_a_refusal()
    {
        uint[] words = new uint[1];

        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(words).TryWrite(0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BitWriter(words).TryWrite(0, 33, 0));
    }
}
