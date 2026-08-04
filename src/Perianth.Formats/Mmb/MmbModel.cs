using System.Collections.Immutable;

namespace Perianth.Formats.Mmb;

/// <summary>
/// Everything an MMB file said: its model-part records, in byte order.
/// </summary>
public sealed class MmbModel
{
    internal MmbModel(string path, ImmutableArray<MmbModelPart> parts)
    {
        Path = path;
        Parts = parts;
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The model parts, in the order they appear in the file.</summary>
    public ImmutableArray<MmbModelPart> Parts { get; }
}
