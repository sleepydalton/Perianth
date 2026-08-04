using System;

namespace Perianth.Formats.Binary;

/// <summary>
/// The single place a byte range is checked against a buffer. Porting
/// specification section 4 requires <c>offset &gt;= 0</c>, <c>count &gt;= 0</c>
/// and <c>count * stride &lt;= length - offset</c>, computed so that overflow
/// cannot turn an invalid range into a valid one. Every read in every grammar
/// resolves its range here, including a read of a single byte: one path means
/// one thing to get right and one thing to mutate to prove it works.
/// </summary>
public static class BoundedRange
{
    /// <summary>
    /// Resolves <paramref name="count"/> elements of <paramref name="stride"/>
    /// bytes at <paramref name="offset"/> within a buffer of
    /// <paramref name="length"/> bytes.
    /// </summary>
    /// <returns>
    /// True when the range lies wholly inside the buffer, with
    /// <paramref name="start"/> and <paramref name="byteCount"/> set. False
    /// otherwise, with both set to zero.
    /// </returns>
    /// <remarks>
    /// <paramref name="offset"/> and <paramref name="count"/> are
    /// <see cref="long"/> so that a <see cref="uint"/> field read straight out
    /// of a file widens losslessly and is rejected on its merits, rather than
    /// being cast into a negative <see cref="int"/> and rejected by accident.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="length"/> is negative, or <paramref name="stride"/> is
    /// below one. Neither can come from a file: they are fixed by the buffer
    /// and by the grammar, so either one is a fault in this code.
    /// </exception>
    public static bool TryResolve(int length, long offset, long count, long stride, out int start, out int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, 1);

        start = 0;
        byteCount = 0;

        if (offset < 0 || count < 0 || offset > length)
        {
            return false;
        }

        // The comparison is a division rather than the multiplication the
        // specification words it as. They agree exactly for non-negative
        // integers, but division has no product to overflow, so the guard holds
        // for any input rather than for inputs that happen to stay small. The
        // multiplication below runs only once its result is known to fit.
        long remaining = length - offset;
        if (count > remaining / stride)
        {
            return false;
        }

        start = (int)offset;
        byteCount = (int)(count * stride);
        return true;
    }
}
