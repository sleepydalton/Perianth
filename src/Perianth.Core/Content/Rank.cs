using System;

namespace Perianth.Core.Content;

/// <summary>
/// Which of two paths a search should offer first.
/// </summary>
/// <remarks>
/// <para>
/// Alphabetical looks neutral and is not. Searching <c>cartman</c> matches
/// 2,291 paths in the shipped archive, and alphabetically the one thing anyone
/// typing that wants — <c>chr_cartman.mmb</c> — sits at position <b>528</b>,
/// behind hundreds of <c>.manimsys</c> and <c>.juice</c> files this tool has no
/// reader for. Under a cap it would not be shown at all.
/// </para>
/// <para>
/// So the order is: a file type the tool can open, models first; then a match in
/// the name rather than only in the folder; then the shorter path. Ties fall
/// back to the path itself, so the same query always gives the same order.
/// </para>
/// </remarks>
public static class Rank
{
    /// <summary>
    /// The file types the tool can do something with, best first.
    /// </summary>
    /// <remarks>
    /// A model leads because every other asset is assembled around one. Anything
    /// absent is game data with no reader here, and belongs behind all of it.
    /// </remarks>
    private static readonly string[] Known =
        [".mmb", ".cameldata", ".editordata", ".anim", ".dds", ".wem", ".mlipsyncdatabase"];

    /// <summary>Negative when <paramref name="left"/> should be offered first.</summary>
    public static int Compare(string left, string right, string wanted)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        int byKind = Kind(left).CompareTo(Kind(right));
        if (byKind != 0)
        {
            return byKind;
        }

        int byName = Named(left, wanted).CompareTo(Named(right, wanted));
        if (byName != 0)
        {
            return byName;
        }

        int byLength = left.Length.CompareTo(right.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left, right);
    }

    private static int Kind(string path)
    {
        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return Known.Length;
        }

        int index = Array.IndexOf(Known, path[dot..]);
        return index < 0 ? Known.Length : index;
    }

    private static int Named(string path, string wanted) =>
        path[(path.LastIndexOf('/') + 1)..].Contains(wanted, StringComparison.Ordinal) ? 0 : 1;
}
