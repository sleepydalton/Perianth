using System;

namespace Perianth.Formats.Binary;

/// <summary>
/// Writes bit fields into a 32-bit word array, in the packing
/// <see cref="BitReader"/> reads.
/// </summary>
/// <remarks>
/// <para>
/// The inverse of the reader and deliberately its mirror image, down to the
/// word-crossing rule and the 64-bit shift that keeps a width of 32 from being a
/// special case. The convention it must agree with is that the low-order bit of
/// the value is the first bit written; getting that backwards produces plausible
/// values everywhere rather than an obvious failure, which is why the two are
/// written to be read side by side and checked against each other.
/// </para>
/// <para>
/// It writes only the bits the field occupies. The packed Z-index stream is one
/// field per pool slot with no padding between them, so a write that cleared a
/// whole word would take its neighbours' depths with it — and those belong to
/// other vertices of the same part.
/// </para>
/// </remarks>
public readonly ref struct BitWriter
{
    private const int BitsPerWord = 32;

    private readonly Span<uint> _words;

    /// <summary>Creates a writer over <paramref name="words"/>.</summary>
    public BitWriter(Span<uint> words)
    {
        _words = words;
    }

    /// <summary>How many bits the array holds.</summary>
    public long BitCount => (long)_words.Length * BitsPerWord;

    /// <summary>
    /// Writes the low <paramref name="bitCount"/> bits of
    /// <paramref name="value"/> at <paramref name="bitOffset"/>.
    /// </summary>
    /// <returns>
    /// False when the field would run past the end of the array, when
    /// <paramref name="bitOffset"/> is negative, or when
    /// <paramref name="value"/> does not fit in <paramref name="bitCount"/> bits
    /// — the last because silently truncating a depth index would point a vertex
    /// at a different depth and still produce a file that loads.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="bitCount"/> is outside 1..32, which the grammar cannot
    /// produce and so is a fault in this code rather than in the data.
    /// </exception>
    public bool TryWrite(long bitOffset, int bitCount, uint value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bitCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bitCount, BitsPerWord);

        if (bitOffset < 0 || bitCount > BitCount - bitOffset)
        {
            return false;
        }

        ulong mask = (1UL << bitCount) - 1;
        if (value > mask)
        {
            return false;
        }

        int word = (int)(bitOffset / BitsPerWord);
        int bit = (int)(bitOffset % BitsPerWord);

        _words[word] = (uint)((_words[word] & ~(uint)(mask << bit)) | ((ulong)value << bit));

        int available = BitsPerWord - bit;
        if (available < bitCount)
        {
            // One boundary at most, as the reader relies on: a field is never
            // wider than a word, so it can reach only into the next one.
            ulong carried = mask >> available;
            _words[word + 1] = (uint)((_words[word + 1] & ~(uint)carried) | (value >> available));
        }

        return true;
    }
}
