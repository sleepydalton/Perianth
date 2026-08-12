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
