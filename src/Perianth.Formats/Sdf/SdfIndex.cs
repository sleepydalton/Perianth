using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Sdf;

/// <summary>
/// Guided descent through the compact filename index.
/// </summary>
/// <remarks>
/// <para>
/// The index is a path tree of three node classes, told apart by the leading
/// byte alone. The ranges partition <c>0x00..0xFF</c>, so every byte is a
/// valid node class and there is no "unknown node" case:
/// </para>
/// <list type="bullet">
/// <item><c>0x01..0x1F</c> — a literal segment, the byte being its length.</item>
/// <item><c>0x41..0x5A</c> — a terminal, the byte also encoding the chunk count.</item>
/// <item>anything else, <c>0x00</c> included — a branch.</item>
/// </list>
/// <para>
/// A branch carries one key byte and a 32-bit absolute offset for its second
/// child. The child taken is chosen by <em>ordering</em> the query character
/// against the key, not by equality: lower takes the inline child at
/// <c>node + 5</c>, equal or higher takes the alternate. This matters because
/// an equality reading also explains a shallow first entry and only fails on
/// deeper paths, so a shallow test cannot tell the two apart.
/// </para>
/// <para>
/// Lookup is guided only. It follows one path from the root and never
/// enumerates unrelated branches, so a query costs the length of its own path
/// rather than the size of the index.
/// </para>
/// </remarks>
public static class SdfIndex
{
    private const int LiteralMax = 0x1F;
    private const byte TerminalFirst = (byte)'A';
    private const byte TerminalLast = (byte)'Z';
    private const int BranchBytes = 5;

    private const int TerminalChunkCountMask = 0x7;
    private const int TerminalPathPatchBit = 0x8;

    private const int HeaderResidentWidthMask = 0x3;
    private const int HeaderReadAheadShift = 2;

    private const int ControlSizeWidthMask = 0x3;
    private const int ControlOffsetWidthShift = 2;
    private const int ControlOffsetWidthMask = 0x7;
    private const int ControlStoredInfoBit = 0x20;

    // The runtime's lookup result carries five inline chunk descriptors, so a
    // terminal encoding more than five could not be represented by the engine
    // the container came from.
    private const int MaxChunks = 5;

    // The runtime stores a 32-bit archive offset, so a wider encoded offset
    // has no representable destination. Widths above four are refused rather
    // than given invented meaning; where they have appeared in practice they
    // meant a cursor had drifted.
    private const int MaxOffsetWidth = 4;

    private const int PageBytes = 0x10000;
    private const int ChunkMetadataBytes = 4;

    /// <summary>
    /// Canonicalizes one virtual path for lookup.
    /// </summary>
    /// <remarks>
    /// Separators are unified and case is folded, because the index compares
    /// path bytes case-insensitively and serialized paths arrive with either
    /// separator. This is source-format behaviour: it is not permission to
    /// case-fold a loose filesystem path, which the specification keeps
    /// case-sensitive.
    /// </remarks>
    public static string NormalizePath(string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);

        // A path already in the form this returns is returned as it came, with
        // nothing allocated. Every path in the shipped container is lowercase
        // with forward separators, so callers that normalize the whole index —
        // searching, selecting, resolving — otherwise pay half a million
        // copies to produce half a million identical strings.
        if (!NeedsNormalizing(virtualPath))
        {
            return virtualPath;
        }

