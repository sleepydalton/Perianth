using System;

namespace Perianth.Gui;

/// <summary>
/// One clip a model can play, named as a person would ask for it.
/// </summary>
/// <remarks>
/// The archive spells clips <c>anm_cartman_base_walk_front</c>. The character's
/// own name is in every one of them and carries no information at the point of
/// choosing, so it is dropped: what remains is what distinguishes one clip from
/// the next.
/// </remarks>
/// <param name="Label">What the list shows.</param>
/// <param name="VirtualPath">The archive path, or none for "no clip".</param>
public sealed record ClipChoice(string Label, string? VirtualPath)
{
    /// <summary>The still-pose entry.</summary>
    public static ClipChoice None { get; } = new("None (still pose)", null);

    /// <summary>Names <paramref name="virtualPath"/> for a character called <paramref name="name"/>.</summary>
    public static ClipChoice For(string virtualPath, string name)
    {
        ArgumentNullException.ThrowIfNull(virtualPath);
        ArgumentNullException.ThrowIfNull(name);

        string stem = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
        int dot = stem.LastIndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        string prefix = "anm_" + name + "_";
        string label = stem.StartsWith(prefix, StringComparison.Ordinal) ? stem[prefix.Length..] : stem;

        return new ClipChoice(label.Replace('_', ' '), virtualPath);
    }
}
