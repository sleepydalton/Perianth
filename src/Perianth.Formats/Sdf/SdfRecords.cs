using System;
using System.Collections.Immutable;

namespace Perianth.Formats.Sdf;

/// <summary>
/// Container facts the index bytes do not describe.
/// </summary>
/// <remarks>
/// The runtime takes these from state on the opened source rather than from
/// any node, so whatever opened the container has to supply them. Keeping
/// them in one object means that the day the governing flag is located in the
/// header, only its selection changes.
/// </remarks>
/// <param name="HasChunkMetadata">
/// Whether every chunk record ends with a trailing 32-bit field. Where the
/// governing flag lives in the container is unknown, so it is selected by
/// container version instead. It is present in the verified version-0x16
/// index: of that index's 23,101 two-chunk terminals, all 23,101 decode to a
/// well-formed second chunk when the field is consumed and only 2,093 when it
/// is not, the malformed remainder being indistinguishable from random bytes.
/// </param>
public readonly record struct SdfIndexLayout(bool HasChunkMetadata)
{
    /// <summary>The layout proven for table-of-contents version 0x16.</summary>
    public static SdfIndexLayout V16 => new(HasChunkMetadata: true);
}

/// <summary>
/// One physically located run of a file's logical bytes.
/// </summary>
/// <param name="LogicalStart">Byte offset of this chunk within the file's archive-backed bytes.</param>
/// <param name="DecodedSize">Bytes this chunk contributes once expanded.</param>
/// <param name="StoredSize">Bytes it occupies in the archive, when one is recorded.</param>
/// <param name="ArchiveOffset">Where in the archive its first page begins.</param>
/// <param name="ArchiveId">Which archive holds it. Nothing spans archives.</param>
/// <param name="HasStoredInfo">Whether a stored size, and page sizes, are present.</param>
/// <param name="PageStoredSizes">Per-page stored sizes, empty unless the chunk is multi-page.</param>
/// <param name="Metadata">
/// The trailing per-chunk field, when the layout says one is present. Its
/// meaning is unknown and deliberately not interpreted; it is carried so a
/// future reading can be checked against real data.
/// </param>
/// <param name="Control">The control byte, kept so a misalignment can be diagnosed.</param>
public readonly record struct SdfChunk(
    long LogicalStart,
    long DecodedSize,
    long StoredSize,
    long ArchiveOffset,
    int ArchiveId,
    bool HasStoredInfo,
    ImmutableArray<int> PageStoredSizes,
    uint Metadata,
    byte Control);

/// <summary>
/// One terminal entry: a complete logical file's placement metadata.
/// </summary>
/// <remarks>
/// A terminal encoding no chunks is a directory or other non-file node. It is
/// returned as-is with an empty <see cref="Chunks"/>; deciding what that means
/// is left to the layer above.
/// </remarks>
/// <param name="Path">The normalized path this descent matched.</param>
/// <param name="TotalSize">
/// Sum of the chunks' decoded sizes. This counts only archive-backed bytes, so
/// a resident prefix's length is added on top of it rather than included.
/// </param>
/// <param name="Chunks">The chunks, in encoded order.</param>
/// <param name="ResidentIndex">Which resident prefix to prepend, or null for none.</param>
/// <param name="ReadAheadBlocks">The header's read-ahead hint, which does not affect decoding.</param>
/// <param name="FileMetadata">
/// The 32-bit file-level value following the terminal tag. Unknown and
/// deliberately not interpreted; carried as evidence.
/// </param>
/// <param name="Tag">The terminal tag byte.</param>
public sealed record SdfEntry(
    string Path,
    long TotalSize,
    ImmutableArray<SdfChunk> Chunks,
    int? ResidentIndex,
    int ReadAheadBlocks,
    uint FileMetadata,
    byte Tag)
{
    /// <summary>Whether this terminal names no archive-backed bytes.</summary>
    public bool IsDirectory => Chunks.IsDefaultOrEmpty;
}

/// <summary>
/// One path the filename index spells, and where its terminal sits.
/// </summary>
/// <remarks>
/// What a full walk of the index can say without decoding any terminal body.
/// <see cref="SdfEntry"/> answers where a file's bytes live and costs a full
/// terminal decode per path; this answers what paths exist, which is the
/// question listing and searching ask over the whole tree at once.
/// </remarks>
/// <param name="Path">
/// The path as the index spells it, in the tree's own case. Lookup folds case,
/// so this is display and search text; normalize it before comparing it with
/// <see cref="SdfIndex.NormalizePath"/>.
/// </param>
/// <param name="NodeOffset">
/// Where this path's terminal begins, so the entry can be decoded without
/// descending the tree a second time.
/// </param>
/// <param name="IsDirectory">
/// Whether the terminal encodes no chunks, read from the tag alone.
/// </param>
public readonly record struct SdfPathEntry(string Path, int NodeOffset, bool IsDirectory);

/// <summary>
/// The outcome of asking an archive set for a path.
/// </summary>
/// <remarks>
/// Absence is an ordinary result, not a refusal: the resolution order in the
/// porting specification tries loose content first and falls back to the
/// archives only when the loose source says the exact path is absent, so
/// "not here" has to be answerable without failing. A <see cref="Diagnostics.Refusal"/>
/// from the same call means the container itself could not be decoded.
/// </remarks>
public readonly record struct SdfContent(bool IsPresent, ReadOnlyMemory<byte> Bytes)
{
    /// <summary>The archive does not hold this path.</summary>
    public static SdfContent Absent => new(IsPresent: false, default);

    /// <summary>The archive holds this path, and here are its bytes.</summary>
    public static SdfContent Present(ReadOnlyMemory<byte> bytes) => new(IsPresent: true, bytes);
}
