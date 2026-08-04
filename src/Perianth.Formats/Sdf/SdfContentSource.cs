using System;
using System.Collections.Immutable;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Sdf;

/// <summary>
/// Reads files straight out of an SDF archive set.
/// </summary>
/// <remarks>
/// <para>
/// The three layers below it join here: the table of contents supplies the
/// filename index and the resident-prefix table, the index resolves a path to
/// one entry, and the archives supply that entry's bytes.
/// </para>
/// <para>
/// The table of contents is read once, on first use, and held for the life of
/// the source. Entries are resolved one lookup at a time and nothing is
/// cached, so the cost of a read is the length of its own path rather than the
/// size of the index.
/// </para>
/// <para>
/// This type takes a virtual path as the archive spells it. Appending
/// <c>.dds</c> to a suffixless texture reference, and refusing absolute paths,
/// <c>..</c> and colon-bearing components, is texture-path resolution rather
/// than container grammar and belongs to the layer that decides which source
/// to consult.
/// </para>
/// </remarks>
public sealed class SdfContentSource : IDisposable
{
    private const string TocName = "sdf.sdftoc";

    private readonly string _root;
    private readonly SdfArchiveSet _archives;
    private SdfToc? _toc;
    private bool _disposed;

    /// <summary>Creates a source over the directory holding the container.</summary>
    public SdfContentSource(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
        _archives = new SdfArchiveSet(root);
    }

    /// <summary>
    /// The parsed table of contents, read on first use.
    /// </summary>
    public Result<SdfToc> Toc()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_toc is not null)
        {
            return Result.Ok(_toc);
        }

        // The table of contents is small enough to read whole, so it keeps the
        // snapshot discipline the archives cannot.
        Result<SourceFile> file = SourceFileReader.Read(Path.Combine(_root, TocName));
        if (!file.TryGetValue(out SourceFile? source, out Refusal? fileRefusal))
        {
            return fileRefusal;
        }

        Result<SdfToc> parsed = SdfTocReader.Read(source.Memory);
        if (!parsed.TryGetValue(out SdfToc? toc, out Refusal? tocRefusal))
        {
            return tocRefusal;
        }

        _toc = toc;
        return Result.Ok(toc);
    }

    /// <summary>
    /// Every path the archive set holds.
    /// </summary>
    /// <remarks>
    /// The container offers no directory listing, so this is the only way to
    /// answer what is in it: a full walk of the filename index. Searching is the
    /// caller's to do over the returned paths — there is one tree and one shape
    /// of answer, and a search method per question is how the last attempt at
    /// this tool reached 44,588 lines.
    /// </remarks>
    public Result<ImmutableArray<SdfPathEntry>> Paths()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<SdfToc> loaded = Toc();
        return loaded.TryGetValue(out SdfToc? toc, out Refusal? tocRefusal)
            ? SdfIndex.Enumerate(toc.FileTable.Span)
            : tocRefusal;
    }

    /// <summary>
    /// Returns the named file's complete bytes, or reports that the archive
    /// does not hold it.
    /// </summary>
    /// <remarks>
    /// Absence is carried in the value rather than as a refusal, because the
    /// resolution order tries loose content first and consults the archives
    /// only when the exact path is absent from it. A refusal here means the
    /// container could not be decoded, which is a different answer entirely.
    /// </remarks>
    public Result<SdfContent> Read(string virtualPath)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<SdfToc> loaded = Toc();
        if (!loaded.TryGetValue(out SdfToc? toc, out Refusal? tocRefusal))
        {
            return tocRefusal;
        }

        Result<SdfEntry?> found = SdfIndex.Lookup(toc.FileTable.Span, virtualPath, toc.Layout);
        if (!found.TryGetValue(out SdfEntry? entry, out Refusal? lookupRefusal))
        {
            return lookupRefusal;
        }

        if (entry is null)
        {
            return Result.Ok(SdfContent.Absent);
        }

        if (entry.IsDirectory)
        {
            return Refusal.Unsupported($"{entry.Path} names a directory rather than a file.");
        }

        ReadOnlyMemory<byte>? prefix = null;
        if (entry.ResidentIndex is int index)
        {
            Result<ReadOnlyMemory<byte>> resident = toc.ResidentPrefix(index);
            if (!resident.TryGetValue(out ReadOnlyMemory<byte> bytes, out Refusal? residentRefusal))
            {
                return residentRefusal;
            }

            prefix = bytes;
        }

        Result<byte[]> content = SdfPayload.ReadEntry(_archives, entry, prefix);
        return content.TryGetValue(out byte[]? payload, out Refusal? payloadRefusal)
            ? Result.Ok(SdfContent.Present(payload))
            : payloadRefusal;
    }

    /// <summary>Closes every retained archive handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archives.Dispose();
    }
}
