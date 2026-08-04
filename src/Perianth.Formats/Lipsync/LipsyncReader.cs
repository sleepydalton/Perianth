using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Lipsync;

/// <summary>One serialized key of a lip-sync schedule: a signed key time and its selector.</summary>
public readonly record struct LipsyncPair(int KeyTime, int Selector);

/// <summary>
/// Reads the one proven lip-sync schedule format: a compact BVM database keyed by
/// numeric speech ID.
/// </summary>
/// <remarks>
/// The container opens <c>FF 42 56 4D</c> and stores a table of ASCII speech-ID
/// strings, then one schedule per string ordinal. Every count and integer uses
/// the same compact little-endian encoding, whose two high bits of the first byte
/// pick how many further bytes follow — a plain byte read desynchronises the whole
/// table at the first value of 64 or more. The requested ID must occur exactly
/// once, its key times must be monotonic and fit their packed fields, and every
/// byte must be consumed.
/// </remarks>
public static class LipsyncReader
{
    /// <summary>
    /// Every speech ID the database holds a schedule for.
    /// </summary>
    /// <remarks>
    /// A front end needs this to answer a question the caller cannot otherwise
    /// answer: whether the number they typed will move the mouth. Audio and
    /// schedules are different populations — 55,677 lines are voiced and 35,334
    /// have schedules, and only 31,865 have both — so a line that plays may
    /// still leave the face still, and the two must be reported apart.
    /// </remarks>
    public static Result<ImmutableArray<string>> Ids(SourceFile file)
    {
        System.ArgumentNullException.ThrowIfNull(file);

        SpanReader reader = file.CreateReader();

        if (!reader.TryReadBytes(4, out System.ReadOnlySpan<byte> magic)
            || magic[0] != 0xFF || magic[1] != (byte)'B' || magic[2] != (byte)'V' || magic[3] != (byte)'M')
        {
            return Refusal.Malformed("The lip-sync input is not an observed BVM database.");
        }

        Result<int> entryCount = CompactUnsigned(ref reader);
        if (!entryCount.TryGetValue(out int entries, out Refusal? countRefusal))
        {
            return countRefusal;
        }

        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(entries);
        for (int ordinal = 0; ordinal < entries; ordinal++)
        {
            Result<string> name = Text(ref reader, ordinal);
            if (!name.TryGetValue(out string? value, out Refusal? nameRefusal))
            {
                return nameRefusal;
            }

            names.Add(value);
        }

        return Result.Ok(names.MoveToImmutable());
    }

    /// <summary>Returns the packed key schedule serialized for <paramref name="speechId"/>.</summary>
    public static Result<ImmutableArray<LipsyncPair>> ReadSchedule(SourceFile file, string speechId)
    {
        System.ArgumentNullException.ThrowIfNull(file);
        System.ArgumentNullException.ThrowIfNull(speechId);

        SpanReader reader = file.CreateReader();

        // The container opens with the raw byte 0xFF then ASCII "BVM".
        if (!reader.TryReadBytes(4, out System.ReadOnlySpan<byte> magic)
            || magic[0] != 0xFF || magic[1] != (byte)'B' || magic[2] != (byte)'V' || magic[3] != (byte)'M')
        {
            return Refusal.Malformed("The lip-sync input is not an observed BVM database.");
        }

        Result<int> entryCount = CompactUnsigned(ref reader);
        if (!entryCount.TryGetValue(out int entries, out Refusal? countRefusal))
        {
            return countRefusal;
        }

        string[] names = new string[entries];
        for (int ordinal = 0; ordinal < entries; ordinal++)
        {
            Result<string> name = Text(ref reader, ordinal);
            if (!name.TryGetValue(out string? value, out Refusal? nameRefusal))
            {
                return nameRefusal;
            }

            names[ordinal] = value;
        }

        int target = -1;
        for (int ordinal = 0; ordinal < entries; ordinal++)
        {
            if (!string.Equals(names[ordinal], speechId, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (target >= 0)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Speech ID '{speechId}' is ambiguous in the lip-sync database."));
            }

            target = ordinal;
        }

        if (target < 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"Speech ID '{speechId}' is absent from the lip-sync database."));
        }

