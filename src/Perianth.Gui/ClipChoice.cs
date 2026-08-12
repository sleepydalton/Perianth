using System;

namespace Perianth.Gui;

/// <summary>
/// One clip a model can play, named as a person would ask for it, and whether it
/// was chosen for the export.
/// </summary>
/// <remarks>
/// <para>
/// The archive spells clips <c>anm_cartman_base_walk_front</c>. The character's
/// own name is in every one of them and carries no information at the point of
/// choosing, so it is dropped: what remains is what distinguishes one clip from
/// the next.
/// </para>
/// <para>
/// Several can be chosen at once, which is why this carries its own selected
/// state rather than the pane holding a single selection. A character can have
/// hundreds of clips, so the list is filtered rather than scrolled, and a row
/// has to stay chosen while the filter hides it — otherwise typing a new search
/// would silently drop what was already picked.
/// </para>
/// </remarks>
public sealed class ClipChoice : ViewModelBase
{
    private bool _chosen;

    /// <param name="label">What the list shows.</param>
    /// <param name="virtualPath">The archive path.</param>
    public ClipChoice(string label, string? virtualPath)
    {
        Label = label;
        VirtualPath = virtualPath;
    }

    /// <summary>Raised when this row is ticked or unticked.</summary>
    public event Action? Changed;

    /// <summary>What the list shows.</summary>
    public string Label { get; }

    /// <summary>The archive path, or none for "no clip".</summary>
    public string? VirtualPath { get; }

    /// <summary>Whether this clip is part of the export.</summary>
    public bool Chosen
    {
        get => _chosen;
        set
        {
            if (Set(ref _chosen, value))
            {
                Changed?.Invoke();
            }
        }
    }

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
