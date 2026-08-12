using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// The browse pane, over a folder — which is the one source a test can make.
/// </summary>
public sealed class BrowseViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-browse-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Choosing_a_file_type_lists_it_without_anything_being_typed()
    {
        // Reported from the window: picking a type showed nothing until a letter
        // was typed, which makes the type look broken and the folder look empty.
        Write("camel/a/one.mmb");
        Write("camel/a/two.mmb");
        Write("camel/b/skin.dds");

        BrowseViewModel pane = new();
        await pane.OpenFolderAsync(_root);

        pane.FileType = ".mmb";
        await Settle(pane);

        Assert.Equal(["camel/a/one.mmb", "camel/a/two.mmb"], pane.Results);
    }

    [Fact]
    public async Task Opening_a_source_lists_it_before_anything_is_asked()
    {
        // Reported from the window: nothing appeared until a letter was typed,
        // which over a folder somebody has just opened reads as "this folder is
        // empty". The archives are too many to list usefully, but the cap and
        // the status line already handle that, and an empty pane handles it
        // worse.
        Write("camel/a/one.mmb");
        Write("camel/b/skin.dds");

        BrowseViewModel pane = new();
        await pane.OpenFolderAsync(_root);
        await Settle(pane);

        Assert.Equal(["camel/a/one.mmb", "camel/b/skin.dds"], pane.Results);
    }

    [Fact]
    public async Task Clearing_the_type_goes_back_to_the_whole_listing()
    {
        Write("camel/a/one.mmb");
        Write("camel/b/skin.dds");

        BrowseViewModel pane = new();
        await pane.OpenFolderAsync(_root);

        pane.FileType = ".mmb";
        await Settle(pane);
        Assert.Equal(["camel/a/one.mmb"], pane.Results);

        pane.FileType = BrowseViewModel.AnyType;
        await Settle(pane);
        Assert.Equal(2, pane.Results.Count);
    }

    [Fact]
    public async Task A_type_and_text_narrow_together()
    {
        Write("camel/a/hero.mmb");
        Write("camel/a/villain.mmb");
        Write("camel/a/hero.dds");

        BrowseViewModel pane = new();
        await pane.OpenFolderAsync(_root);

        pane.FileType = ".mmb";
        pane.Search = "hero";
        await Settle(pane);

        Assert.Equal(["camel/a/hero.mmb"], pane.Results);
    }

    /// <summary>
    /// Waits for the debounce and the background search the pane starts and
    /// does not await.
    /// </summary>
    private static async Task Settle(BrowseViewModel pane)
    {
        for (int i = 0; i < 100 && pane.Busy; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(400);
    }

    private void Write(string relative)
    {
        string full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "x");
    }
}
