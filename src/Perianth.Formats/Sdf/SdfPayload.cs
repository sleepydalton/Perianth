using System;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Sdf;

/// <summary>
/// Turns an index entry into bytes. Performs no lookup and interprets no paths.
/// </summary>
/// <remarks>
/// <para>
/// A chunk recording no stored size is neither compressed nor paged: read its
/// decoded size directly. Otherwise its bytes are stored as 64 KiB pages,
/// every page but the last decoding to exactly one full page, the last holding
/// the declared remainder.
/// </para>
/// <para>
/// Two encoding rules here are easy to get wrong in opposite directions. A
/// 16-bit page size cannot express a full page, so a stored size of zero means
/// <c>0x10000</c>; reading it as a length truncates. And a page whose stored
/// size equals its decoded size was written through uncompressed; treating it
/// as deflate fails on the first byte. A full uncompressed page is both at
/// once — stored as zero <em>and</em> equal to its decoded size — which is the
/// case that breaks a reader that only handles one of the two.
/// </para>
/// </remarks>
public static class SdfPayload
{
    private const int PageBytes = 0x10000;

    /// <summary>
    /// Returns one entry's complete bytes: its resident prefix, when it names
    /// one, followed by its chunks in encoded order.
    /// </summary>
    /// <remarks>
    /// The composition is generic. Nothing here inspects the path, the
    /// extension or the prefix contents, so a non-texture file naming a prefix
    /// is assembled by exactly the same rule. The entry's declared total counts
    /// only the archive-backed chunks, so the prefix length is added on top of
    /// it rather than included in it.
    /// </remarks>
    public static Result<byte[]> ReadEntry(
        SdfArchiveSet archives,
        SdfEntry entry,
        ReadOnlyMemory<byte>? residentPrefix)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(entry);

        if ((entry.ResidentIndex is not null) != residentPrefix.HasValue)
        {
            // Supplying a prefix for an entry that names none, or omitting one
            // for an entry that does, would quietly produce a file of the wrong
            // length.
            return Refusal.Malformed(residentPrefix.HasValue
                ? $"{entry.Path} names no resident prefix, but one was supplied."
                : $"{entry.Path} names a resident prefix, but none was supplied to prepend.");
        }

        if (entry.IsDirectory)
        {
            return Refusal.Unsupported($"{entry.Path} names no chunks and has no archive-backed bytes.");
        }

        int prefixLength = residentPrefix?.Length ?? 0;
        long expected = prefixLength + entry.TotalSize;

