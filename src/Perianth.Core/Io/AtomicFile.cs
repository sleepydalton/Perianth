using System;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Io;

/// <summary>
/// Publishes an output file so that it either appears complete or does not
/// appear at all.
/// </summary>
/// <remarks>
/// <para>
/// The destination is written through a temporary in the same directory, then
/// renamed. A rename within one filesystem is atomic, so a reader never sees a
/// half-written export and a refusal partway through leaves any existing file
/// exactly as it was.
/// </para>
/// <para>
/// <b>The temporary is created with <see cref="FileStream"/> deliberately.</b>
/// The runtime opens it with mode 0666 and the kernel applies the process umask,
/// which is the mode finished output should have. The obvious alternative,
/// <c>Path.GetTempFileName</c>, creates with mode 0600 — and that mode survives
/// the rename, so every published file would be owner-only. That is invisible on
/// the machine that produced it and only shows up for whoever receives the file.
/// </para>
/// </remarks>
public static class AtomicFile
{
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="path"/>.</summary>
    public static Result<int> Publish(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);

        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temporary = Path.Combine(
            directory,
            string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp"));

        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                stream.Write(bytes);

                // Flush through the operating system's buffers, not just the
                // stream's, so the rename cannot beat the contents to disk.
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
            return Result.Ok(bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Discard(temporary);

            // Naming the cause, not only the casualty. "Could not be written"
            // is true of a missing directory, a full disk and a read-only file
            // alike, and the three are fixed differently — a caller told only
            // that it failed has to go and find out which.
            string why = !Directory.Exists(directory)
                ? string.Create(CultureInfo.InvariantCulture, $"the directory {directory} does not exist")
                : ex.Message;

            return Refusal.Resource(
                string.Create(CultureInfo.InvariantCulture, $"The output file {path} could not be written: {why}."),
                DiagnosticIds.ResourceMissing);
        }
    }

    private static void Discard(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary that cannot be removed is untidy, not a failure of the
            // export, and reporting it would replace the real refusal.
        }
    }
}
