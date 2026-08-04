using System;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Binary;

public sealed class BoundedRangeTests
{
    [Fact]
    public void A_range_that_ends_exactly_at_the_buffer_end_is_valid()
    {
        Assert.True(BoundedRange.TryResolve(length: 16, offset: 4, count: 3, stride: 4, out int start, out int byteCount));
        Assert.Equal(4, start);
        Assert.Equal(12, byteCount);
    }

    [Fact]
    public void A_range_that_ends_one_byte_past_the_buffer_is_refused()
    {
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 5, count: 3, stride: 4, out _, out _));
    }

    [Fact]
    public void An_empty_range_at_the_buffer_end_is_valid()
    {
        Assert.True(BoundedRange.TryResolve(length: 16, offset: 16, count: 0, stride: 1, out int start, out int byteCount));
        Assert.Equal(16, start);
        Assert.Equal(0, byteCount);
    }

    [Fact]
    public void An_empty_range_is_valid_however_wide_its_stride()
    {
        // Nothing is read, so a stride larger than the buffer is not a conflict.
        Assert.True(BoundedRange.TryResolve(length: 16, offset: 16, count: 0, stride: 1000, out _, out int byteCount));
        Assert.Equal(0, byteCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void An_offset_past_the_buffer_end_is_refused_even_with_no_elements(int stride)
    {
        // Both strides matter. Past the end the remaining count is negative, and
        // dividing it by a stride above one truncates toward zero: -1 / 4 is 0,
        // so a zero count compares as fitting. The explicit offset guard is what
        // refuses this, and only the wider stride shows that.
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 17, count: 0, stride: stride, out _, out _));
    }

    [Fact]
    public void A_negative_offset_is_refused()
    {
        Assert.False(BoundedRange.TryResolve(length: 16, offset: -1, count: 1, stride: 1, out _, out _));
    }

    [Fact]
    public void A_negative_count_is_refused()
    {
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 0, count: -1, stride: 1, out _, out _));
    }

    [Fact]
    public void Both_outputs_are_zero_when_the_range_is_refused()
    {
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 20, count: 4, stride: 4, out int start, out int byteCount));
        Assert.Equal(0, start);
        Assert.Equal(0, byteCount);
    }

    // The two tests below are the reason this arithmetic is centralised. Both
    // counts fit in an Int32 and are positive, so nothing about the call looks
    // suspicious; it is the product that overflows. A check written as
    // `offset + count * stride > length` in 32-bit arithmetic accepts both.

    [Fact]
    public void A_byte_total_that_wraps_to_zero_in_32_bit_arithmetic_is_refused()
    {
        // 0x2000_0000 * 8 == 2^32, whose low 32 bits are 0: a naive check reads
        // this as a zero-length range sitting comfortably inside the buffer.
        Assert.False(BoundedRange.TryResolve(length: 64, offset: 0, count: 0x2000_0000, stride: 8, out _, out _));
    }

    [Fact]
    public void A_byte_total_that_wraps_into_range_in_32_bit_arithmetic_is_refused()
    {
        // 0x4000_0001 * 4 == 0x1_0000_0004, whose low 32 bits are 4: a naive
        // check reads this as a four-byte range in a sixteen-byte buffer.
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 0, count: 0x4000_0001, stride: 4, out _, out _));
    }

    [Fact]
    public void A_count_too_large_for_an_int32_stays_too_large()
    {
        // A UInt32 field holding this value reaches the check intact instead of
        // arriving as a negative Int32 and being refused for the wrong reason.
        // Narrowing the arithmetic turns it into -1, which then compares as
        // fitting in any buffer.
        Assert.False(BoundedRange.TryResolve(length: 16, offset: 0, count: uint.MaxValue, stride: 1, out _, out _));
    }

    [Fact]
    public void A_stride_below_one_is_a_fault_rather_than_a_refusal()
    {
        // A stride is fixed by the grammar and can never come from a file, so a
        // zero or negative one is a bug in this code and not a bad input.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BoundedRange.TryResolve(length: 16, offset: 0, count: 1, stride: 0, out _, out _));
    }

    [Fact]
    public void A_negative_buffer_length_is_a_fault_rather_than_a_refusal()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BoundedRange.TryResolve(length: -1, offset: 0, count: 0, stride: 1, out _, out _));
    }
}