        if (expected > int.MaxValue)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Path} declares {expected} bytes, which does not fit in one buffer."));
        }

        byte[] content = new byte[expected];
        residentPrefix?.Span.CopyTo(content);
        int written = prefixLength;

        foreach (SdfChunk chunk in entry.Chunks)
        {
            Result<byte[]> decoded = ReadChunk(archives, chunk);
            if (!decoded.TryGetValue(out byte[]? bytes, out Refusal? refusal))
            {
                return refusal;
            }

            if (bytes.Length > content.Length - written)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.Path} assembled past the {expected} bytes its entry declares."));
            }

            bytes.CopyTo(content, written);
            written += bytes.Length;
        }

        if (written != content.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"{entry.Path} assembled to {written} bytes, but its prefix and chunks declare {expected}."));
        }

        return Result.Ok(content);
    }

    /// <summary>Returns one chunk's decoded bytes.</summary>
    internal static Result<byte[]> ReadChunk(SdfArchiveSet archives, SdfChunk chunk)
    {
        if (!chunk.HasStoredInfo)
        {
            // No stored size is recorded, so the bytes are neither compressed
            // nor paged: the decoded length is also the length on disk.
            return archives.Read(chunk.ArchiveId, chunk.ArchiveOffset, chunk.DecodedSize);
        }

        if (chunk.StoredSize == 0 && chunk.DecodedSize != 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A chunk in archive {chunk.ArchiveId} declares no stored bytes for {chunk.DecodedSize} decoded bytes."));
        }

        Result<ImmutableArray<SdfPage>> expanded = ExpandPages(chunk);
        if (!expanded.TryGetValue(out ImmutableArray<SdfPage> pages, out Refusal? pageRefusal))
        {
            return pageRefusal;
        }

        byte[] output = new byte[chunk.DecodedSize];
        int written = 0;

        for (int i = 0; i < pages.Length; i++)
        {
            SdfPage page = pages[i];
            Result<byte[]> stored = archives.Read(chunk.ArchiveId, page.ArchiveOffset, page.StoredSize);
            if (!stored.TryGetValue(out byte[]? bytes, out Refusal? readRefusal))
            {
                return readRefusal;
            }

            if (page.StoredSize == page.DecodedSize)
            {
                // Equal lengths mean the page was written through uncompressed.
                bytes.CopyTo(output, written);
                written += bytes.Length;
                continue;
            }

            Result<byte[]> inflated = SdfTocReader.Inflate(
                bytes,
                page.DecodedSize,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"page {i} at {page.ArchiveOffset} in archive {chunk.ArchiveId}"));

            if (!inflated.TryGetValue(out byte[]? expandedPage, out Refusal? inflateRefusal))
            {
                return inflateRefusal;
            }

            expandedPage.CopyTo(output, written);
            written += expandedPage.Length;
        }

        if (written != output.Length)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A chunk in archive {chunk.ArchiveId} assembled to {written} bytes, but declares {chunk.DecodedSize}."));
        }

        return Result.Ok(output);
    }

    /// <summary>
    /// Expands a chunk's compact page metadata into explicit page records.
    /// </summary>
    /// <remarks>
    /// A page's position is the running total of the stored sizes before it,
    /// so one wrong size silently displaces every page that follows. The
    /// aggregate stored size is therefore checked against the sum of the page
    /// sizes, which catches a misread vector before any bytes are read.
    /// </remarks>
    internal static Result<ImmutableArray<SdfPage>> ExpandPages(SdfChunk chunk)
    {
        long pageCount = (chunk.DecodedSize + PageBytes - 1) / PageBytes;

        if (pageCount <= 1)
        {
            if (!chunk.PageStoredSizes.IsDefaultOrEmpty)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A chunk in archive {chunk.ArchiveId} occupies one page but records {chunk.PageStoredSizes.Length} page sizes."));
            }

            return Result.Ok(ImmutableArray.Create(
                new SdfPage(chunk.ArchiveOffset, (int)chunk.StoredSize, (int)chunk.DecodedSize)));
        }

        if (chunk.PageStoredSizes.Length != pageCount)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A chunk in archive {chunk.ArchiveId} declares {chunk.DecodedSize} decoded bytes, needing {pageCount} pages, but records {chunk.PageStoredSizes.Length} page sizes."));
        }

        ImmutableArray<SdfPage>.Builder pages = ImmutableArray.CreateBuilder<SdfPage>((int)pageCount);
        long offset = chunk.ArchiveOffset;
        long storedTotal = 0;

        // The last page holds whatever the declared size leaves over; a zero
        // remainder means the chunk ends on an exact page boundary.
        long finalDecoded = chunk.DecodedSize % PageBytes;
        if (finalDecoded == 0)
        {
            finalDecoded = PageBytes;
        }

        for (int i = 0; i < pageCount; i++)
        {
            // A 16-bit page size cannot express a full page, so zero means one.
            int stored = chunk.PageStoredSizes[i] == 0 ? PageBytes : chunk.PageStoredSizes[i];
            int decoded = i == pageCount - 1 ? (int)finalDecoded : PageBytes;

            pages.Add(new SdfPage(offset, stored, decoded));
            offset += stored;
            storedTotal += stored;
        }

        if (storedTotal != chunk.StoredSize)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"A chunk in archive {chunk.ArchiveId} declares {chunk.StoredSize} stored bytes, but its pages account for {storedTotal}."));
        }

        return Result.Ok(pages.MoveToImmutable());
    }
}

/// <summary>One page's physical placement and its two lengths.</summary>
/// <param name="ArchiveOffset">Where the page's stored bytes begin.</param>
/// <param name="StoredSize">Bytes it occupies, after the zero rule is applied.</param>
/// <param name="DecodedSize">Bytes it expands to.</param>
public readonly record struct SdfPage(long ArchiveOffset, int StoredSize, int DecodedSize);
