using System;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// Where a texture's bytes come from, and in what order.
/// </summary>
/// <remarks>
/// <para>
/// Loose content takes precedence and the archives are tried only when the
/// loose source says the exact path is <em>absent</em>. If the loose path
/// exists but is unreadable, malformed, outside the root through a symbolic
/// link, or changes while it is read, the export refuses: falling back to the
/// shipped archive bytes there would silently export something other than what
/// the caller put on disk.
/// </para>
/// <para>
/// That is why absence has to be distinct from failure the whole way down. A
/// source that reported "could not read" for both would make the precedence
/// rule unimplementable.
/// </para>
/// </remarks>
public sealed class ContentSources : IDisposable
{
    private readonly string? _contentRoot;
    private readonly SdfContentSource? _archives;
    private bool _disposed;

    /// <summary>
    /// Creates a resolver over an optional loose tree and an optional archive set.
    /// </summary>
    public ContentSources(string? contentRoot, string? sdfRoot)
    {
        _contentRoot = contentRoot;
        _archives = sdfRoot is null ? null : new SdfContentSource(sdfRoot);
    }

    /// <summary>Whether any source at all was supplied.</summary>
    public bool HasAny => _contentRoot is not null || _archives is not null;

    /// <summary>
    /// Reads one normalized virtual path.
    /// </summary>
    /// <returns>
    /// A successful result whose value is null when no source holds the path.
    /// </returns>
    public Result<byte[]?> Read(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_contentRoot is not null)
        {
            Result<byte[]?> loose = ReadLoose(_contentRoot, normalizedPath);
            if (!loose.TryGetValue(out byte[]? bytes, out Refusal? refusal))
            {
                return refusal;
            }

            if (bytes is not null)
            {
                return Result.Ok<byte[]?>(bytes);
            }
        }

        if (_archives is null)
        {
            return Result.Ok<byte[]?>(null);
        }

        Result<SdfContent> archived = _archives.Read(normalizedPath);
        if (!archived.TryGetValue(out SdfContent content, out Refusal? archiveRefusal))
        {
            return archiveRefusal;
        }

        return Result.Ok(content.IsPresent ? content.Bytes.ToArray() : null);
    }

    /// <summary>Closes any archive handles.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _archives?.Dispose();
    }

    /// <summary>
    /// Reads one path beneath a loose content root, preserving case.
    /// </summary>
    private static Result<byte[]?> ReadLoose(string root, string normalizedPath)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The content root '{root}' is not a usable path."), DiagnosticIds.ResourceMissing);
        }

        if (!Directory.Exists(fullRoot))
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The content root '{root}' is not a directory."), DiagnosticIds.ResourceMissing);
        }

        string candidate = fullRoot;
        foreach (string component in TexturePath.Components(normalizedPath))
        {
            candidate = Path.Combine(candidate, component);
        }

        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            // Absence specifically, so the archives can be tried.
            return Result.Ok<byte[]?>(null);
        }

        // Resolve links before comparing, so a symbolic link pointing outside
        // the root is caught rather than followed.
        string resolved;
        try
        {
            resolved = Path.GetFullPath(new FileInfo(candidate).LinkTarget ?? candidate);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The texture {normalizedPath} is inaccessible from the content root."), DiagnosticIds.ResourceMissing);
        }

        string prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The texture {normalizedPath} resolves outside the content root."));
        }

        // The whole-file snapshot guard applies here: a loose texture is small
        // enough to read entire, and one that changes mid-read is refused.
        Result<SourceFile> file = SourceFileReader.Read(resolved);
        return file.TryGetValue(out SourceFile? source, out Refusal? refusal)
            ? Result.Ok<byte[]?>(source.Bytes.ToArray())
            : refusal;
    }
}
