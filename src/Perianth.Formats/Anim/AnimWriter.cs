using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Anim;

/// <summary>
/// Writes an <see cref="AnimDocument"/> back to bytes.
/// </summary>
/// <remarks>
/// <para>
/// The fifth writer, and the one that reaches the thing every part of a model is
/// placed by: the node hierarchy. It is the same bar as the other four —
/// <b>read a real file, write it back, require the bytes to match</b> — over all
/// 68,561 shipped animations, which is the largest population any of them faces.
/// </para>
/// <para>
/// So this writer has no opinions. It does not renumber a selector, drop a
/// static value nothing selects, recompress a flat channel, rebuild a change
/// table, or normalise the authoring path the exporter stamped in. The header is
/// written back verbatim, thirteen unread bytes and all. Every one of those
/// would be an improvement that breaks the only test capable of showing the
/// writer works.
/// </para>
/// <para>
/// Two things here are derived rather than stored, and both are derived
/// <em>beside the thing they come from</em>, which is the lesson of four stale
/// fields found in one day on the MMB. A chunk's length comes from the array it
/// precedes, and the tail's restated node count comes from the name table. A
/// document that disagrees with itself — a selector stream of the wrong length,
/// an offset table that does not match its channel count — refuses rather than
/// being quietly corrected.
/// </para>
/// </remarks>
public static class AnimWriter
{
    /// <summary>Serializes a document, or refuses if it cannot be spelled.</summary>
    /// <remarks>
    /// Every refusal here is a document the grammar cannot express, and none may
    /// arise from a document this project's reader produced.
    /// </remarks>
    public static Result<byte[]> Write(AnimDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Header.Length < 0x28)
        {
            return Refusal.Unsupported("An ANIM header must be long enough to declare its own counts.");
        }

        uint version = U32(document.Header, 4);
        int headerLength = AnimReader.HeaderLength(version);
        if (headerLength != document.Header.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header is {document.Header.Length} bytes, and format version 0x{version:x8} takes {headerLength}."));
        }

