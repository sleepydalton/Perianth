using System;

namespace Perianth.Formats.Binary;

/// <summary>
/// Reads bit fields out of a 32-bit word array, in the packing the mode-3
/// Z index uses.
/// </summary>
/// <remarks>
/// <para>
/// The operand is words rather than bytes on purpose. Specification section 5.3
/// describes the packing in terms of 32-bit words and promises a read crosses at
/// most one word boundary; taking <see cref="uint"/> makes that a property of
/// the type instead of arithmetic to re-derive, and it is the same shape the
/// section 7.6 vectors are written in.
/// </para>
/// <para>
/// The low-order bit of the result is the first bit read. That is the whole
/// convention, and getting it backwards produces plausible values everywhere
/// rather than an obvious failure, which is why it is stated here and pinned by
/// a golden vector.
/// </para>
/// </remarks>
public readonly ref struct BitReader
{
    private readonly ReadOnlySpan<uint> _words;

    /// <summary>Creates a reader over <paramref name="words"/>.</summary>
    public BitReader(ReadOnlySpan<uint> words)
    {
        _words = words;
    }

    /// <summary>How many bits the array holds.</summary>
    public long BitCount => (long)_words.Length * BitsPerWord;

    private const int BitsPerWord = 32;

    /// <summary>
    /// Reads <paramref name="bitCount"/> bits starting at
    /// <paramref name="bitOffset"/>.
    /// </summary>
    /// <returns>
    /// False when the field would run past the end of the array, or when
    /// <paramref name="bitOffset"/> is negative.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bitCount"/> is outside 1..32. A width comes from the
    /// grammar, which derives it from five stored bits and can only produce
    /// 1..32, so anything else is a fault in this code rather than bad data.
    /// </exception>
    public bool TryRead(long bitOffset, int bitCount, out uint value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, BitsPerWord);

        value = 0;
        if (bitOffset < 0 || bitCount > BitCount - bitOffset)
        {
            return false;
        }

        int word = (int)(bitOffset / BitsPerWord);
        int bit = (int)(bitOffset % BitsPerWord);

        ulong chunk = _words[word] >> bit;
        int available = BitsPerWord - bit;
        if (available < bitCount)
        {
            // One boundary at most: the field is never wider than a word, so a
            // read starting inside one word can reach only into the next.
            chunk |= (ulong)_words[word + 1] << available;
        }

        // A 64-bit shift masks its count to 63, so a width of 32 is not a special
        // case here: 1UL << 32 is 2^32 and the mask comes out as 0xFFFFFFFF.
        value = (uint)(chunk & ((1UL << bitCount) - 1));
        return true;
    }
}
