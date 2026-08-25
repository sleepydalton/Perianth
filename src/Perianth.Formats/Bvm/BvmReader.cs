using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Bvm;

/// <summary>
/// Reads the string table of a BVM container — an animation system
/// (<c>.manimsys</c>) or an actor definition (<c>.mgraphobject</c>).
/// </summary>
/// <remarks>
/// <para>
/// The table is what carries asset paths, so reading it answers what the game
/// itself assigns to an actor rather than what a filename convention implies.
/// The graph that follows is left as a range: its value tags are read but its
/// container header is not, and a reader that guessed at it would produce a
/// plausible tree rather than a refusal.
/// </para>
/// <para>
/// This is a strictly weaker claim than "the file is understood", and it is
/// deliberately the one made. The table says which assets a system mentions; it
/// cannot say which node references which, because that relationship lives in
/// the graph. Callers that need one answer must handle a system that offers
/// several, and must not pick.
/// </para>
/// </remarks>
public static class BvmReader
{
    /// <summary>The container's four-byte signature.</summary>
    private static ReadOnlySpan<byte> Magic => [0xFF, (byte)'B', (byte)'V', (byte)'M'];

    /// <summary>
    /// The value tag every observed graph begins with. It is not decoded here —
    /// it is asserted, because landing on it is what shows the table was read to
    /// its true end rather than to a plausible one.
    /// </summary>
    private const byte ContainerTag = 0x01;

    /// <summary>Reads the string table and locates the graph.</summary>
    public static Result<BvmFile> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        SpanReader reader = file.CreateReader();

        if (!reader.TryReadBytes(Magic.Length, out ReadOnlySpan<byte> magic) || !magic.SequenceEqual(Magic))
        {
            return Refusal.Malformed("The input is not a BVM container.");
        }

        if (!CompactInteger.TryReadCount(ref reader, out int count))
        {
            return Refusal.Malformed(Truncated(reader.Position));
        }