        return string.Create(virtualPath.Length, virtualPath, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i] == '\\' ? '/' : source[i];
                span[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }
        });
    }

    /// <summary>Whether any character in the path would be rewritten.</summary>
    private static bool NeedsNormalizing(string virtualPath)
    {
        foreach (char c in virtualPath)
        {
            if (c == '\\' || c is >= 'A' and <= 'Z')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the entry for one complete virtual path.
    /// </summary>
    /// <returns>
    /// A successful result carrying the entry, or carrying <see langword="null"/>
    /// when the path is simply not in the tree. Absence is an ordinary
    /// mismatch; a refusal means the index itself could not be decoded.
    /// </returns>
    public static Result<SdfEntry?> Lookup(
        ReadOnlySpan<byte> table,
        string virtualPath,
        SdfIndexLayout layout)
    {
        string query = NormalizePath(virtualPath);
        if (query.Length == 0)
        {
            return Result.Ok<SdfEntry?>(null);
        }

        int offset = 0;
        int matched = 0;

        // Corruption detection only: a well-formed tree never revisits a node
        // on one descent, so a repeat means the data is cyclic.
        HashSet<int> visited = [];

        while (true)
        {
            if (!visited.Add(offset))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index revisits node 0x{offset:X} on one descent, so the tree is cyclic."));
            }

            if (offset < 0 || offset >= table.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index node at 0x{offset:X} is outside the {table.Length}-byte table."));
            }

            byte leading = table[offset];

            if (leading is >= 1 and <= LiteralMax)
            {
                if (table.Length - offset - 1 < leading)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The filename index literal segment at 0x{offset:X} is truncated."));
                }

                if (query.Length - matched < leading ||
                    !SegmentMatches(table.Slice(offset + 1, leading), query, matched))
                {
                    return Result.Ok<SdfEntry?>(null);
                }

                matched += leading;
                offset += 1 + leading;
                continue;
            }

            if (leading is >= TerminalFirst and <= TerminalLast)
            {
                // A match must consume the whole query; a prefix is not a match.
                return matched != query.Length
                    ? Result.Ok<SdfEntry?>(null)
                    : ReadTerminal(table, offset, query, layout);
            }

            if (table.Length - offset - 1 < 4)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index branch at 0x{offset:X} is truncated."));
            }

            uint alternate = BinaryPrimitives.ReadUInt32LittleEndian(table[(offset + 1)..]);
            if (alternate >= (uint)table.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index branch at 0x{offset:X} targets 0x{alternate:X}, outside the {table.Length}-byte table."));
            }

            char key = leading is >= 0x41 and <= 0x5A ? (char)(leading + 32) : (char)leading;

            // An exhausted query compares as its terminator, not as no answer.
            // The runtime compares NUL-terminated strings, and 0x00 is a valid
            // key byte the format's node classes account for, so the end of the
            // query orders below every ordinary key and takes the inline child.
            //
            // Treating it as absent instead loses every path that is a proper
            // prefix of another: `barks.locpack` sits behind exactly this branch
            // because `barks.locpackbin` shares its spelling. The container
            // ships 211 such files, and a full walk of the index finds each one
            // at the terminal this rule reaches.
            char probe = matched < query.Length ? query[matched] : '\0';
            offset = probe < key ? offset + BranchBytes : (int)alternate;
        }
    }

    /// <summary>
    /// Walks the whole index and returns every path it spells.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Lookup"/> descends to one path; this visits all of them, which
    /// is what listing and searching need and what the container otherwise
    /// offers no way to ask. It is the same grammar read in the same direction:
    /// a literal appends its bytes, a branch appends nothing and has both
    /// children explored, and a terminal names the path built so far.
    /// </para>
    /// <para>
    /// Terminal bodies are not decoded. The tag alone says whether a terminal
    /// encodes chunks, and the offset is returned so a caller wanting the bytes
    /// can decode exactly the terminals it cares about with
    /// <see cref="ReadEntryAt"/> rather than paying for all of them.
    /// </para>
    /// <para>
    /// Order is the tree's own: the inline child holds the keys ordering before
    /// the branch's, and it is walked first. Nothing here sorts, and callers
    /// must not read the order as a guarantee the format has not been shown to
    /// make.
    /// </para>
    /// </remarks>
    /// <returns>
    /// Every path in the index, or a refusal if the tree could not be walked.
    /// An empty table yields no paths, which is not an error.
    /// </returns>
    public static Result<ImmutableArray<SdfPathEntry>> Enumerate(ReadOnlySpan<byte> table)
    {
        ImmutableArray<SdfPathEntry>.Builder found = ImmutableArray.CreateBuilder<SdfPathEntry>();
        if (table.Length == 0)
        {
            return Result.Ok(found.ToImmutable());
        }

        // Alternate children still to walk, each with the path length in force
        // where it was found. A branch appends nothing, so its two children
        // share a prefix, and the deeper walk only ever writes past it.
        Stack<(int Offset, int PathLength)> pending = new();
        char[] path = new char[256];
        int length = 0;

        // Every node begins at a distinct byte in a tree, so a walk that visits
        // more nodes than the table has bytes is not walking a tree. This is the
        // whole-walk counterpart to Lookup's per-descent cycle check, which
        // cannot be reused here: a shared subtree is legitimately reached twice
        // by two different paths and must be emitted under both.
        int budget = table.Length;
        int offset = 0;

        while (true)
        {
            if (--budget < 0)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index walk visited more than {table.Length} nodes, so the tree is cyclic."));
            }

            if (offset < 0 || offset >= table.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index node at 0x{offset:X} is outside the {table.Length}-byte table."));
            }

            byte leading = table[offset];

            if (leading is >= 1 and <= LiteralMax)
            {
                if (table.Length - offset - 1 < leading)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The filename index literal segment at 0x{offset:X} is truncated."));
                }

                if (length + leading > path.Length)
                {
                    System.Array.Resize(ref path, System.Math.Max(path.Length * 2, length + leading));
                }

                for (int i = 0; i < leading; i++)
                {
                    path[length + i] = (char)table[offset + 1 + i];
                }

                length += leading;
                offset += 1 + leading;
                continue;
            }

            if (leading is >= TerminalFirst and <= TerminalLast)
            {
                int value = leading - TerminalFirst;

                if ((value & TerminalPathPatchBit) != 0)
                {
                    // The same reason Lookup refuses one: the terminal names a
                    // path derived from the one spelled here, so listing the
                    // spelled path would report a path the index does not hold.
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The filename index terminal at 0x{offset:X} carries a path-substitution table, which this reader does not interpret."));
                }

                found.Add(new SdfPathEntry(
                    new string(path, 0, length),
                    offset,
                    IsDirectory: (value & TerminalChunkCountMask) == 0));

                if (pending.Count == 0)
                {
                    return Result.Ok(found.ToImmutable());
                }

                (offset, length) = pending.Pop();
                continue;
            }

            if (table.Length - offset - 1 < 4)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index branch at 0x{offset:X} is truncated."));
            }

            uint alternate = BinaryPrimitives.ReadUInt32LittleEndian(table[(offset + 1)..]);
            if (alternate >= (uint)table.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index branch at 0x{offset:X} targets 0x{alternate:X}, outside the {table.Length}-byte table."));
            }

            pending.Push(((int)alternate, length));
            offset += BranchBytes;
        }
    }

    /// <summary>
    /// Decodes the terminal at a known offset, for a path already spelled.
    /// </summary>
    /// <remarks>
    /// The second half of <see cref="Enumerate"/>: the walk found the path and
    /// where its terminal sits, and this reads where the bytes live. The path is
    /// passed in rather than rediscovered because only the walk that reached the
    /// terminal knows it — nothing in the terminal itself spells it.
    /// </remarks>
    public static Result<SdfEntry> ReadEntryAt(
        ReadOnlySpan<byte> table,
        int nodeOffset,
        string path,
        SdfIndexLayout layout)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (nodeOffset < 0 || nodeOffset >= table.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index node at 0x{nodeOffset:X} is outside the {table.Length}-byte table."));
        }

        if (table[nodeOffset] is < TerminalFirst or > TerminalLast)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index node at 0x{nodeOffset:X} is not a terminal."));
        }

        Result<SdfEntry?> entry = ReadTerminal(table, nodeOffset, path, layout);
        if (!entry.TryGetValue(out SdfEntry? decoded, out Refusal? refusal))
        {
            return refusal;
        }

        // ReadTerminal's null means "the descent found no match", which cannot
        // arise here: the caller already holds the terminal.
        return decoded is null
            ? Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index terminal at 0x{nodeOffset:X} decoded to no entry."))
            : Result.Ok(decoded);
    }

    private static bool SegmentMatches(ReadOnlySpan<byte> segment, string query, int matched)
    {
        for (int i = 0; i < segment.Length; i++)
        {
            byte b = segment[i];
            char c = b is >= 0x41 and <= 0x5A ? (char)(b + 32) : (char)b;
            if (c != query[matched + i])
            {
                return false;
            }
        }

        return true;
    }

    private static Result<SdfEntry?> ReadTerminal(
        ReadOnlySpan<byte> table,
        int offset,
        string path,
        SdfIndexLayout layout)
    {
        byte tag = table[offset];
        int value = tag - TerminalFirst;
        int chunkCount = value & TerminalChunkCountMask;

        if ((value & TerminalPathPatchBit) != 0)
        {
            // A patched terminal names a path derived from, but not equal to,
            // the one this descent matched, so the match that reached it
            // cannot be trusted. Refusing beats returning an entry for a path
            // the index does not actually spell here.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index terminal at 0x{offset:X} carries a path-substitution table, which this reader does not interpret."));
        }

        if (chunkCount > MaxChunks)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index terminal at 0x{offset:X} encodes {chunkCount} chunks, more than the {MaxChunks} the runtime can hold."));
        }

        if (table.Length - offset < 6)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index terminal at 0x{offset:X} is truncated."));
        }

        uint fileMetadata = BinaryPrimitives.ReadUInt32LittleEndian(table[(offset + 1)..]);
        byte header = table[offset + 5];
        int residentWidth = header & HeaderResidentWidthMask;
        int readAheadBlocks = header >> HeaderReadAheadShift;

        int cursor = offset + 6;
        int? residentIndex = null;

        if (residentWidth != 0)
        {
            if (!TryUInt(table, ref cursor, residentWidth, out long index))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The filename index terminal at 0x{offset:X} has a truncated resident index."));
            }

            residentIndex = (int)index;
        }

        ImmutableArray<SdfChunk>.Builder chunks = ImmutableArray.CreateBuilder<SdfChunk>(chunkCount);
        long total = 0;

        for (int i = 0; i < chunkCount; i++)
        {
            Result<SdfChunk> chunk = ReadChunk(table, ref cursor, i, offset, total, layout);
            if (!chunk.TryGetValue(out SdfChunk decoded, out Refusal? refusal))
            {
                return refusal;
            }

            chunks.Add(decoded);
            total += decoded.DecodedSize;
        }

        return Result.Ok<SdfEntry?>(new SdfEntry(
            path,
            total,
            chunks.ToImmutable(),
            residentIndex,
            readAheadBlocks,
            fileMetadata,
            tag));
    }

    private static Result<SdfChunk> ReadChunk(
        ReadOnlySpan<byte> table,
        ref int cursor,
        int index,
        int terminalOffset,
        long logicalStart,
        SdfIndexLayout layout)
    {
        if (cursor < 0 || cursor >= table.Length)
        {
            return Truncated(terminalOffset, index, "control byte");
        }

        byte control = table[cursor++];
        int sizeWidth = (control & ControlSizeWidthMask) + 1;
        int offsetWidth = (control >> ControlOffsetWidthShift) & ControlOffsetWidthMask;
        bool hasStoredInfo = (control & ControlStoredInfoBit) != 0;

        if (offsetWidth > MaxOffsetWidth)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The filename index terminal at 0x{terminalOffset:X} chunk {index} encodes offset width {offsetWidth} (control 0x{control:X2}), which has no representable destination."));
        }

        if (!TryUInt(table, ref cursor, sizeWidth, out long decodedSize))
        {
            return Truncated(terminalOffset, index, "decoded size");
        }

        long storedSize = 0;
        if (hasStoredInfo && !TryUInt(table, ref cursor, sizeWidth, out storedSize))
        {
            return Truncated(terminalOffset, index, "stored size");
        }

        if (!TryUInt(table, ref cursor, offsetWidth, out long archiveOffset))
        {
            return Truncated(terminalOffset, index, "archive offset");
        }

        if (!TryUInt(table, ref cursor, 2, out long archiveId))
        {
            return Truncated(terminalOffset, index, "archive id");
        }

        ImmutableArray<int> pageSizes = [];
        if (hasStoredInfo)
        {
            long pages = (decodedSize + PageBytes - 1) / PageBytes;
            if (pages > 1)
            {
                ImmutableArray<int>.Builder builder = ImmutableArray.CreateBuilder<int>((int)pages);
                for (long page = 0; page < pages; page++)
                {
                    if (!TryUInt(table, ref cursor, 2, out long size))
                    {
                        return Truncated(terminalOffset, index, "page size");
                    }

                    builder.Add((int)size);
                }

                pageSizes = builder.MoveToImmutable();
            }
        }

        uint metadata = 0;
        if (layout.HasChunkMetadata)
        {
            // Consumed because the container says it is there. Its meaning is
            // unknown and deliberately not interpreted. Skipping it decodes the
            // first chunk of every terminal correctly and drifts every chunk
            // after it, which is why single-chunk files look fine while the
            // reader is wrong.
            if (!TryUInt(table, ref cursor, ChunkMetadataBytes, out long value))
            {
                return Truncated(terminalOffset, index, "trailing metadata");
            }

            metadata = (uint)value;
        }

        return Result.Ok(new SdfChunk(
            logicalStart,
            decodedSize,
            storedSize,
            archiveOffset,
            (int)archiveId,
            hasStoredInfo,
            pageSizes,
            metadata,
            control));
    }

    private static Refusal Truncated(int terminalOffset, int index, string what) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"The filename index terminal at 0x{terminalOffset:X} chunk {index} has a truncated {what}."));

    private static bool TryUInt(ReadOnlySpan<byte> table, ref int cursor, int width, out long value)
    {
        value = 0;
        if (width < 0 || cursor < 0 || table.Length - cursor < width)
        {
            return false;
        }

        for (int i = 0; i < width; i++)
        {
            value |= (long)table[cursor + i] << (8 * i);
        }

        cursor += width;
        return true;
    }
}
