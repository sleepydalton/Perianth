using System;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Binary;

/// <summary>
/// The BVM container's variable-width integer, at the widths a real file never
/// reaches.
/// </summary>
/// <remarks>
/// The narrow widths are already covered through <c>BvmReaderTests</c>, which
/// exercises them the way the format is used. What had no coverage at all was
/// the <c>0xC0</c> selector, and it was implemented wrongly for that reason:
/// it read seven further bytes above the first six value bits, where the engine
/// reads a plain <c>uint32</c> and discards those bits. No corpus file uses the
/// form, so nothing disagreed — which is why the test is written from the
/// engine's own reader rather than from anything this repository already
/// believed.
/// </remarks>
public sealed class CompactIntegerTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x3FFFFFFFu)]   // the last value the three-byte form can hold
    [InlineData(0x40000000u)]   // the first that needs the wide form
    [InlineData(uint.MaxValue)]
    public void The_wide_selector_carries_a_whole_uint32(uint value)
    {
        byte[] bytes = [0xC0, 0, 0, 0, 0, 0xEE];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(1), value);

        SpanReader reader = new(bytes);
        Assert.True(CompactInteger.TryRead(ref reader, out ulong read));
        Assert.Equal(value, read);

        // Five bytes consumed, not eight: a wrong width reads the value
        // correctly whenever the extra bytes happen to be zero, and only the
        // cursor shows it.
        Assert.Equal(5, reader.Position);
    }

    [Fact]
    public void The_wide_selector_ignores_the_first_bytes_low_bits()
    {
        // The engine masks nothing here — it overwrites. A reader that ORed the
        // low six bits in would return 0x2A rather than zero.
        byte[] bytes = [0xC0 | 0x2A, 0, 0, 0, 0];

        SpanReader reader = new(bytes);
        Assert.True(CompactInteger.TryRead(ref reader, out ulong read));
        Assert.Equal(0ul, read);
    }

    [Fact]
    public void A_truncated_wide_value_fails_without_moving_the_cursor()
    {
        byte[] bytes = [0xC0, 0x01, 0x02];

        SpanReader reader = new(bytes);
        Assert.False(CompactInteger.TryRead(ref reader, out _));
        Assert.Equal(0, reader.Position);
    }

    [Fact]
    public void A_count_wider_than_an_index_is_refused()
    {
        byte[] bytes = [0xC0, 0xFF, 0xFF, 0xFF, 0xFF];

        SpanReader reader = new(bytes);
        Assert.False(CompactInteger.TryReadCount(ref reader, out _));
        Assert.Equal(0, reader.Position);
    }
}
