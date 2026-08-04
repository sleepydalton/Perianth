using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32.SafeHandles;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Sdf;

/// <summary>
/// Bounds-checked read-only access to a directory of payload archives.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the project that reads byte ranges out of a file
/// rather than taking a whole-file snapshot. The archive set for one title is
/// tens of gigabytes across thousands of files, the largest a few hundred
/// megabytes, so <see cref="Io.SourceFileReader"/>'s read-it-all-and-compare
/// guard is not available here.
/// </para>
/// <para>
/// What replaces it: a handle is opened once and kept, every range is checked
/// against the archive's length as the open handle reports it, and a short
/// read refuses rather than returning what it got. On a file replaced while
/// this object lives, the retained handle still refers to the bytes that were
/// there when it was opened, which is the safe direction; a file truncated in
/// place is caught by the length check.
/// </para>
/// <para>
/// Handles are kept in a small most-recently-used set rather than one per
/// archive, because a material pass touches far more archives than a process
/// should hold open at once.
/// </para>
/// </remarks>
public sealed class SdfArchiveSet : IDisposable
{
    private const string Families = "ABC";
    private const int IdsPerFamily = 1000;
    private const int MaxOpenHandles = 8;

    private readonly string _root;
    private readonly Dictionary<int, SafeFileHandle> _handles = [];
    private readonly List<int> _order = [];
    private bool _disposed;

    /// <summary>Creates a set over the directory holding the archives.</summary>
    public SdfArchiveSet(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        _root = root;
    }

    /// <summary>
    /// Returns the filename one archive ID selects.
    /// </summary>
    /// <remarks>
    /// The family letter is the ID divided by the per-family span and the
    /// number is the ID zero-padded to four digits. Only families A, B and C
    /// were observed; an ID beyond them names no file, so it refuses rather
    /// than being extrapolated into a fourth letter.
    /// </remarks>
    public static Result<string> FileName(int archiveId)
    {
        if (archiveId < 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive id {archiveId} is negative."));
        }

        int family = archiveId / IdsPerFamily;
        if (family >= Families.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive id {archiveId} is beyond the {Families} families this container names."));
        }

        return Result.Ok(string.Create(
            CultureInfo.InvariantCulture,
            $"sdf-{Families[family]}-{archiveId:D4}.sdfdata"));
    }

    /// <summary>
    /// Returns exactly <paramref name="size"/> bytes at <paramref name="offset"/>.
    /// </summary>
    public Result<byte[]> Read(int archiveId, long offset, long size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Result<string> name = FileName(archiveId);
        if (!name.TryGetValue(out string? fileName, out Refusal? nameRefusal))
        {
            return nameRefusal;
        }

        if (offset < 0 || size < 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive {fileName} range {offset}+{size} is negative."));
        }

        if (size > int.MaxValue)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive {fileName} range {offset}+{size} does not fit in one buffer."));
        }

        Result<SafeFileHandle> opened = Open(archiveId, fileName);
        if (!opened.TryGetValue(out SafeFileHandle? handle, out Refusal? openRefusal))
        {
            return openRefusal;
        }

        long length;
        try
        {
            length = RandomAccess.GetLength(handle);
        }
        catch (IOException error)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Cannot measure archive {fileName}: {error.Message}"), DiagnosticIds.ResourceMissing);
        }

        if (offset > length || size > length - offset)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive {fileName} range {offset}+{size} lies outside its {length} bytes."));
        }

        byte[] buffer = new byte[size];

        try
        {
            int read = RandomAccess.Read(handle, buffer, offset);
            while (read < buffer.Length)
            {
                int more = RandomAccess.Read(handle, buffer.AsSpan(read), offset + read);
                if (more == 0)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Archive {fileName} returned {read} of {size} bytes at {offset}; it may have changed while it was being read."));
                }

                read += more;
            }
        }
        catch (IOException error)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Cannot read archive {fileName}: {error.Message}"), DiagnosticIds.ResourceMissing);
        }
        catch (OutOfMemoryException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Resources are insufficient to read archive {fileName}."));
        }

        return Result.Ok(buffer);
    }

    /// <summary>Closes every retained handle.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (SafeFileHandle handle in _handles.Values)
        {
            handle.Dispose();
        }

        _handles.Clear();
        _order.Clear();
    }

    private Result<SafeFileHandle> Open(int archiveId, string fileName)
    {
        if (_handles.TryGetValue(archiveId, out SafeFileHandle? existing))
        {
            _order.Remove(archiveId);
            _order.Add(archiveId);
            return Result.Ok(existing);
        }

        string path = Path.Combine(_root, fileName);

        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Archive {fileName} is not present."), DiagnosticIds.ResourceMissing);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"Cannot open archive {fileName}: {error.Message}"), DiagnosticIds.ResourceMissing);
        }

        if (_order.Count >= MaxOpenHandles)
        {
            int evicted = _order[0];
            _order.RemoveAt(0);
            if (_handles.Remove(evicted, out SafeFileHandle? stale))
            {
                stale.Dispose();
            }
        }

        _handles[archiveId] = handle;
        _order.Add(archiveId);
        return Result.Ok(handle);
    }
}