        // The count precedes the strings and each string precedes its bytes, so a
        // count larger than the file cannot be satisfied. Rejecting it before
        // allocating keeps a malformed header from asking for gigabytes.
        if (count > reader.Remaining)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The BVM container declares {count} strings, more than its {reader.Remaining} remaining bytes can hold."));
        }

        ImmutableArray<string>.Builder strings = ImmutableArray.CreateBuilder<string>(count);
        for (int ordinal = 0; ordinal < count; ordinal++)
        {
            if (!CompactInteger.TryReadCount(ref reader, out int length)
                || !reader.TryReadBytes(length, out ReadOnlySpan<byte> bytes))
            {
                return Refusal.Malformed(Truncated(reader.Position));
            }

            string? text = Utf8(bytes);
            if (text is null)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"String {ordinal} of the BVM container is not valid UTF-8."));
            }

            strings.Add(text);
        }

        // Every observed container opens its graph with a container tag. A table
        // read one byte short or long lands elsewhere, so this turns a silent
        // desynchronisation into a refusal at the point it happened.
        if (!reader.TryReadByte(out byte tag) || tag != ContainerTag)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The BVM string table ends at byte {reader.Position} without reaching the graph."));
        }

        int start = reader.Position - 1;
        return Result.Ok(new BvmFile(strings.MoveToImmutable(), new ByteRange(start, reader.Length - start)));
    }

    /// <summary>
    /// How deep a graph may nest before this calls it malformed.
    /// </summary>
    /// <remarks>
    /// A guard against a crafted file, not a property of the format: the decoder
    /// recurses, so an unbounded depth is a stack overflow rather than a
    /// refusal. The deepest graph in the 15,399 shipped containers is far below
    /// this, and a file that exceeds it is refused rather than truncated.
    /// </remarks>
    private const int MaxDepth = 128;

    /// <summary>
    /// The most entries a container may declare, matching the engine's own
    /// rejection.
    /// </summary>
    private const int MaxEntries = 0x10000000;

    /// <summary>Reads the string table and the graph that follows it.</summary>
    /// <remarks>
    /// <para>
    /// The whole file, which is what a writer needs. It asserts that the graph
    /// ends exactly where the file does: a decoder that stopped early would
    /// produce a plausible tree over half a file, and only counting the bytes
    /// can tell that from a correct read.
    /// </para>
    /// <para>
    /// The grammar is the engine's <c>DecodeBvmValue</c> and
    /// <c>DecodeBvmContainer</c> rather than an inference from the bytes —
    /// Roadmap §10.86. Nothing here interprets a payload; see
    /// <see cref="BvmValue"/> for why.
    /// </para>
    /// </remarks>
    public static Result<BvmDocument> ReadDocument(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        Result<BvmFile> table = Read(file);
        if (!table.TryGetValue(out BvmFile? read, out Refusal? refusal))
        {
            return refusal;
        }

        SpanReader reader = file.CreateReader();
        if (!reader.TrySeek(read.Graph.Offset))
        {
            return Refusal.Malformed(Truncated(read.Graph.Offset));
        }

        Result<BvmValue> graph = ReadValue(ref reader, read.Strings.Length, depth: 0);
        if (!graph.TryGetValue(out BvmValue? value, out Refusal? bad))
        {
            return bad;
        }

        // The graph is the rest of the file, so anything left over means the
        // grammar and the bytes disagree somewhere this cannot see.
        if (reader.Remaining != 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The BVM graph ends at byte {reader.Position} with {reader.Remaining} bytes left in the file."));
        }

        return Result.Ok(new BvmDocument(read.Strings, value));
    }

    private static Result<BvmValue> ReadValue(ref SpanReader reader, int strings, int depth)
    {
        if (depth > MaxDepth)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The BVM graph nests deeper than {MaxDepth} at byte {reader.Position}."));
        }

        int at = reader.Position;
        if (!reader.TryReadByte(out byte tag))
        {
            return Refusal.Malformed(Truncated(at));
        }

        switch (tag)
        {
            case BvmMarker.Empty:
            case BvmMarker.True:
            case BvmMarker.False:
                return Result.Ok<BvmValue>(new BvmMarker(tag));

            case BvmContainer.ContainerTag:
                return ReadContainer(ref reader, strings, depth);

            case BvmString.StringA:
            case BvmString.StringB:
                {
                    if (!CompactInteger.TryReadCount(ref reader, out int index))
                    {
                        return Refusal.Malformed(Truncated(reader.Position));
                    }

                    // Checked here rather than left to a caller: a reference past
                    // the table is the shape a desynchronised read takes, and
                    // catching it names the byte where it happened.
                    if (index >= strings)
                    {
                        return Refusal.Malformed(string.Create(
                            CultureInfo.InvariantCulture,
                            $"The BVM graph references string {index} of {strings} at byte {at}."));
                    }

                    return Result.Ok<BvmValue>(new BvmString(tag, index));
                }
        }

        int numbers = BvmNumbers.CountFor(tag);
        if (numbers > 0)
        {
            ImmutableArray<int>.Builder values = ImmutableArray.CreateBuilder<int>(numbers);
            for (int i = 0; i < numbers; i++)
            {
                if (!CompactInteger.TryReadSigned(ref reader, out int value))
                {
                    return Refusal.Malformed(Truncated(reader.Position));
                }

                values.Add(value);
            }

            return Result.Ok<BvmValue>(new BvmNumbers(tag, values.MoveToImmutable()));
        }

        int width = BvmRaw.WidthFor(tag);
        if (width > 0)
        {
            return reader.TryReadBytes(width, out ReadOnlySpan<byte> raw)
                ? Result.Ok<BvmValue>(new BvmRaw(tag, raw.ToArray()))
                : Refusal.Malformed(Truncated(reader.Position));
        }

        return Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"The BVM graph carries an unknown value tag 0x{tag:x2} at byte {at}."));
    }

    private static Result<BvmValue> ReadContainer(ref SpanReader reader, int strings, int depth)
    {
        int at = reader.Position;

        if (!CompactInteger.TryReadCount(ref reader, out int items)
            || !CompactInteger.TryReadCount(ref reader, out int entries))
        {
            return Refusal.Malformed(Truncated(reader.Position));
        }

        if (items > MaxEntries || entries > MaxEntries)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A BVM container at byte {at} declares {items} array and {entries} map entries."));
        }

        // Each entry is at least one byte, so a count past the remaining bytes
        // cannot be satisfied — refused before allocating, as the string table is.
        if ((long)items + entries > reader.Remaining)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A BVM container at byte {at} declares more entries than its {reader.Remaining} remaining bytes can hold."));
        }

        ImmutableArray<BvmValue>.Builder array = ImmutableArray.CreateBuilder<BvmValue>(items);
        for (int i = 0; i < items; i++)
        {
            Result<BvmValue> item = ReadValue(ref reader, strings, depth + 1);
            if (!item.TryGetValue(out BvmValue? value, out Refusal? refusal))
            {
                return refusal;
            }

            array.Add(value);
        }

        ImmutableArray<BvmPair>.Builder pairs = ImmutableArray.CreateBuilder<BvmPair>(entries);
        for (int i = 0; i < entries; i++)
        {
            // A key is a full value, not a string index, which is what lets one
            // container be keyed by name and another by integer.
            Result<BvmValue> key = ReadValue(ref reader, strings, depth + 1);
            if (!key.TryGetValue(out BvmValue? left, out Refusal? keyRefusal))
            {
                return keyRefusal;
            }

            Result<BvmValue> mapped = ReadValue(ref reader, strings, depth + 1);
            if (!mapped.TryGetValue(out BvmValue? right, out Refusal? valueRefusal))
            {
                return valueRefusal;
            }

            pairs.Add(new BvmPair(left, right));
        }

        return Result.Ok<BvmValue>(new BvmContainer(array.MoveToImmutable(), pairs.MoveToImmutable()));
    }

    /// <summary>
    /// Decodes strictly: an invalid sequence is a refusal, not a replacement
    /// character. Strings carry asset paths, and a path silently repaired names
    /// a file that does not exist.
    /// </summary>
    private static string? Utf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Truncated(int position) => string.Create(
        CultureInfo.InvariantCulture, $"The BVM container is truncated at byte {position}.");
}
