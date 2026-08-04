using System;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Io;

/// <summary>
/// Reads an input file into a verified snapshot.
/// </summary>
public static class SourceFileReader
{
    /// <summary>
    /// Reads <paramref name="path"/> completely, refusing if it is absent, is
    /// not a regular readable file, or changes while it is being read.
    /// </summary>
    public static Result<SourceFile> Read(string path) => Read(path, onBytesRead: null);

    /// <summary>
    /// The same read, with a hook that runs after the bytes are in hand and
    /// before the file is re-examined. It exists so that the change guard can be
    /// shown to fail when it is removed; there is otherwise no way to reach the
    /// window it protects without racing the filesystem.
    /// </summary>
    internal static Result<SourceFile> Read(string path, Action? onBytesRead)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (path.Length == 0)
        {
            return Refusal.Resource("No path was supplied to read.", DiagnosticIds.ResourceMissing);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Refusal.Resource(Describe("is not a usable path", path), DiagnosticIds.ResourceMissing);
        }

        FileSnapshot before = FileSnapshot.Take(info);
        if (!before.Exists)
        {
            // File.Exists is already false for a directory, so this covers both.
            return Refusal.Resource(Describe("does not exist, or is not a file", path), DiagnosticIds.ResourceMissing);
        }

        if (!TryBufferSize(before.Length, out int size))
        {
            return Refusal.Resource(
                Describe("is too large to read into memory at once", path),
                DiagnosticIds.ResourceInsufficient);
        }

        byte[] bytes;
        try
        {
            // Sharing write access is deliberate. Denying it would prevent the
            // very change this method is required to detect, and only on the
            // platform where the denial has teeth -- so the guard would be
            // unreachable on Windows and load-bearing on Linux. Detecting the
            // change behaves the same everywhere, which is what section 2 asks
            // for.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // A pipe or device reports a length that means nothing and cannot be
            // snapshotted. "Regular file" has no portable predicate in the base
            // class library; seekability is the closest honest one.
            if (!stream.CanSeek)
            {
                return Refusal.Resource(Describe("is not a regular file", path), DiagnosticIds.ResourceMissing);
            }

            bytes = new byte[size];
            stream.ReadExactly(bytes);
        }
        catch (EndOfStreamException)
        {
            // The file shrank between the stat and the read, which the guard
            // below would also catch; reaching here first says the same thing.
            return Refusal.Malformed(
                Describe("changed while it was being read", path),
                DiagnosticIds.InputChangedDuringRead);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refusal.Resource(Describe("could not be read", path), DiagnosticIds.ResourceMissing);
        }
        catch (OutOfMemoryException)
        {
            return Refusal.Resource(
                Describe("did not fit in memory", path),
                DiagnosticIds.ResourceInsufficient);
        }

        onBytesRead?.Invoke();

        FileSnapshot after = FileSnapshot.Take(info);
        if (!before.Matches(after))
        {
            // Publishing an export built from a file that moved underneath us
            // would produce a plausible-looking result from bytes that never
            // existed together.
            return Refusal.Malformed(
                Describe("changed while it was being read", path),
                DiagnosticIds.InputChangedDuringRead);
        }

        return Result.Ok(new SourceFile(path, bytes));
    }

    /// <summary>
    /// Whether a file's length can be held in one buffer. Spans are indexed by
    /// <see cref="int"/>, so anything larger has no representation to refuse
    /// later and must be refused here.
    /// </summary>
    internal static bool TryBufferSize(long length, out int size)
    {
        if (length < 0 || length > int.MaxValue)
        {
            size = 0;
            return false;
        }

        size = (int)length;
        return true;
    }

    private static string Describe(string problem, string path) =>
        string.Create(CultureInfo.InvariantCulture, $"The input file {path} {problem}.");
}
