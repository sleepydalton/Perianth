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
