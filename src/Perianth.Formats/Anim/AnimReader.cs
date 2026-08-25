using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Anim;

/// <summary>
/// Reads an ANIM container: its header, node table, hierarchy and selector
/// streams. The transform values themselves stay in the file and are decoded on
/// demand through <see cref="AnimFile"/>.
/// </summary>
public static class AnimReader
{
    private const int RotationLayoutOffset = 0x1C;

    /// <summary>
    /// Reads <paramref name="file"/>. A setup hierarchy requires a <c>PRNT</c>
    /// chunk and is validated acyclic; a clip or facial atlas may omit it.
    /// </summary>
    public static Result<AnimFile> Read(SourceFile file, bool hierarchy)
    {
        ArgumentNullException.ThrowIfNull(file);

        ReadOnlyMemory<byte> memory = file.Memory;
        ReadOnlySpan<byte> source = memory.Span;

        if (source.Length < 0x3C || !source[..4].SequenceEqual("ANIM"u8))
        {
            return Refusal.Malformed("The animation input is not a complete ANIM file.");
        }

        int nodeCount = (int)ReadU32(source, 0x24);
        float fps = BitConverter.Int32BitsToSingle((int)ReadU32(source, 0x08));
        int sampleCount = (int)ReadU32(source, 0x10);
        uint layout = ReadU32(source, RotationLayoutOffset);

        int rotationStride = layout switch
        {
            5 => 3,
            2 => 6,
            0 => 8,
            _ => 0,
        };

        if (rotationStride == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM declares packed rotation layout {layout}, which is not one of the supported 3-byte (5), 6-byte (2) or 8-byte (0) forms."));
        }

        Result<Dictionary<string, int>> markers = MarkerOffsets(source);
        if (!markers.TryGetValue(out Dictionary<string, int>? offsets, out Refusal? markerRefusal))
        {
            return markerRefusal;
        }

        if (!offsets.TryGetValue("NAME", out int nameOffset))
        {
            return Refusal.Unsupported("ANIM input lacks a NAME table.");
        }

        Result<ImmutableArray<string>> nameResult = ReadNames(source, nameOffset, nodeCount);
        if (!nameResult.TryGetValue(out ImmutableArray<string> names, out Refusal? nameRefusal))
        {
            return nameRefusal;
        }

        Dictionary<string, int> nameToIndex = new(names.Length, StringComparer.Ordinal);
        for (int i = 0; i < names.Length; i++)
        {
            nameToIndex[names[i]] = i;
        }

        Dictionary<AnimChannel, ImmutableArray<ushort>> streams = new(3);
        foreach ((AnimChannel channel, string tag) in new[]
        {
            (AnimChannel.Translation, "TRAI"),
            (AnimChannel.Rotation, "ROTI"),
            (AnimChannel.Scale, "SCAI"),
        })
        {
            if (!offsets.TryGetValue(tag, out int streamOffset))
            {
                streams[channel] = [.. Enumerable(nodeCount, (ushort)0xFFFF)];
                continue;
            }

            Result<ImmutableArray<ushort>> stream = ReadU16Stream(source, streamOffset + 4, nodeCount, tag);
            if (!stream.TryGetValue(out ImmutableArray<ushort> selectors, out Refusal? streamRefusal))
            {
                return streamRefusal;
            }

            streams[channel] = selectors;
        }

        ImmutableArray<int> parents = [];
        if (offsets.TryGetValue("PRNT", out int parentOffset))
        {
            Result<ImmutableArray<ushort>> parentStream = ReadU16Stream(source, parentOffset + 4, nodeCount, "PRNT");
            if (!parentStream.TryGetValue(out ImmutableArray<ushort> rawParents, out Refusal? parentRefusal))
            {
                return parentRefusal;
            }

            ImmutableArray<int>.Builder builder = ImmutableArray.CreateBuilder<int>(nodeCount);
            foreach (ushort parent in rawParents)
            {
                builder.Add(parent == 0xFFFF ? AnimFile.Root : parent);
            }

            parents = builder.MoveToImmutable();
        }
        else if (hierarchy)
        {
            return Refusal.Unsupported("A setup ANIM lacks a PRNT hierarchy.");
        }

        AnimFile anim = new(memory, names, nameToIndex, parents, streams, offsets, fps, sampleCount, rotationStride);

        if (hierarchy)
        {
            Result<AnimFile> validated = ValidateHierarchy(anim);
            if (!validated.IsSuccess)
            {
                return validated.Refusal;
            }
        }

        return Result.Ok(anim);
    }


