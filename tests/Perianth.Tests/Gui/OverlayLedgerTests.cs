using System;
using System.IO;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// The preview folder gives back what a pane put into it.
/// </summary>
/// <remarks>
/// <para>
/// Reported from use, twice, in different clothes. A costume recolour outlived
/// its selection and kept recolouring; then a texture edit outlived the model it
/// was made for and kept repainting, because that pane wrote into the preview
/// folder and never took anything back out. The folder is a content root an
/// export reads and it survives the session, so anything left there is applied
/// silently to every later export.
/// </para>
/// <para>
/// Three panes now share one ledger rather than three copies of it, and these
/// tests are about the mechanism rather than any one pane.
/// </para>
/// </remarks>
public sealed class OverlayLedgerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-overlay-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_texture_pane_takes_back_what_it_laid_down()
    {
        // The reported fault, at the point it goes wrong: a file the pane wrote
        // for one model, and a later overlay for a different one. Without the
        // withdrawal the first is still there, still being exported.
        string stale = Write("camel/assets/chr_one.editordata");
        File.WriteAllText(Path.Combine(_root, "perianth-texture-files.txt"), "camel/assets/chr_one.editordata\n");

        TextureViewModel pane = new();
        Result<int> laid = pane.OverlayInto(_root);

        Assert.True(laid.IsSuccess, laid.IsSuccess ? string.Empty : laid.Refusal!.Message);
        Assert.False(File.Exists(stale), "an edit from a previous session is still overlaying the export");
    }

    [Fact]
    public void Choosing_another_model_ends_the_texture_edits_made_for_the_last_one()
    {
        // Reported twice. Clearing on the next overlay was not enough: between
        // choosing another model and exporting again, the previous model's
        // colours were still being applied out of a folder the export reads.
        string laid = Write("camel/assets/chr_one.editordata");
        File.WriteAllText(
            Path.Combine(_root, "perianth-texture-files.txt"), "camel/assets/chr_one.editordata\n");

        // A fresh pane has nothing staged, so a file left in the folder is a
        // ghost whichever model is shown: the pane's memory is what is
        // authoritative, and it is empty.
        UiThread.Run(() =>
        {
            TextureViewModel pane = new();
            pane.UseWorkingFolder(_root);
            pane.Show(Assets("chr_two"));
        });

        Assert.False(File.Exists(laid), "the previous model's material edit is still in the preview folder");
    }

    private static CharacterAssets Assets(string name) => new(
        Name: name,
        Model: $"camel/assets/{name}.mmb",
        Cameldata: $"camel/assets/{name}.cameldata",
        Editordata: $"camel/assets/{name}.editordata",
        Setup: null,
        Mouth: null,
        Eyes: null,
        Pupils: null,
        Eyebrows: null,
        Clips: ImmutableArray<ResolvedAsset>.Empty,
        LipsyncDatabase: null,
        Unresolved: ImmutableArray<string>.Empty);

    [Fact]
    public void A_file_the_panes_did_not_write_survives()
    {
        // The folder is shared with hand-authored files, so "remove the
        // editordata" is not a safe rule. Only what was recorded is removed.
        string mine = Write("camel/assets/by_hand.editordata");

        Assert.True(new TextureViewModel().OverlayInto(_root).IsSuccess);
        Assert.True(new CostumeViewModel().OverlayInto(_root).IsSuccess);
        Assert.True(new ShapeViewModel().OverlayInto(_root).IsSuccess);

        Assert.True(File.Exists(mine));
    }

    [Fact]
    public void An_empty_overlay_leaves_no_ledger_behind()
    {
        Assert.True(new ShapeViewModel().OverlayInto(_root).IsSuccess);

        Assert.Empty(Directory.GetFiles(_root, "perianth-*.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public void Each_pane_keeps_its_own_ledger_so_one_does_not_withdraw_anothers_files()
    {
        // They write into one folder. If they shared a ledger, whichever ran
        // second would delete what the first had just laid down.
        string texture = Write("camel/assets/from_texture.editordata");
        string costume = Write("camel/assets/from_costume.editordata");
        File.WriteAllText(Path.Combine(_root, "perianth-texture-files.txt"), "camel/assets/from_texture.editordata\n");
        File.WriteAllText(Path.Combine(_root, "perianth-costume-colours.txt"), "camel/assets/from_costume.editordata\n");

        Assert.True(new TextureViewModel().OverlayInto(_root).IsSuccess);

        Assert.False(File.Exists(texture));
        Assert.True(File.Exists(costume), "the texture pane removed a file the costume pane wrote");
    }

    private string Write(string virtualPath)
    {
        string path = Path.Combine(_root, virtualPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }
}