        if (!reader.TryReadBytes(2, out System.ReadOnlySpan<byte> mapHeader) || mapHeader[0] != 0x01 || mapHeader[1] != 0x00)
        {
            return Refusal.Malformed("The lip-sync database has an invalid map header.");
        }

        Result<int> mapCount = CompactUnsigned(ref reader);
        if (!mapCount.TryGetValue(out int mapEntries, out Refusal? mapRefusal))
        {
            return mapRefusal;
        }

        if (mapEntries != entries)
        {
            return Refusal.Malformed("The lip-sync database map count does not match its string count.");
        }

        ImmutableArray<LipsyncPair> selected = [];
        for (int ordinal = 0; ordinal < entries; ordinal++)
        {
            Result<int> stringRef = StringRef(ref reader);
            if (!stringRef.TryGetValue(out int reference, out Refusal? refRefusal))
            {
                return refRefusal;
            }

            if (reference != ordinal)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Lip-sync database entry {ordinal} has an invalid speech-ID reference."));
            }

            Result<ImmutableArray<LipsyncPair>> entry = Entry(ref reader, ordinal);
            if (!entry.TryGetValue(out ImmutableArray<LipsyncPair> pairs, out Refusal? entryRefusal))
            {
                return entryRefusal;
            }

            if (ordinal == target)
            {
                selected = pairs;
            }
        }

        if (reader.Position != reader.Length)
        {
            return Refusal.Malformed("The lip-sync database has trailing uninterpreted data.");
        }

        if (selected.Length < 2)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"Speech ID '{speechId}' has no complete lip-sync interval."));
        }

        // Selector 0 and values above 24 are observed but unresolved, so they are
        // refused rather than reinterpreted as a default or as sample zero.
        ImmutableSortedSet<int> unresolved =
            [.. selected.Where(p => p.Selector is < 1 or > 24).Select(p => p.Selector)];
        if (unresolved.Count > 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The lip-sync schedule uses unresolved selector value(s) {string.Join(", ", unresolved)}."));
        }

        return Result.Ok(selected);
    }

    private static Result<ImmutableArray<LipsyncPair>> Entry(ref SpanReader reader, int ordinal)
    {
        Result<int> pairCount = ArrayCount(ref reader);
        if (!pairCount.TryGetValue(out int count, out Refusal? countRefusal))
        {
            return countRefusal;
        }

        ImmutableArray<LipsyncPair>.Builder pairs = ImmutableArray.CreateBuilder<LipsyncPair>(count);
        int previousTime = 0;
        for (int pair = 0; pair < count; pair++)
        {
            Result<int> width = ArrayCount(ref reader);
            if (!width.TryGetValue(out int values, out Refusal? widthRefusal))
            {
                return widthRefusal;
            }

            if (values != 2)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Lip-sync entry {ordinal} pair {pair} is not a two-value pair."));
            }

            Result<long> keyTime = Integer(ref reader, signed: true);
            if (!keyTime.TryGetValue(out long time, out Refusal? timeRefusal))
            {
                return timeRefusal;
            }

            Result<long> selector = Integer(ref reader, signed: false);
            if (!selector.TryGetValue(out long select, out Refusal? selectorRefusal))
            {
                return selectorRefusal;
            }

            if (time is < short.MinValue or > short.MaxValue || select > ushort.MaxValue)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Lip-sync entry {ordinal} pair {pair} exceeds its packed field."));
            }

            if (pair > 0 && time < previousTime)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Lip-sync entry {ordinal} has non-monotonic key times."));
            }

            previousTime = (int)time;
            pairs.Add(new LipsyncPair((int)time, (int)select));
        }

        return Result.Ok(pairs.ToImmutable());
    }

    private static Result<string> Text(ref SpanReader reader, int ordinal)
    {
        Result<int> length = CompactUnsigned(ref reader);
        if (!length.TryGetValue(out int count, out Refusal? lengthRefusal))
        {
            return lengthRefusal;
        }

        if (!reader.TryReadBytes(count, out System.ReadOnlySpan<byte> bytes))
        {
            return Truncated(reader.Position);
        }

        foreach (byte b in bytes)
        {
            if (b > 0x7F)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"Lip-sync speech ID {ordinal} is not ASCII."));
            }
        }

        return Result.Ok(Encoding.ASCII.GetString(bytes));
    }

    private static Result<int> ArrayCount(ref SpanReader reader)
    {
        if (!reader.TryReadByte(out byte token) || token != 0x01)
        {
            return Refusal.Malformed("The lip-sync database has an invalid array token.");
        }

        Result<int> count = CompactUnsigned(ref reader);
        if (!count.TryGetValue(out int value, out Refusal? refusal))
        {
            return refusal;
        }

        if (!reader.TryReadByte(out byte marker) || marker != 0x00)
        {
            return Refusal.Malformed("The lip-sync database has an invalid array marker.");
        }

        return Result.Ok(value);
    }

    private static Result<int> StringRef(ref SpanReader reader)
    {
        if (!reader.TryReadByte(out byte token) || token != 0x0D)
        {
            return Refusal.Malformed("The lip-sync database has an invalid string-reference token.");
        }

        return CompactUnsigned(ref reader);
    }

    private static Result<long> Integer(ref SpanReader reader, bool signed)
    {
        if (!reader.TryReadByte(out byte token) || token != 0x04)
        {
            return Refusal.Malformed("The lip-sync database has an invalid integer token.");
        }

        if (signed)
        {
            return CompactSigned(ref reader);
        }

        Result<int> unsigned = CompactUnsigned(ref reader);
        return unsigned.TryGetValue(out int value, out Refusal? refusal) ? Result.Ok((long)value) : refusal;
    }

    private static Result<long> CompactSigned(ref SpanReader reader)
    {
        Result<(ulong Value, int Width)> compact = CompactWithBits(ref reader);
        if (!compact.TryGetValue(out (ulong Value, int Width) result, out Refusal? refusal))
        {
            return refusal;
        }

        ulong sign = 1UL << (result.Width - 1);
        long value = (result.Value & sign) != 0
            ? (long)result.Value - (1L << result.Width)
            : (long)result.Value;
        return Result.Ok(value);
    }

    private static Result<int> CompactUnsigned(ref SpanReader reader)
    {
        Result<(ulong Value, int Width)> compact = CompactWithBits(ref reader);
        if (!compact.TryGetValue(out (ulong Value, int Width) result, out Refusal? refusal))
        {
            return refusal;
        }

        if (result.Value > int.MaxValue)
        {
            return Refusal.Malformed("The lip-sync database encodes a count too large to address.");
        }

        return Result.Ok((int)result.Value);
    }

    private static Result<(ulong Value, int Width)> CompactWithBits(ref SpanReader reader)
    {
        if (!reader.TryReadByte(out byte first))
        {
            return Truncated(reader.Position);
        }

        int extra = (first >> 6) switch { 0 => 0, 1 => 1, 2 => 3, _ => 7 };
        ulong high = 0;
        if (extra > 0)
        {
            if (!reader.TryReadBytes(extra, out System.ReadOnlySpan<byte> bytes))
            {
                return Truncated(reader.Position);
            }

            for (int i = 0; i < extra; i++)
            {
                high |= (ulong)bytes[i] << (8 * i);
            }
        }

        return Result.Ok(((ulong)(first & 0x3Fu) | (high << 6), 6 + (extra * 8)));
    }

    private static Refusal Truncated(int position) => Refusal.Malformed(string.Create(
        CultureInfo.InvariantCulture, $"The lip-sync database is truncated at byte {position}."));
}