    /// <summary>The three chunks that hold a channel's static values, in file order.</summary>
    private static readonly string[] BlobTags = ["DTRA", "DROT", "DSCA"];

    /// <summary>The three per-node selector streams, in file order.</summary>
    private static readonly string[] StreamTags = ["TRAI", "ROTI", "SCAI"];

    /// <summary>The three animated value chunks, in file order.</summary>
    private static readonly string[] DataTags = ["TRAD", "ROTD", "SCAD"];

    /// <summary>
    /// The three chunks that carry a <c>u32</c> count and nothing else, on every
    /// one of the 68,561 shipped animations.
    /// </summary>
    private static readonly string[] EmptyTags = ["PART", "IKEF", "IKEA"];
    /// <summary>
    /// Reads <paramref name="file"/> whole, taking every chunk's length from the
    /// header rather than searching for the chunk that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The companion to <see cref="Read"/> and a different question: that one asks
    /// what the animation poses, this asks what is in the file. It refuses where
    /// the other does not, because a chunk that is not where the header puts it is
    /// a file this does not understand, and reading past it would let a writer
    /// produce something that loads and misbehaves.
    /// </para>
    /// <para>
    /// <b>The header states every count.</b> Beyond the node count it carries,
    /// per channel, how many nodes the channel animates, how many entries its
    /// value array holds, and how many static values sit in its blob — and, at
    /// format version 14, whether a <c>PRNT</c> chunk follows. So the walk is
    /// strictly sequential: no tag is searched for, and the container has no
    /// ambiguity in it. The specification's §6 describes a bounded tag search,
    /// which is a reader strategy rather than a layout; this is the layout.
    /// </para>
    /// <para>
    /// Each header count is then <b>checked against the chunk it describes</b>,
    /// which is worth more than either reading alone: the counts and the selector
    /// streams are independent statements of the same facts, and all 68,561
    /// shipped animations agree. A file where they disagree refuses.
    /// </para>
    /// </remarks>
    public static Result<AnimDocument> ReadDocument(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        ReadOnlySpan<byte> source = file.Memory.Span;
        if (source.Length < 8 || !source[..4].SequenceEqual("ANIM"u8))
        {
            return Refusal.Malformed("The animation input is not an ANIM file.");
        }

        uint version = ReadU32(source, 4);
        int headerLength = HeaderLength(version);
        if (headerLength < 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM format version 0x{version:x8} is not one this reader knows the header length of."));
        }

        if (source.Length < headerLength)
        {
            return Refusal.Malformed("The ANIM header is truncated.");
        }

        long sampleCount = ReadU32(source, 0x10);
        long nodeCount = ReadU32(source, 0x24);
        uint layout = ReadU32(source, RotationLayoutOffset);
        int rotationStride = layout switch { 5 => 3, 2 => 6, 0 => 8, _ => 0 };

        if (rotationStride == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"ANIM declares packed rotation layout {layout}, which is not one of the supported 3-byte (5), 6-byte (2) or 8-byte (0) forms."));
        }

        // Only the node count is bounded by the file: it sizes the TYPE chunk at
        // one byte each, so a file cannot hold more nodes than it has bytes. The
        // sample count is a length of time, and five shipped animations declare
        // more samples than their compressed channels have bytes.
        if (nodeCount > source.Length)
        {
            return Refusal.Malformed("The ANIM header declares more nodes than the file could hold.");
        }

        int nodes = (int)nodeCount;
        int samples = (int)sampleCount;
        uint low = version & 0xFFFF;

        // Per channel: how many nodes it animates, how many entries its value
        // array holds, and how many static values its blob holds. Before version
        // 14 the entry counts are not written and a channel is stored flat, so
        // the count is what a flat channel takes.
        int[] animated = new int[3];
        int[] entries = new int[3];
        int[] statics = new int[3];
        bool hasParents = true;
        for (int channel = 0; channel < 3; channel++)
        {
            animated[channel] = (int)ReadU32(source, 0x28 + (channel * 4));
        }

        int staticsAt = low > 13 ? 0x41 : 0x34;
        for (int channel = 0; channel < 3; channel++)
        {
            entries[channel] = low > 13
                ? (int)ReadU32(source, 0x34 + (channel * 4))
                : animated[channel] * samples;
            statics[channel] = (int)ReadU32(source, staticsAt + (channel * 4));
        }

        if (low > 13)
        {
            hasParents = source[0x40] != 0;
        }

        for (int channel = 0; channel < 3; channel++)
        {
            if (animated[channel] < 0 || animated[channel] > source.Length ||
                entries[channel] < 0 || entries[channel] > source.Length ||
                statics[channel] < 0 || statics[channel] > source.Length)
            {
                return Refusal.Malformed("The ANIM header declares a count larger than the file could hold.");
            }
        }

        int cursor = headerLength;

        if (!Expect(source, ref cursor, "TYPE") || !Take(source, ref cursor, nodes, out ReadOnlySpan<byte> types))
        {
            return AtCursor("TYPE", cursor);
        }

        ImmutableArray<ushort> parents = [];
        if (hasParents &&
            (!Expect(source, ref cursor, "PRNT") || !TakeU16(source, ref cursor, nodes, out parents)))
        {
            return AtCursor("PRNT", cursor);
        }

        ImmutableArray<byte>[] blobs = new ImmutableArray<byte>[3];
        for (int channel = 0; channel < 3; channel++)
        {
            int stride = channel == 1 ? rotationStride : 8;
            if (!Expect(source, ref cursor, BlobTags[channel]) ||
                !Take(source, ref cursor, statics[channel] * stride, out ReadOnlySpan<byte> blob))
            {
                return AtCursor(BlobTags[channel], cursor);
            }

            blobs[channel] = [.. blob];
        }

        ImmutableArray<AnimChannelBlock>.Builder channels = ImmutableArray.CreateBuilder<AnimChannelBlock>(3);
        for (int channel = 0; channel < 3; channel++)
        {
            int stride = channel == 1 ? rotationStride : 8;
            if (!Expect(source, ref cursor, StreamTags[channel]) ||
                !TakeU16(source, ref cursor, nodes, out ImmutableArray<ushort> stream))
            {
                return AtCursor(StreamTags[channel], cursor);
            }

            Refusal? disagreement = Agrees(stream, channel, animated[channel], statics[channel]);
            if (disagreement is not null)
            {
                return disagreement;
            }

            if (!Expect(source, ref cursor, DataTags[channel]) ||
                !Take(source, ref cursor, entries[channel] * stride, out ReadOnlySpan<byte> values))
            {
                return AtCursor(DataTags[channel], cursor);
            }

            // The values end where the header says, and what sits there decides
            // how they were stored: a change table means each entry is a value the
            // channel changes to, and no change table means every animated channel
            // at every sample. Nothing else has to be inferred.
            if (!Matches(source, cursor, "CHAK"))
            {
                if (entries[channel] != animated[channel] * samples)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"ANIM {DataTags[channel]} is stored flat with {entries[channel]} entries, and {animated[channel]} channels over {samples} samples take {animated[channel] * samples}."));
                }

                channels.Add(new AnimChannelBlock(blobs[channel], stream, [.. values], false, [], []));
                continue;
            }

            int changeCount = entries[channel] - animated[channel];
            if (changeCount < 0)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"ANIM {DataTags[channel]} holds {entries[channel]} entries against {animated[channel]} animated channels, which cannot each carry a first value."));
            }

            if (!Expect(source, ref cursor, "CHAK") ||
                !TakeU16(source, ref cursor, changeCount, out ImmutableArray<ushort> changes) ||
                !Expect(source, ref cursor, "CAKS") ||
                !TakeU16(source, ref cursor, animated[channel] + 1, out ImmutableArray<ushort> offsets))
            {
                return AtCursor(DataTags[channel], cursor);
            }

            if (offsets[^1] != changeCount)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"ANIM {DataTags[channel]} offset table ends at {offsets[^1]} against {changeCount} changes."));
            }

            channels.Add(new AnimChannelBlock(blobs[channel], stream, [.. values], true, changes, offsets));
        }

        if (!Expect(source, ref cursor, "NAME"))
        {
            return AtCursor("NAME", cursor);
        }

        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(nodes);
        for (int ordinal = 0; ordinal < nodes; ordinal++)
        {
            int end = cursor >= source.Length ? -1 : source[cursor..].IndexOf((byte)0);
            if (end < 0)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"ANIM name {ordinal} is truncated."));
            }

            names.Add(Encoding.Latin1.GetString(source.Slice(cursor, end)));
            cursor += end + 1;
        }

        foreach (string tag in EmptyTags)
        {
            if (!Expect(source, ref cursor, tag) || !TakeU32(source, ref cursor, out uint count))
            {
                return AtCursor(tag, cursor);
            }

            if (count != 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"ANIM {tag} declares {count} entries, and every shipped animation declares none, so an entry's shape is unknown."));
            }
        }

        if (!TakeU32(source, ref cursor, out uint pathLength) ||
            pathLength > source.Length - cursor ||
            !Take(source, ref cursor, (int)pathLength, out ReadOnlySpan<byte> path))
        {
            return Refusal.Malformed("The ANIM source path is truncated.");
        }

        string sourcePath = path.Length > 0 && path[^1] == 0
            ? Encoding.Latin1.GetString(path[..^1])
            : Encoding.Latin1.GetString(path);

        // The tail grows with the version word's high half rather than its low
        // one: 0 ends at the source path, 1 adds a flag byte and an array of u32,
        // and 3 adds the node count restated and a per-node bit array.
        ImmutableArray<uint> tail = [];
        ImmutableArray<byte> nodeBits = [];
        uint high = version >> 16;
        if (high >= 1)
        {
            if (!Take(source, ref cursor, 1, out ReadOnlySpan<byte> flag) || flag[0] != 0)
            {
                return Refusal.Malformed("The ANIM tail flag byte is set, which no shipped animation does.");
            }

            if (!TakeU32(source, ref cursor, out uint count) ||
                count > (source.Length - cursor) / 4 ||
                !TakeU32Array(source, ref cursor, (int)count, out tail))
            {
                return Refusal.Malformed("The ANIM tail array is truncated.");
            }
        }

        if (high >= 3)
        {
            if (!TakeU32(source, ref cursor, out uint restated) || restated != nodeCount)
            {
                return Refusal.Malformed("The ANIM tail restates a different node count.");
            }

            if (!TakeU32(source, ref cursor, out uint bytes) || bytes != (uint)((nodes + 7) / 8))
            {
                return Refusal.Malformed("The ANIM tail bit array is not one bit per node.");
            }

            if (!Take(source, ref cursor, (int)bytes, out ReadOnlySpan<byte> bits))
            {
                return Refusal.Malformed("The ANIM tail bit array is truncated.");
            }

            nodeBits = [.. bits];
        }

        if (cursor != source.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM chunks account for {cursor} of its {source.Length} bytes."));
        }

        return Result.Ok(new AnimDocument(
            [.. source[..headerLength]],
            [.. types],
            parents,
            channels.MoveToImmutable(),
            names.MoveToImmutable(),
            sourcePath,
            tail,
            nodeBits));
    }

    /// <summary>
    /// Whether a selector stream states the counts the header does.
    /// </summary>
    /// <remarks>
    /// The header's counts and the stream are independent statements of the same
    /// two facts, so checking one against the other is worth more than trusting
    /// either. All 68,561 shipped animations agree; a file that does not is one
    /// whose chunks are not the lengths it claims.
    /// </remarks>
    private static Refusal? Agrees(ImmutableArray<ushort> stream, int channel, int animated, int statics)
    {
        int countedAnimated = 0;
        int countedStatics = 0;
        foreach (ushort selector in stream)
        {
            if (selector < 0x8000)
            {
                countedAnimated++;
            }
            else if (selector < 0xFFFE)
            {
                countedStatics++;
            }
        }

        if (countedAnimated != animated)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header says {DataTags[channel]} animates {animated} nodes and {StreamTags[channel]} selects {countedAnimated}."));
        }

        if (countedStatics != statics)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The ANIM header says {BlobTags[channel]} holds {statics} values and {StreamTags[channel]} selects {countedStatics}."));
        }

        return null;
    }

    /// <summary>
    /// How long the fixed header is, by the <em>low</em> half of the version word.
    /// </summary>
    /// <remarks>
    /// The high half varies across the corpus — 0, 1 and 3 all ship — and moves
    /// only the tail, never the header. Version 14's thirteen extra bytes are the
    /// three value-entry counts and the flag saying a <c>PRNT</c> chunk follows;
    /// before 14 a channel is always flat and a hierarchy is always present, which
    /// is what makes those thirteen bytes unnecessary there.
    /// </remarks>
    internal static int HeaderLength(uint version) => (version & 0xFFFF) switch
    {
        14 => 0x51,
        13 or 12 => 0x44,
        _ => -1,
    };

    private static bool Matches(ReadOnlySpan<byte> source, int at, string tag) =>
        at >= 0 && at <= source.Length - 4 &&
        source[at] == tag[0] && source[at + 1] == tag[1] &&
        source[at + 2] == tag[2] && source[at + 3] == tag[3];

    private static bool Expect(ReadOnlySpan<byte> source, ref int cursor, string tag)
    {
        if (!Matches(source, cursor, tag))
        {
            return false;
        }

        cursor += 4;
        return true;
    }

    private static bool Take(ReadOnlySpan<byte> source, ref int cursor, int count, out ReadOnlySpan<byte> bytes)
    {
        if (count < 0 || count > source.Length - cursor)
        {
            bytes = default;
            return false;
        }

        bytes = source.Slice(cursor, count);
        cursor += count;
        return true;
    }

    private static bool TakeU16(ReadOnlySpan<byte> source, ref int cursor, int count, out ImmutableArray<ushort> values)
    {
        if (!Take(source, ref cursor, count * 2, out ReadOnlySpan<byte> bytes))
        {
            values = [];
            return false;
        }

        ImmutableArray<ushort>.Builder builder = ImmutableArray.CreateBuilder<ushort>(count);
        for (int i = 0; i < count; i++)
        {
            builder.Add((ushort)(bytes[i * 2] | (bytes[(i * 2) + 1] << 8)));
        }

        values = builder.MoveToImmutable();
        return true;
    }

    private static bool TakeU32(ReadOnlySpan<byte> source, ref int cursor, out uint value)
    {
        if (!Take(source, ref cursor, 4, out ReadOnlySpan<byte> bytes))
        {
            value = 0;
            return false;
        }

        value = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        return true;
    }

    private static bool TakeU32Array(ReadOnlySpan<byte> source, ref int cursor, int count, out ImmutableArray<uint> values)
    {
        ImmutableArray<uint>.Builder builder = ImmutableArray.CreateBuilder<uint>(count);
        for (int i = 0; i < count; i++)
        {
            if (!TakeU32(source, ref cursor, out uint value))
            {
                values = [];
                return false;
            }

            builder.Add(value);
        }

        values = builder.MoveToImmutable();
        return true;
    }

    private static Refusal AtCursor(string tag, int cursor) => Refusal.Malformed(string.Create(
        CultureInfo.InvariantCulture, $"The ANIM {tag} chunk is not at byte {cursor}, where the chunks before it end."));

    /// <summary>
    /// Locates each chunk by a bounded sequential search, load-bearing exactly as
    /// the specification insists.
    /// </summary>
    /// <remarks>
    /// Each transform tag is searched from four bytes past the previous match, so
    /// a chunk cannot begin inside another's tag. Without that bound a payload
    /// byte manufactures a straddling match — <c>DROT</c> followed by an <c>I</c>
    /// reads as <c>ROTI</c> one byte later — which mis-locates the chunk and makes
    /// a canonical file look out of order. There is deliberately no ordering
    /// check: once bounded, the offsets ascend by construction.
    /// </remarks>
    private static Result<Dictionary<string, int>> MarkerOffsets(ReadOnlySpan<byte> source)
    {
        Dictionary<string, int> offsets = new(StringComparer.Ordinal);
        int cursor = 0x3C;
        foreach (string tag in AnimFile.OrderedTags)
        {
            int offset = FindFrom(source, tag, cursor);
            if (offset < 0)
            {
                if (tag is "SCAI" or "NAME")
                {
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture, $"ANIM input lacks a {tag} chunk."));
                }

                continue;
            }

            offsets[tag] = offset;
            cursor = offset + 4;
        }

        // PRNT and PART sit outside the transform sequence, so they are located by
        // an unbounded search from the start.
        foreach (string tag in new[] { "PRNT", "PART" })
        {
            int offset = FindFrom(source, tag, 0);
            if (offset >= 0)
            {
                offsets[tag] = offset;
            }
        }

        return Result.Ok(offsets);
    }

    private static Result<ImmutableArray<string>> ReadNames(ReadOnlySpan<byte> source, int nameOffset, int nodeCount)
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>(nodeCount);
        HashSet<string> seen = new(nodeCount, StringComparer.Ordinal);
        int cursor = nameOffset + 4;

        for (int ordinal = 0; ordinal < nodeCount; ordinal++)
        {
            int end = cursor > source.Length ? -1 : source[cursor..].IndexOf((byte)0);
            if (end < 0)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"ANIM name {ordinal} is truncated."));
            }

            end += cursor;
            if (end == cursor)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"ANIM name {ordinal} is empty."));
            }

            string name = Encoding.Latin1.GetString(source[cursor..end]);
            if (!seen.Add(name))
            {
                return Refusal.Malformed("The ANIM NAME table contains duplicate nodes.");
            }

            names.Add(name);
            cursor = end + 1;
        }

        return Result.Ok(names.MoveToImmutable());
    }

    private static Result<ImmutableArray<ushort>> ReadU16Stream(ReadOnlySpan<byte> source, int start, int nodeCount, string tag)
    {
        long byteCount = (long)nodeCount * 2;
        if (start > source.Length || byteCount > source.Length - start)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture, $"ANIM {tag} selector stream is truncated."));
        }

        ImmutableArray<ushort>.Builder builder = ImmutableArray.CreateBuilder<ushort>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            builder.Add((ushort)(source[start + (i * 2)] | (source[start + (i * 2) + 1] << 8)));
        }

        return Result.Ok(builder.MoveToImmutable());
    }

    private static Result<AnimFile> ValidateHierarchy(AnimFile anim)
    {
        for (int index = 0; index < anim.Parents.Length; index++)
        {
            int parent = anim.Parents[index];
            if (parent != AnimFile.Root && (parent < 0 || parent >= anim.NodeCount))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"ANIM node {index} has an out-of-range parent."));
            }
        }

        // 0 unvisited, 1 on the current path, 2 settled. A node reached while on
        // the path closes a cycle.
        byte[] state = new byte[anim.NodeCount];
        for (int index = 0; index < anim.NodeCount; index++)
        {
            int current = index;
            // Walk to the root, marking the path; a repeat of an on-path node is a
            // cycle. Iterative to stay bounded on deep hierarchies.
            List<int> path = [];
            while (current != AnimFile.Root && state[current] == 0)
            {
                state[current] = 1;
                path.Add(current);
                current = anim.Parents[current];
            }

            if (current != AnimFile.Root && state[current] == 1)
            {
                return Refusal.Malformed("The ANIM PRNT hierarchy contains a cycle.");
            }

            foreach (int node in path)
            {
                state[node] = 2;
            }
        }

        return Result.Ok(anim);
    }

    private static uint ReadU32(ReadOnlySpan<byte> source, int offset) =>
        (uint)(source[offset] | (source[offset + 1] << 8) | (source[offset + 2] << 16) | (source[offset + 3] << 24));

    private static int FindFrom(ReadOnlySpan<byte> source, string tag, int start)
    {
        if (start < 0)
        {
            start = 0;
        }

        if (start > source.Length)
        {
            return -1;
        }

        int relative = source[start..].IndexOf(Encoding.ASCII.GetBytes(tag));
        return relative < 0 ? -1 : start + relative;
    }

    private static IEnumerable<ushort> Enumerable(int count, ushort value)
    {
        for (int i = 0; i < count; i++)
        {
            yield return value;
        }
    }
}