        int nodes = document.NodeCount;
        if (U32(document.Header, 0x24) != (uint)nodes)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header declares {U32(document.Header, 0x24)} nodes against a name table of {nodes}."));
        }

        if (document.Types.Length != nodes)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM TYPE chunk holds {document.Types.Length} bytes against {nodes} nodes."));
        }

        if (!document.Parents.IsEmpty && document.Parents.Length != nodes)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM PRNT chunk holds {document.Parents.Length} entries against {nodes} nodes."));
        }

        if (document.Channels.Length != 3)
        {
            return Refusal.Unsupported("An ANIM holds exactly three channels: translation, rotation and scale.");
        }

        foreach (string name in document.Names)
        {
            if (name.Contains('\0', StringComparison.Ordinal))
            {
                return Refusal.Unsupported("An ANIM node name cannot contain a NUL, which is what ends one in the file.");
            }
        }

        List<byte> bytes = new(4096);
        bytes.AddRange(document.Header);

        Tag(bytes, "TYPE");
        bytes.AddRange(document.Types);

        if (!document.Parents.IsEmpty)
        {
            Tag(bytes, "PRNT");
            AddU16(bytes, document.Parents);
        }

        uint low = version & 0xFFFF;
        bool declaresParents = low <= 13 || document.Header[0x40] != 0;
        if (declaresParents != !document.Parents.IsEmpty)
        {
            return Refusal.Unsupported(declaresParents
                ? "The ANIM header says a PRNT chunk follows and the document has no hierarchy."
                : "The ANIM header says no PRNT chunk follows and the document has one.");
        }

        int samples = (int)U32(document.Header, 0x10);
        int staticsAt = low > 13 ? 0x41 : 0x34;
        for (int channel = 0; channel < 3; channel++)
        {
            Refusal? refusal = Check(
                document.Channels[channel],
                channel,
                nodes,
                samples,
                (int)U32(document.Header, 0x28 + (channel * 4)),
                low > 13 ? (int)U32(document.Header, 0x34 + (channel * 4)) : -1,
                (int)U32(document.Header, staticsAt + (channel * 4)));
            if (refusal is not null)
            {
                return refusal;
            }

            Tag(bytes, StaticTags[channel]);
            bytes.AddRange(document.Channels[channel].Statics);
        }

        for (int channel = 0; channel < 3; channel++)
        {
            AnimChannelBlock block = document.Channels[channel];
            Tag(bytes, SelectorTags[channel]);
            AddU16(bytes, block.Selectors);
            Tag(bytes, ValueTags[channel]);
            bytes.AddRange(block.Values);

            if (!block.Compressed)
            {
                continue;
            }

            Tag(bytes, "CHAK");
            AddU16(bytes, block.Changes);
            Tag(bytes, "CAKS");
            AddU16(bytes, block.Offsets);
        }

        Tag(bytes, "NAME");
        foreach (string name in document.Names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        foreach (string tag in EmptyTags)
        {
            Tag(bytes, tag);
            AddU32(bytes, 0);
        }

        byte[] path = Encoding.Latin1.GetBytes(document.SourcePath);
        AddU32(bytes, (uint)(path.Length + 1));
        bytes.AddRange(path);
        bytes.Add(0);

        uint high = version >> 16;
        if (high >= 1)
        {
            bytes.Add(0);
            AddU32(bytes, (uint)document.TailArray.Length);
            foreach (uint entry in document.TailArray)
            {
                AddU32(bytes, entry);
            }
        }
        else if (!document.TailArray.IsEmpty || !document.NodeBits.IsEmpty)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM format version 0x{version:x8} ends at its source path and has nowhere to write a tail."));
        }

        if (high >= 3)
        {
            if (document.NodeBits.Length != (nodes + 7) / 8)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ANIM tail bit array holds {document.NodeBits.Length} bytes against {nodes} nodes."));
            }

            AddU32(bytes, (uint)nodes);
            AddU32(bytes, (uint)document.NodeBits.Length);
            bytes.AddRange(document.NodeBits);
        }
        else if (!document.NodeBits.IsEmpty)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM format version 0x{version:x8} has no per-node bit array."));
        }

        return Result.Ok(bytes.ToArray());
    }

    private static readonly string[] StaticTags = ["DTRA", "DROT", "DSCA"];
    private static readonly string[] SelectorTags = ["TRAI", "ROTI", "SCAI"];
    private static readonly string[] ValueTags = ["TRAD", "ROTD", "SCAD"];
    private static readonly string[] EmptyTags = ["PART", "IKEF", "IKEA"];

    /// <summary>
    /// Whether a channel agrees with the header that describes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The header carries, per channel, how many nodes it animates, how many
    /// entries its value array holds and how many static values its blob holds.
    /// Those are the lengths a reader uses, so a document whose channels have
    /// moved away from them is one that cannot be written — <b>the header is not
    /// quietly recomputed here</b>. A count restated in two places is exactly
    /// what went stale four times in one day on the MMB, and the answer there was
    /// the same: refuse rather than guess which of the two is right.
    /// </para>
    /// <para>
    /// The counts are all checked without a stride, so the writer never has to
    /// decode the rotation layout: how many nodes a stream selects, and how many
    /// changes a table holds, are both stride-free facts.
    /// </para>
    /// </remarks>
    private static Refusal? Check(
        AnimChannelBlock block,
        int channel,
        int nodes,
        int samples,
        int animatedDeclared,
        int entriesDeclared,
        int staticsDeclared)
    {
        if (block.Selectors.Length != nodes)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM {SelectorTags[channel]} stream holds {block.Selectors.Length} selectors against {nodes} nodes."));
        }

        int animated = 0;
        int statics = 0;
        foreach (ushort selector in block.Selectors)
        {
            if (selector < 0x8000)
            {
                animated++;
            }
            else if (selector < 0xFFFE)
            {
                statics++;
            }
        }

        if (animated != animatedDeclared)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header says {ValueTags[channel]} animates {animatedDeclared} nodes and {SelectorTags[channel]} selects {animated}."));
        }

        if (statics != staticsDeclared)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header says {StaticTags[channel]} holds {staticsDeclared} values and {SelectorTags[channel]} selects {statics}."));
        }

        int entries = block.Compressed ? animated + block.Changes.Length : animated * samples;
        if (entriesDeclared >= 0 && entries != entriesDeclared)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header says {ValueTags[channel]} holds {entriesDeclared} entries and the channel holds {entries}."));
        }

        if (!block.Compressed)
        {
            return block.Changes.IsEmpty && block.Offsets.IsEmpty
                ? null
                : Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The ANIM {ValueTags[channel]} channel is flat and has no change table to write."));
        }

        if (block.Offsets.Length != animated + 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM {ValueTags[channel]} offset table holds {block.Offsets.Length} entries against {animated} animated channels."));
        }

        if (block.Offsets[^1] != block.Changes.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM {ValueTags[channel]} offset table ends at {block.Offsets[^1]} against {block.Changes.Length} changes."));
        }

        return null;
    }

    private static uint U32(ImmutableArray<byte> bytes, int at) =>
        (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24));

    private static void Tag(List<byte> bytes, string tag)
    {
        foreach (char letter in tag)
        {
            bytes.Add((byte)letter);
        }
    }

    private static void AddU16(List<byte> bytes, ImmutableArray<ushort> values)
    {
        foreach (ushort value in values)
        {
            bytes.Add((byte)value);
            bytes.Add((byte)(value >> 8));
        }
    }

    private static void AddU32(List<byte> bytes, uint value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }
}
