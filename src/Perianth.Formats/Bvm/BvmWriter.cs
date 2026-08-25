using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Bvm;

/// <summary>
/// Writes a <see cref="BvmDocument"/> back to bytes.
/// </summary>
/// <remarks>
/// <para>
/// The fourth writer, and the one both remaining rungs of import wait on: a
/// character needs an actor graph object and a prop needs its own, while
/// equipment needed none (Roadmap §10.87). It is the same bar as the other
/// three — <b>read a real file, write it back, require the bytes to match</b> —
/// over all 15,399 shipped containers, and for the same reason: a wrong read
/// refuses, while a wrong write produces a file that loads and misbehaves.
/// </para>
/// <para>
/// So this writer has no opinions. It does not sort a container's map, merge the
/// two string tags, decode a raw payload, or rebuild the string table. That last
/// one deserves saying plainly: <b>the table is written exactly as it was read,
/// duplicates and unreferenced entries included</b>. Dropping an entry nothing
/// points at would be an improvement that renumbers every reference after it,
/// and compacting a table is not what "write this file back" means.
/// </para>
/// <para>
/// One thing here is a measurement rather than a rule of the format. The compact
/// integer encoding is <b>not canonical</b> — a small value may legally be
/// written wide — so writing the narrowest form is a claim about the game's own
/// writer. The corpus oracle is what tests it, and if a file ever disagrees the
/// fix is to keep the width that was read rather than to widen everything.
/// </para>
/// </remarks>
public static class BvmWriter
{
    /// <summary>The container's four-byte signature.</summary>
    private static ReadOnlySpan<byte> Magic => [0xFF, (byte)'B', (byte)'V', (byte)'M'];

    /// <summary>Serializes a document, or refuses if it cannot be spelled.</summary>
    /// <remarks>
    /// Every refusal here is a document the grammar cannot express, and none may
    /// arise from a document this project's reader produced — a value whose
    /// payload does not match its tag, or a string reference past the table.
    /// </remarks>
    public static Result<byte[]> Write(BvmDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<byte> bytes = new(4096);
        bytes.AddRange(Magic);

        CompactInteger.Write(bytes, (uint)document.Strings.Length);
        foreach (string text in document.Strings)
        {
            // Counted in bytes, not characters. A path with a character outside
            // ASCII would otherwise declare a length shorter than it writes, and
            // every value after it would be read from the wrong offset.
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            CompactInteger.Write(bytes, (uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        Refusal? refusal = AddValue(bytes, document.Graph, document.Strings.Length, depth: 0);
        return refusal is null ? Result.Ok(bytes.ToArray()) : refusal;
    }

    /// <summary>
    /// How deep a graph may nest, matching the reader so that a document which
    /// read cannot fail to write.
    /// </summary>
    private const int MaxDepth = 128;

    private static Refusal? AddValue(List<byte> bytes, BvmValue value, int strings, int depth)
    {
        if (depth > MaxDepth)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The graph nests deeper than {MaxDepth}, which this cannot write."));
        }

        switch (value)
        {
            case BvmMarker marker:
                if (marker.Tag is not (BvmMarker.Empty or BvmMarker.True or BvmMarker.False))
                {
                    return Tag(marker.Tag, "a value with no payload");
                }

                bytes.Add(marker.Tag);
                return null;

            case BvmString reference:
                if (reference.Tag is not (BvmString.StringA or BvmString.StringB))
                {
                    return Tag(reference.Tag, "a string reference");
                }

                if (reference.Index < 0 || reference.Index >= strings)
                {
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"A string reference names entry {reference.Index} of a table holding {strings}."));
                }

                bytes.Add(reference.Tag);
                CompactInteger.Write(bytes, (uint)reference.Index);
                return null;

            case BvmNumbers numbers:
                {
                    int expected = BvmNumbers.CountFor(numbers.Tag);
                    if (expected == 0)
                    {
                        return Tag(numbers.Tag, "signed integers");
                    }

                    // The tag says how many follow, so a mismatch would write a
                    // file whose reader stops in the wrong place — checked rather
                    // than trusted, because nothing downstream could recover.
                    if (numbers.Values.Length != expected)
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"Tag 0x{numbers.Tag:x2} carries {expected} integers and this value holds {numbers.Values.Length}."));
                    }

                    bytes.Add(numbers.Tag);
                    foreach (int number in numbers.Values)
                    {
                        CompactInteger.WriteSigned(bytes, number);
                    }

                    return null;
                }

            case BvmRaw raw:
                {
                    int width = BvmRaw.WidthFor(raw.Tag);
                    if (width == 0)
                    {
                        return Tag(raw.Tag, "raw bytes");
                    }

                    if (raw.Bytes.Length != width)
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"Tag 0x{raw.Tag:x2} carries {width} bytes and this value holds {raw.Bytes.Length}."));
                    }

                    bytes.Add(raw.Tag);
                    bytes.AddRange(raw.Bytes.Span);
                    return null;
                }

            case BvmContainer container:
                {
                    bytes.Add(BvmContainer.ContainerTag);
                    CompactInteger.Write(bytes, (uint)container.Items.Length);
                    CompactInteger.Write(bytes, (uint)container.Entries.Length);

                    foreach (BvmValue item in container.Items)
                    {
                        Refusal? refusal = AddValue(bytes, item, strings, depth + 1);
                        if (refusal is not null)
                        {
                            return refusal;
                        }
                    }

                    // In file order. Sorting by key would be the improvement that
                    // breaks the only check capable of showing this works.
                    foreach (BvmPair pair in container.Entries)
                    {
                        Refusal? key = AddValue(bytes, pair.Key, strings, depth + 1);
                        if (key is not null)
                        {
                            return key;
                        }

                        Refusal? mapped = AddValue(bytes, pair.Value, strings, depth + 1);
                        if (mapped is not null)
                        {
                            return mapped;
                        }
                    }

                    return null;
                }

            default:
                return Refusal.Unsupported(
                    $"'{value.GetType().Name}' is not a BVM value this writer knows how to spell.");
        }
    }

    private static Refusal Tag(byte tag, string kind) => Refusal.Unsupported(string.Create(
        CultureInfo.InvariantCulture,
        $"Tag 0x{tag:x2} is not one that carries {kind}."));
}
