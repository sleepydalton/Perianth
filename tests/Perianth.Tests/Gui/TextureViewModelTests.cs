using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Avalonia.Threading;
using Perianth.Core.Content;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// The texture pane's state around showing a different model.
/// </summary>
/// <remarks>
/// Not a test of the grid, which needs a running window. It pins the invariant
/// the grid depends on: nothing may still be selected once the thumbnails have
/// gone. Avalonia's selection model updates when the collection it is bound to
/// changes, and a second change arriving during that update throws — which is
/// what choosing another file with the Textures tab open used to do.
/// </remarks>
public sealed class TextureViewModelTests
{
    /// <summary>
    /// Runs the work the view model deferred.
    /// </summary>
    /// <remarks>
    /// It empties the grid on a later dispatcher turn rather than inside the
    /// event that asked for it, because touching a bound collection while
    /// Avalonia is mid-update throws. A test has to pump that turn itself.
    /// </remarks>
    private static void Settle() => Dispatcher.UIThread.RunJobs();

    private static CharacterAssets Assets(string name) => new(
        Name: name,
        Model: $"camel/{name}.mmb",
        Cameldata: $"camel/{name}.cameldata",
        Editordata: $"camel/{name}.editordata",
        Setup: null,
        Mouth: null,
        Eyes: null,
        Pupils: null,
        Eyebrows: null,
        Clips: ImmutableArray<ResolvedAsset>.Empty,
        LipsyncDatabase: null,
        Unresolved: ImmutableArray<string>.Empty);

    [Fact]
    public void Showing_another_model_leaves_nothing_selected() => UiThread.Run(() =>
    {
        TextureViewModel model = new();
        model.Show(Assets("chr_a"));

        model.Thumbnails.Add(new TextureThumbnail(
            "tex_a.dds", "camel/tex_a.dds", "1 binding", null, string.Empty));
        model.Selected = model.Thumbnails[0];

        model.Show(Assets("chr_b"));
        Settle();

        Assert.Null(model.Selected);
        Assert.Empty(model.Thumbnails);
    });

    [Fact]
    public void Showing_another_model_replaces_the_grid_rather_than_emptying_it() => UiThread.Run(() =>
    {
        // The guarantee that stops the crash. Emptying an ObservableCollection
        // a ListBox has bound raises a change its selection model must work
        // through, and a second one arriving mid-update throws. Swapping the
        // collection raises no change at all.
        TextureViewModel model = new();
        model.Show(Assets("chr_a"));

        ObservableCollection<TextureThumbnail> first = model.Thumbnails;
        first.Add(new TextureThumbnail("t.dds", "camel/t.dds", "1 binding", null, string.Empty));

        int changes = 0;
        first.CollectionChanged += (_, _) => changes++;

        model.Show(Assets("chr_b"));
        Settle();

        Assert.NotSame(first, model.Thumbnails);
        Assert.Empty(model.Thumbnails);

        // Untouched: no notification was raised on the collection the list was
        // bound to, which is the whole point.
        Assert.Equal(0, changes);
        Assert.Single(first);
    });

    [Fact]
    public void The_grid_is_replaced_before_the_selection_is_dropped() => UiThread.Run(() =>
    {
        // The ordering two failed fixes got wrong. Assigning the selection is
        // what starts a selection update, so it must come after the source has
        // been swapped, not before — otherwise the swap lands inside an update
        // and Avalonia throws.
        TextureViewModel model = new();
        model.Show(Assets("chr_a"));
        Settle();

        model.Thumbnails.Add(new TextureThumbnail("t.dds", "camel/t.dds", "1", null, string.Empty));
        model.Selected = model.Thumbnails[0];

        List<string> order = [];
        void Watch(object? _, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TextureViewModel.Thumbnails) or nameof(TextureViewModel.Selected))
            {
                order.Add(e.PropertyName);
            }
        }

        model.PropertyChanged += Watch;
        model.Show(Assets("chr_b"));
        Settle();
        model.PropertyChanged -= Watch;

