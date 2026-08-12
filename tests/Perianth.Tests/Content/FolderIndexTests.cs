using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Listing a folder as the virtual paths the archives would spell.
/// </summary>
public sealed class FolderIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-folder-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Lists_files_at_every_depth_as_paths_relative_to_the_root()
    {
        Write("camel/baked/model.mmb");
        Write("camel/loose.txt");
        Write("top.dds");

        ImmutableArray<SdfPathEntry> paths = FolderIndex.Paths(_root).Value;

        Assert.Equal(
            ["camel/baked/model.mmb", "camel/loose.txt", "top.dds"],
            paths.Select(e => e.Path));
    }

    [Fact]
    public void Produces_the_same_list_every_time()
    {
        // The file system's order is not stable across platforms, and this feeds
        // a ranked, capped list. An unstable order shows up to a user as results
        // moving between runs of the same search.
        foreach (string name in new[] { "b.mmb", "a.mmb", "c/d.mmb", "c/a.mmb" })
        {
            Write(name);
        }

        ImmutableArray<SdfPathEntry> first = FolderIndex.Paths(_root).Value;
        ImmutableArray<SdfPathEntry> second = FolderIndex.Paths(_root).Value;

        Assert.Equal(first.Select(e => e.Path), second.Select(e => e.Path));
        Assert.Equal(["a.mmb", "b.mmb", "c/a.mmb", "c/d.mmb"], first.Select(e => e.Path));
    }

    [Fact]
    public void Keeps_every_file_rather_than_only_the_ones_with_readers()
    {
        // The browser builds its type list from what is here, so filtering would
        // make that list claim the folder holds nothing it in fact holds.
        Write("a.mmb");
        Write("notes.txt");
        Write("unknown.whatever");

        ImmutableArray<SdfPathEntry> paths = FolderIndex.Paths(_root).Value;

        Assert.Equal(3, paths.Length);
    }

    [Fact]
    public void An_empty_folder_lists_nothing_and_is_not_a_refusal()
    {
        Result<ImmutableArray<SdfPathEntry>> listed = FolderIndex.Paths(_root);

        Assert.True(listed.IsSuccess);
        Assert.Empty(listed.Value);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        // "Nothing here" and "this is not a folder" lead to different next
        // steps, and a list showing nothing cannot say which happened.
        Result<ImmutableArray<SdfPathEntry>> listed =
            FolderIndex.Paths(Path.Combine(_root, "no-such-folder"));

        Assert.False(listed.IsSuccess);
        Assert.Equal(RefusalKind.Resource, listed.Refusal!.Kind);
    }

    [Fact]
    public void Paths_are_spelled_the_way_the_archives_spell_them()
    {
        // Same normalization as the archive index, so one path list serves both
        // and nothing downstream has to know which it is looking at.
        Write("Camel/Baked/Model.MMB");

        ImmutableArray<SdfPathEntry> paths = FolderIndex.Paths(_root).Value;

        Assert.Equal(SdfIndex.NormalizePath("Camel/Baked/Model.MMB"), paths[0].Path);
    }

    private void Write(string relative)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }
}
