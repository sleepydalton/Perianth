namespace Perianth.Formats.Binary;

/// <summary>
/// The variable-width unsigned integer used throughout the BVM container.
/// </summary>
/// <remarks>
/// <para>
/// Six value bits live in the first byte. Its top two bits select what follows:
/// nothing, one further byte, or three, carrying the remaining value
/// little-endian above those six bits. The fourth selector, <c>0xC0</c>, is
/// different in kind — <b>a raw little-endian <c>uint32</c> follows and the
/// first byte's six bits are discarded</b>. It is not LEB128: the width is
/// chosen up front rather than signalled by a continuation bit on each byte.
/// </para>
/// <para>
/// The <c>0xC0</c> form was previously read as seven further bytes ORed above
/// the first six bits, which is wrong in both length and value. It was a latent
/// error rather than a live one: no shipped file uses the form, because every
/// count and length in the corpus is below 2^30, so the two readings never
/// disagree on real input. Corrected against the engine's own
/// <c>ParseBvmHeaderAndStringTable</c> and <c>DecodeBvmContainer</c>, which use
/// this same encoding for the string table and the graph alike — Roadmap
/// §10.86. <b>Agreement on the corpus was not evidence</b>, and is exactly the
/// shape of hazard the shader's Z-mask table turned out to have.
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

        // The widest selector is not "more of the same": it replaces the value
        // rather than extending it, so it cannot be folded into the loop below.
        if ((first & 0xC0) == 0xC0)
        {
            if (!reader.TryReadUInt32(out uint whole))
            {
                _ = reader.TrySeek(start);
                return false;
            }

            value = whole;
            return true;
        }

        int extra = (first >> 6) switch { 0 => 0, 1 => 1, _ => 3 };
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
    /// Reads the signed variant, which shares the widths and differs in what it
    /// does with the bits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same three narrow forms, then <b>sign-extended from the top bit of
    /// the value</b> — bit 6, 14 or 30 — rather than zero-extended. The
    /// <c>0xC0</c> form is a plain little-endian <c>int32</c> with nothing to
    /// extend.
    /// </para>
    /// <para>
    /// Two encodings in one format is not a quirk to be tidied away: the graph's
    /// container counts and string indices are unsigned and its numbers are
    /// signed, and reading one as the other is right for every non-negative
    /// value and wrong for the rest. From the engine's own
    /// <c>ReadBvmCompactSignedInt32</c> — Roadmap §10.86.
    /// </para>
    /// </remarks>
    public static bool TryReadSigned(ref SpanReader reader, out int value)
    {
        value = 0;
        int start = reader.Position;

        if (!reader.TryReadByte(out byte first))
        {
            return false;
        }

        if ((first & 0xC0) == 0xC0)
        {
            if (!reader.TryReadUInt32(out uint whole))
            {
                _ = reader.TrySeek(start);
                return false;
            }

            value = unchecked((int)whole);
            return true;
        }

        int extra = (first >> 6) switch { 0 => 0, 1 => 1, _ => 3 };
        uint magnitude = first & 0x3Fu;

        if (extra > 0)
        {
            if (!reader.TryReadBytes(extra, out System.ReadOnlySpan<byte> bytes))
            {
                _ = reader.TrySeek(start);
                return false;
            }

            for (int i = 0; i < extra; i++)
            {
                magnitude |= (uint)bytes[i] << (6 + (8 * i));
            }
        }

        int bits = extra switch { 0 => 6, 1 => 14, _ => 30 };
        uint sign = 1u << (bits - 1);
        value = unchecked((int)((magnitude ^ sign) - sign));
        return true;
    }

    /// <summary>
    /// Writes one unsigned value in the narrowest form that holds it.
    /// </summary>
    /// <remarks>
    /// <b>Narrowest, because that is what the files do.</b> The encoding is not
    /// canonical — a small value could legally be written wide — so this is a
    /// claim about the game's own writer rather than about the format, and it is
    /// the byte-identity oracle over 15,399 containers that keeps it honest. If
    /// a file ever disagrees, the fix is to keep the width that was read, not to
    /// widen everything.
    /// </remarks>
    public static void Write(System.Collections.Generic.List<byte> target, uint value)
    {
        System.ArgumentNullException.ThrowIfNull(target);

        if (value < 1u << 6)
        {
            target.Add((byte)value);
        }
        else if (value < 1u << 14)
        {
            target.Add((byte)(0x40 | (value & 0x3F)));
            target.Add((byte)(value >> 6));
        }
        else if (value < 1u << 30)
        {
            target.Add((byte)(0x80 | (value & 0x3F)));
            target.Add((byte)(value >> 6));
            target.Add((byte)(value >> 14));
            target.Add((byte)(value >> 22));
        }
        else
        {
            target.Add(0xC0);
            target.Add((byte)value);
            target.Add((byte)(value >> 8));
            target.Add((byte)(value >> 16));
            target.Add((byte)(value >> 24));
        }
    }

    /// <summary>Writes one signed value in the narrowest form that holds it.</summary>
    /// <remarks>
    /// The narrow forms hold a two's-complement value of 6, 14 or 30 bits, so
    /// the test is whether sign-extending the truncated value returns what was
    /// given. Writing <c>-1</c> as a single byte <c>0x3F</c> is correct and is
    /// what the shipped files do.
    /// </remarks>
    public static void WriteSigned(System.Collections.Generic.List<byte> target, int value)
    {
        System.ArgumentNullException.ThrowIfNull(target);

        if (value is >= -(1 << 5) and < 1 << 5)
        {
            target.Add((byte)(value & 0x3F));
        }
        else if (value is >= -(1 << 13) and < 1 << 13)
        {
            uint bits = (uint)value & 0x3FFFu;
            target.Add((byte)(0x40 | (bits & 0x3F)));
            target.Add((byte)(bits >> 6));
        }
        else if (value is >= -(1 << 29) and < 1 << 29)
        {
            uint bits = (uint)value & 0x3FFFFFFFu;
            target.Add((byte)(0x80 | (bits & 0x3F)));
            target.Add((byte)(bits >> 6));
            target.Add((byte)(bits >> 14));
            target.Add((byte)(bits >> 22));
        }
        else
        {
            uint bits = unchecked((uint)value);
            target.Add(0xC0);
            target.Add((byte)bits);
            target.Add((byte)(bits >> 8));
            target.Add((byte)(bits >> 16));
            target.Add((byte)(bits >> 24));
        }
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
