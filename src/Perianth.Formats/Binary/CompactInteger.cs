namespace Perianth.Formats.Binary;

/// <summary>
/// The variable-width unsigned integer used throughout the BVM container.
/// </summary>
/// <remarks>
/// <para>
/// Six value bits live in the first byte. Its top two bits select how many
/// further bytes follow — none, one, three or seven — and those carry the
/// remaining value little-endian. It is not LEB128: the width is chosen up
/// front rather than signalled by a continuation bit on each byte.
/// </para>
/// <para>
/// Every value below 64 is a single byte that reads correctly under a plain byte
/// read, which is what makes a wrong implementation so hard to notice. It first
/// diverges at 64, and in a container whose strings are mostly short names, the
/// values that reach 64 are the long asset paths — so a naive reader gets every
/// name right and desynchronises precisely on the paths worth reading.
/// </para>
/// <para>
/// Failure is <c>false</c> rather than a refusal, matching <see cref="SpanReader"/>:
/// this knows a read ran off the end, and only a grammar knows what that meant.
/// </para>
/// </remarks>
public static class CompactInteger
{
    /// <summary>
    /// Reads one value, advancing <paramref name="reader"/> past it.
    /// </summary>
    /// <remarks>
    /// The cursor is left untouched on failure, so there is nothing to restore
    /// and no shifted-cursor retry to be tempted by.
    /// </remarks>
    public static bool TryRead(ref SpanReader reader, out ulong value)
    {
        value = 0;
        int start = reader.Position;

        if (!reader.TryReadByte(out byte first))
        {
            return false;
        }

        int extra = (first >> 6) switch { 0 => 0, 1 => 1, 2 => 3, _ => 7 };
        ulong high = 0;
        if (extra > 0)
        {
            if (!reader.TryReadBytes(extra, out System.ReadOnlySpan<byte> bytes))
            {
                _ = reader.TrySeek(start);
                return false;
            }

            for (int i = 0; i < extra; i++)
            {
                high |= (ulong)bytes[i] << (8 * i);
            }
        }

        value = (ulong)(first & 0x3Fu) | (high << 6);
        return true;
    }

    /// <summary>
    /// Reads one value that must address a buffer, so it has to fit an
    /// <see cref="int"/>.
    /// </summary>
    /// <remarks>
    /// A count wider than an index is a malformed file rather than a large one,
    /// and rejecting it here keeps every caller from repeating the check.
    /// </remarks>
    public static bool TryReadCount(ref SpanReader reader, out int value)
    {
        value = 0;
        int start = reader.Position;

        if (!TryRead(ref reader, out ulong wide) || wide > int.MaxValue)
        {
            _ = reader.TrySeek(start);
            return false;
        }

        value = (int)wide;
        return true;
    }
}
