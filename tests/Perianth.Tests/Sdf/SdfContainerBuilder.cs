using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Perianth.Tests.Sdf;

/// <summary>
/// Builds a synthetic SDF container on disk so the grammar can be tested
/// without the game's archives.
/// </summary>
internal sealed class SdfContainerBuilder
{
    internal const int PageBytes = 0x10000;

    private readonly List<byte> _archive = [];

    public string Magic { get; set; } = "WEST";

    public uint Version { get; set; } = 0x16;

    public byte LayoutFlag { get; set; }

    public uint InstallPartCount { get; set; }

    /// <summary>Resident-prefix payloads, indexed as the terminals name them.</summary>
    public List<byte[]> ResidentPrefixes { get; } = [];

    /// <summary>The inflated filename index.</summary>
    public byte[] Index { get; set; } = [];

    /// <summary>
    /// Appends bytes to the archive and returns the offset they landed at.
    /// </summary>
    public long AppendToArchive(ReadOnlySpan<byte> bytes)
    {
        long offset = _archive.Count;
        _archive.AddRange(bytes);
        return offset;
    }

    /// <summary>Deflates one page's worth of bytes, zlib-wrapped.</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> bytes)
    {
        using MemoryStream output = new();
        using (ZLibStream stream = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            stream.Write(bytes);
        }

        return output.ToArray();
    }

    /// <summary>Writes the container into <paramref name="directory"/>.</summary>
    public void Write(string directory)
    {
        File.WriteAllBytes(Path.Combine(directory, "sdf-A-0000.sdfdata"), [.. _archive]);
        File.WriteAllBytes(Path.Combine(directory, "sdf.sdftoc"), BuildToc());
    }

    private byte[] BuildToc()
    {
        byte[] compressed = Deflate(Index);
        List<byte> toc = [];

        foreach (char c in Magic)
        {
            toc.Add((byte)c);
        }

        AddUInt32(toc, Version);
        AddUInt32(toc, (uint)Index.Length);
        AddUInt32(toc, (uint)compressed.Length);
        AddUInt32(toc, 0);
        AddUInt32(toc, InstallPartCount);
        AddUInt32(toc, (uint)ResidentPrefixes.Count);

        AddIdentity(toc);

        toc.Add(LayoutFlag);
        if (LayoutFlag != 0)
        {
            toc.AddRange(new byte[0x140]);
        }

        for (uint i = 0; i < InstallPartCount; i++)
        {
            AddUInt32(toc, 0);
        }

        for (uint i = 0; i < InstallPartCount; i++)
        {
            AddIdentity(toc);
        }

        foreach (byte[] prefix in ResidentPrefixes)
        {
            AddUInt32(toc, (uint)prefix.Length);
            byte[] record = new byte[0x98 - 4];
            prefix.CopyTo(record, 0);
            toc.AddRange(record);
        }

        toc.AddRange(compressed);
        return [.. toc];
    }

    private static void AddIdentity(List<byte> toc)
    {
        // A NUL-terminated label, an opaque blob, then a second label. The
        // reader measures the stride from the first record rather than
        // assuming one, so the labels' lengths are deliberately not 0x30's.
        toc.AddRange("vendor\0"u8);
        toc.AddRange(new byte[0x20]);
        toc.AddRange("engine\0"u8);
    }

    private static void AddUInt32(List<byte> target, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        target.AddRange(bytes);
    }
}

/// <summary>
/// Emits filename-index nodes, resolving branch targets after the fact.
/// </summary>
internal sealed class SdfIndexBuilder
{
    private readonly List<byte> _bytes = [];

    public int Position => _bytes.Count;

    /// <summary>Appends a literal path segment, at most 31 bytes.</summary>
    public SdfIndexBuilder Literal(string segment)
    {
        _bytes.Add((byte)segment.Length);
        foreach (char c in segment)
        {
            _bytes.Add((byte)c);
        }

        return this;
    }

    /// <summary>
    /// Appends a branch on <paramref name="key"/>. The returned offset is
    /// where the four-byte alternate target must later be patched in.
    /// </summary>
    public int Branch(char key)
    {
        _bytes.Add((byte)key);
        int patch = _bytes.Count;
        _bytes.AddRange(new byte[4]);
        return patch;
    }

    /// <summary>Writes an alternate target into a slot Branch returned.</summary>
    public SdfIndexBuilder PatchBranch(int patch, int target)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)target);
        for (int i = 0; i < 4; i++)
        {
            _bytes[patch + i] = bytes[i];
        }

        return this;
    }

    /// <summary>
    /// Appends a terminal. <paramref name="chunkCount"/> goes in the tag's low
    /// three bits and <paramref name="pathPatch"/> sets its bit 3.
    /// </summary>
    public SdfIndexBuilder Terminal(
        int chunkCount,
        int residentIndex = -1,
        bool pathPatch = false,
        uint fileMetadata = 0)
    {
        int value = chunkCount | (pathPatch ? 0x8 : 0);
        _bytes.Add((byte)('A' + value));
        AddUInt32(fileMetadata);

        // Low two bits are the resident index's byte width; the rest is a
        // read-ahead hint that does not affect decoding.
        int residentWidth = residentIndex < 0 ? 0 : 1;
        _bytes.Add((byte)(residentWidth | (3 << 2)));

        if (residentIndex >= 0)
        {
            _bytes.Add((byte)residentIndex);
        }

        return this;
    }

    /// <summary>Appends one chunk record.</summary>
    public SdfIndexBuilder Chunk(
        long decodedSize,
        long archiveOffset,
        int archiveId = 0,
        long? storedSize = null,
        IReadOnlyList<int>? pageStoredSizes = null,
        int offsetWidth = 4,
        int sizeWidth = 4,
        uint metadata = 0)
    {
        bool hasStoredInfo = storedSize.HasValue;
        int control = (sizeWidth - 1) | (offsetWidth << 2) | (hasStoredInfo ? 0x20 : 0);
        _bytes.Add((byte)control);

        AddVariable(decodedSize, sizeWidth);
        if (hasStoredInfo)
        {
            AddVariable(storedSize!.Value, sizeWidth);
        }

        AddVariable(archiveOffset, offsetWidth);
        AddVariable(archiveId, 2);

        if (hasStoredInfo && pageStoredSizes is not null)
        {
            foreach (int size in pageStoredSizes)
            {
                AddVariable(size, 2);
            }
        }

        // The trailing per-chunk field the verified container carries.
        AddUInt32(metadata);
        return this;
    }

    public byte[] Build() => [.. _bytes];

    private void AddUInt32(uint value) => AddVariable(value, 4);

    private void AddVariable(long value, int width)
    {
        for (int i = 0; i < width; i++)
        {
            _bytes.Add((byte)((value >> (8 * i)) & 0xFF));
        }
    }
}
