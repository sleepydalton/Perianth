using System;
using System.IO;

namespace Perianth.Formats.Io;

/// <summary>
/// What is known about a file immediately before and immediately after reading
/// it, so the two can be compared.
/// </summary>
/// <remarks>
/// Length and last-write time are what a stat gives portably, and they catch the
/// case this guard exists for: a file being rewritten underneath the tool while
/// it is parsed. They are not a proof of identity. A writer that restores the
/// original timestamp and length would pass, and this comparison would miss it.
/// That limit is worth stating rather than implying a stronger promise; closing
/// it would mean hashing every input twice, which buys little against an
/// accident and nothing against intent.
/// </remarks>
internal readonly record struct FileSnapshot(bool Exists, long Length, DateTime LastWriteTimeUtc)
{
    /// <summary>Reads the current state of <paramref name="info"/>.</summary>
    public static FileSnapshot Take(FileInfo info)
    {
        info.Refresh();
        return info.Exists
            ? new FileSnapshot(true, info.Length, info.LastWriteTimeUtc)
            : new FileSnapshot(false, 0, default);
    }

    /// <summary>Whether the file appears to be the same one, unmodified.</summary>
    public bool Matches(FileSnapshot other) =>
        Exists && other.Exists && Length == other.Length && LastWriteTimeUtc == other.LastWriteTimeUtc;
}
