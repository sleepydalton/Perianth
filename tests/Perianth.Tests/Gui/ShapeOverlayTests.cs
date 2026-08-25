using System;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// The shape pane's preview overlay, and its lifetime.
/// </summary>
/// <remarks>
/// A reshape put into the preview folder is derived from a file the user loaded,
/// so it must not outlive the loading. The costume pane learned this the
/// expensive way — a recolour from one evening was still applying the next day,
/// in every export, with nothing on screen saying why — and a reshape is the same
/// shape of hazard with a louder failure, since it moves geometry rather than
/// colour. What was written is recorded rather than worked out from the paths,
/// because the folder is shared with hand-authored files.
/// </remarks>
public sealed class ShapeOverlayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-shape-overlay-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_reshape_from_a_previous_export_is_taken_back_out()
    {
        // What the pane wrote last time, as it would have left it.
        string stale = Path.Combine(_root, "camel", "assets", "chr_test.cameldata");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllBytes(stale, [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(_root, "perianth-shape-files.txt"),
            "camel/assets/chr_test.cameldata\n");

        // Nothing is loaded now, so the overlay must come back to nothing.
        ShapeViewModel pane = new();
        Result<int> laid = pane.OverlayInto(_root);

        Assert.True(laid.IsSuccess, laid.IsSuccess ? string.Empty : laid.Refusal!.Message);
        Assert.Equal(0, laid.Value);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(Path.Combine(_root, "perianth-shape-files.txt")));
    }

    [Fact]
    public void A_file_the_pane_did_not_write_is_left_alone()
    {
        // The overlay folder is shared with the texture and costume panes and
        // with whatever the author put there by hand. Withdrawing removes what
        // this pane recorded and nothing else.
        string mine = Path.Combine(_root, "camel", "assets", "hand_written.editordata");
        Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
        File.WriteAllBytes(mine, [9, 9, 9]);

        ShapeViewModel pane = new();
        Assert.True(pane.OverlayInto(_root).IsSuccess);

        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void An_overlay_with_nothing_loaded_writes_nothing()
    {
        ShapeViewModel pane = new();

        Result<int> laid = pane.OverlayInto(_root);

        Assert.True(laid.IsSuccess);
        Assert.Equal(0, laid.Value);
        Assert.Empty(Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Choosing_another_model_takes_the_previous_reshape_out_of_the_folder()
    {
        // What was reported: a reshape made for one character was still being
        // applied after moving on to another. Clearing it in memory is not
        // enough -- the file it wrote sits in a folder the export reads, so
        // until it is removed the tool looks like it is remembering edits
        // nobody asked it to keep.
        string laid = Path.Combine(_root, "camel", "assets", "chr_one.cameldata");
        Directory.CreateDirectory(Path.GetDirectoryName(laid)!);
        File.WriteAllBytes(laid, [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(_root, "perianth-shape-files.txt"), "camel/assets/chr_one.cameldata\n");

        ShapeViewModel pane = new();
        pane.UseWorkingFolder(_root);
        pane.Discard();

        Assert.False(File.Exists(laid), "the previous model's reshape is still in the preview folder");
    }

    [Fact]
    public void Nothing_else_is_gathered_for_a_mod_that_will_not_be_written()
    {
        // A reshape and a repaint are one piece of work. Writing the geometry
        // alone leaves a mod that installs and draws the model with its original
        // art, which reads as the texture edits having been lost.
        bool asked = false;
        ShapeViewModel pane = new()
        {
            AlsoStaged = () =>
            {
                asked = true;
                return [];
            },
        };

        // The refusal comes first, so nothing is collected for a mod that is not
        // going to exist.
        //
        // What this does NOT cover is the staged files reaching a mod that IS
        // written: that needs a loaded reshape, which needs a real model behind
        // it, and it is checked by hand rather than here.
        Assert.True(pane.Save(_root, "probe", "me").IsRefused);
        Assert.False(asked);
    }

    [Fact]
    public void Saving_with_nothing_loaded_refuses_rather_than_writing_an_empty_mod()
    {
        ShapeViewModel pane = new();

        Result<Perianth.Core.Content.ModOutcome> saved = pane.Save(_root, "probe", "me");

        Assert.True(saved.IsRefused);
        Assert.Contains("no edit to save", saved.Refusal.Message, StringComparison.Ordinal);
    }
}
