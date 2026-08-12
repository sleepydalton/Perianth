using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// A folder of loose files, listed as the virtual paths the archives would spell.
/// </summary>
/// <remarks>
/// <para>
/// Everything that browses, searches, ranks and resolves works from a list of
/// virtual paths, so a folder that produces the same list is browsable by all of
/// it without any of it learning what a folder is. That is the whole design
/// here: convert once at the edge, change nothing downstream.
/// </para>
/// <para>
/// The conversion is honest because <see cref="ArchiveExtraction"/> already
/// writes the archive's own paths — so an extracted tree, a mod folder and the
/// archives are three spellings of one layout, and this reads the first two the
/// way <see cref="SdfIndex"/> reads the third. A folder that is not one of those
/// still lists; its paths simply will not resolve as a character's asset set,
/// which the panes report in their own words.
/// </para>
/// <para>
/// Separators are normalized to the archive's, and paths are relative to the
/// root. Nothing is filtered by extension: the browser's own type list is built
/// from what is found, and hiding files here would make that list lie.
/// </para>
/// </remarks>
public static class FolderIndex
{
    /// <summary>
    /// Lists every file under <paramref name="root"/>, deepest paths included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sorted, so the same folder always produces the same list — the file
    /// system's own order is not stable across platforms and this feeds a
    /// ranked, capped list where an unstable order shows up as results moving
    /// between runs.
    /// </para>
    /// <para>
    /// A folder that cannot be walked is a refusal rather than an empty list:
    /// "no files here" and "this is not readable" lead to different next steps,
    /// and a browser showing nothing cannot tell you which happened.
    /// </para>
    /// </remarks>
    public static Result<ImmutableArray<SdfPathEntry>> Paths(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (!Directory.Exists(root))
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"{root} is not a folder."));
        }

        List<string> found = [];
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                found.Add(SdfIndex.NormalizePath(relative));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"{root} could not be read: {exception.Message}"));
        }

        found.Sort(StringComparer.Ordinal);

        ImmutableArray<SdfPathEntry>.Builder entries =
            ImmutableArray.CreateBuilder<SdfPathEntry>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            // NodeOffset addresses a node in the archive's trie and has no
            // meaning here. The ordinal keeps entries distinguishable without
            // pretending to be an offset into something.
            entries.Add(new SdfPathEntry(found[i], i, IsDirectory: false));
        }

        return Result.Ok(entries.MoveToImmutable());
    }
}