        Assert.Equal([nameof(TextureViewModel.Thumbnails), nameof(TextureViewModel.Selected)], order);
    });

    // --- Where a second custom texture is written.

    private static TextureViewModel Aimed(string texturePath)
    {
        TextureViewModel model = new();
        model.Show(Assets("chr_test"));
        model.Thumbnails.Add(new TextureThumbnail("t.dds", texturePath, "2 bindings", null, string.Empty));
        model.Selected = model.Thumbnails[0];
        return model;
    }

    [Fact]
    public void A_second_addition_does_not_reuse_the_first_ones_path() => UiThread.Run(() =>
    {
        // The reported bug, at the point it goes wrong. Add an image for part
        // 47 and the box is filled in with where it went — deliberately, so it
        // is visible. Then name part 51 and add a different image: the box
        // still held the first path, so the second image was written over the
        // first and part 47, already pointed there, silently changed with it.
        TextureViewModel model = Aimed("camel/baked/assets/textures/paper/tex_ashgray_d.dds");

        string first = model.ChoosePath([47]);
        model.RepointTo = first;   // what a successful addition does: show where it went

        string second = model.ChoosePath([51]);

        Assert.NotEqual(first, second);
        Assert.EndsWith("_part_51.dds", second, StringComparison.Ordinal);
    });

    [Fact]
    public void A_path_the_user_typed_is_still_obeyed() => UiThread.Run(() =>
    {
        // The box's promise, and the reason the fix is not simply to clear it:
        // whatever is typed there wins, including on the next addition.
        TextureViewModel model = Aimed("camel/baked/assets/textures/paper/tex_ashgray_d.dds");
        model.RepointTo = "camel/baked/assets/textures/mine/chosen.dds";

        Assert.Equal("camel/baked/assets/textures/mine/chosen.dds", model.ChoosePath([47]));
        Assert.Equal("camel/baked/assets/textures/mine/chosen.dds", model.ChoosePath([51]));
    });

    [Fact]
    public void Redoing_the_same_parts_returns_to_the_same_path() => UiThread.Run(() =>
    {
        // Correcting an image you have just added must land where the first one
        // did, or the parts stay bound to the version you were replacing.
        TextureViewModel model = Aimed("camel/baked/assets/textures/paper/tex_ashgray_d.dds");

        string first = model.ChoosePath([47]);
        model.RepointTo = first;

        Assert.Equal(first, model.ChoosePath([47]));
    });

    [Fact]
    public void Two_aims_at_one_texture_are_told_apart() => UiThread.Run(() =>
    {
        TextureViewModel model = Aimed("camel/baked/assets/textures/paper/tex_ashgray_d.dds");

        Assert.NotEqual(model.Aim([47]), model.Aim([51]));
        Assert.Equal(model.Aim([47, 51]), model.Aim([51, 47]));
        Assert.NotEqual(model.Aim([47]), model.Aim(null));
    });

    [Fact]
    public void The_staged_list_says_what_each_file_is_for() => UiThread.Run(() =>
    {
        // Two additions differ by a few characters near the end of a long path,
        // which is the pair somebody actually needs to tell apart. The parts
        // come first because they are what differs.
        TextureViewModel model = Aimed("camel/baked/assets/textures/paper/tex_ashgray_d.dds");

        Assert.Equal("new — for part 47", model.Purpose([47]));
        Assert.Equal("new — for parts 47, 51", model.Purpose([51, 47]));
        Assert.Equal("new — for every part using t.dds", model.Purpose(null));
    });

    [Fact]
    public void Nothing_staged_overlays_nothing()
    {
        // The export asks unconditionally, so the quiet answer has to be right.
        TextureViewModel model = new();
        string folder = Directory.CreateTempSubdirectory("perianth-overlay-").FullName;

        try
        {
            Result<int> laid = model.OverlayInto(folder);

            Assert.False(laid.IsRefused);
            Assert.Equal(0, laid.Value);
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // --- Work a later selection superseded.

    [Fact]
    public async Task A_cancelled_decode_is_an_outcome_and_not_a_fault()
    {
        // Choosing another model cancels the decode of the one before, and a
        // cancelled Task.Run completes by throwing. The callers are void event
        // handlers, so an uncaught one ends the process — which is what
        // selecting two models quickly did.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await TextureViewModel.Superseded(Task.Run(() => { }, cancelled.Token));
    }

    [Fact]
    public async Task A_real_fault_still_surfaces()
    {
        // Swallowing cancellation must not become swallowing everything: a
        // decode that failed for a reason is a thing the pane has to report.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => TextureViewModel.Superseded(Task.FromException(new InvalidOperationException("x"))));
    }

    // --- Aiming an edit at named parts.

    [Theory]
    [InlineData("", "Give this model its own copy, from my image…")]
    [InlineData("47", "Paint part 47 with my image…")]
    [InlineData("47, 51", "Paint parts 47, 51 with my image…")]
    public void The_add_button_says_what_it_will_change(string parts, string expected)
    {
        // The two operations on this pane produce the same-looking result and
        // differ only in who else is affected, so the label has to carry it.
        TextureViewModel model = new() { Parts = parts };

        Assert.Equal(expected, model.AddButtonText);
    }

    [Theory]
    [InlineData("", "Every part painted with the selected texture.")]
    [InlineData("47", "Only part 47.")]
    [InlineData(" 47 , 51 ", "Only parts 47, 51.")]
    [InlineData("47; 51", "Not a list of part numbers — separate them with commas.")]
    [InlineData("the hat", "Not a list of part numbers — separate them with commas.")]
    public void The_parts_box_says_what_it_will_do(string typed, string expected)
    {
        // It has to be readable before the edit runs, because the alternative
        // to understanding it is repainting a whole model by accident.
        TextureViewModel model = new() { Parts = typed };

        Assert.Equal(expected, model.PartsNote);
    }

    // --- Edits surviving a write, so a second custom texture is possible.

    private const string Paper = @"camel\baked\assets\textures\tex_kraft_d.dds";
    private const string Other = @"camel\baked\assets\textures\tex_cranberry_d.dds";
    private const string MineA = "camel/baked/assets/textures/perianth/a.dds";
    private const string MineB = "camel/baked/assets/textures/perianth/b.dds";

    private static EditordataFile TwoParts() => new(
        "chr_test.editordata",
        [Section(0, Paper), Section(1, Other)],
        CustomVersion: 3);

    private static EditordataSection Section(int ordinal, string diffuse) => new(
        ordinal,
        [new EditordataMaterial(
            $"mat{ordinal}", "CamelDefaultShader", [new EditordataChannel("DiffuseColor", diffuse)])],
        "intermediate",
        [.. new byte[12]],
        []);

    private static string DiffuseOf(EditordataFile file, int section) =>
        file.Sections[section].Materials[0].Channels[0].TexturePath;

    [Fact]
    public async Task A_second_custom_texture_builds_on_the_first_rather_than_replacing_it()
    {
        // The fault: writing a mod used the same reset as discarding one, so the
        // model went back to what the archives hold. The next edit started from
        // there, and the next write replaced the mod's editordata with one that
        // had never carried the first repoint. Both images sat in the folder,
        // one was bound, and nothing said so.
        //
        // Driven through WriteModAsync rather than the reset it calls, because
        // the defect was the wiring: a first version of this test called that
        // reset directly, passed, and went on passing with the fault put back.
        string root = Directory.CreateTempSubdirectory("perianth-second-").FullName;

        try
        {
            TextureViewModel model = new();
            model.Load(TwoParts(), "camel/chr_test.editordata");

            model.Apply(MaterialEdit.Repoint(model.Current!, Paper, MineA), _ => "first");
            await model.WriteModAsync(root);

            model.Apply(MaterialEdit.Repoint(model.Current!, Other, MineB), _ => "second");

            Assert.Equal(MineA.Replace('/', '\\'), DiffuseOf(model.Current!, 0));
            Assert.Equal(MineB.Replace('/', '\\'), DiffuseOf(model.Current!, 1));

            // And what a second write would put in the folder is one editordata
            // carrying both, which is what the loader reads.
            Assert.Single(model.Replacing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discarding_still_returns_the_model_to_what_the_archives_hold()
    {
        // The other half of the same seam. Discard must undo the edits, or the
        // next one builds on changes the user has just thrown away.
        TextureViewModel model = new();
        model.Load(TwoParts(), "camel/chr_test.editordata");

        model.Apply(MaterialEdit.Repoint(model.Current!, Paper, MineA), _ => "first");
        model.Forget();

        Assert.Equal(Paper, DiffuseOf(model.Current!, 0));
        Assert.Empty(model.Replacing);
    }

    [Fact]
    public void A_swatch_says_which_colour_is_already_in_use()
    {
        // Colour alone is a poor way to find one entry among eighty, and the one
        // already in use is what a person looks for first.
        PaperSwatch cobalt = new("cobalt", "camel/tex_cobalt_x_d.dds", 0x34, 0x37, 0x53);
        PaperSwatch moss = new("moss", "camel/tex_moss_x_d.dds", 0x2E, 0x7D, 0x32);

        SwatchChoice current = new(cobalt, current: true);
        SwatchChoice other = new(moss, current: false);

        Assert.Contains("in use now", current.Label, System.StringComparison.Ordinal);
        Assert.Equal("moss", other.Label);
        Assert.True(current.Current);
        Assert.False(other.Current);
    }

    [Fact]
    public void A_colour_row_says_how_much_of_the_piece_it_covers()
    {
        // The complaint this answers: a row named "cobalt" tells you nothing
        // about what it paints. A share does — the big one is the garment.
        PaperSwatch cobalt = new("cobalt", "camel/tex_cobalt_x_d.dds", 0x34, 0x37, 0x53);

        CostumeColour garment = new("camel/tex_cobalt_x_d.dds", 244, cobalt) { Total = 300 };
        CostumeColour trim = new("camel/tex_cobalt_x_d.dds", 40, cobalt) { Total = 300 };
        CostumeColour scrap = new("camel/tex_cobalt_x_d.dds", 1, cobalt) { Total = 300 };

        Assert.Contains("most of it", garment.Label, System.StringComparison.Ordinal);
        Assert.Contains("40 parts", trim.Label, System.StringComparison.Ordinal);
        Assert.Contains("one part", scrap.Label, System.StringComparison.Ordinal);
    }
}
