using System;
using System.IO;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// The costume pane's recolours, and their lifetime in the shared overlay.
/// </summary>
/// <remarks>
/// A colour here is derived from what is selected, not authored, so it must not
/// outlive the selection. It did: a costume recoloured one evening was still
/// recolouring it the next day, in every export, with nothing on screen saying
/// why. The overlay is shared with hand-authored files, so what was written has
/// to be recorded rather than worked out from the paths.
/// </remarks>
public sealed class CostumeOverlayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-costume-overlay-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void A_recolour_from_a_previous_export_is_taken_back_out()
    {
        // What the pane wrote last time, as it would have left it.
        string worn = Path.Combine(_root, "camel", "equipment", "suit.editordata");
        Directory.CreateDirectory(Path.GetDirectoryName(worn)!);
        File.WriteAllBytes(worn, [1, 2, 3]);
        File.WriteAllText(Path.Combine(_root, "perianth-costume-colours.txt"),
            "camel/equipment/suit.editordata\n");

        // Nothing is worn now, so the overlay must come back to nothing.
        CostumeViewModel pane = new();
        Result<int> laid = pane.OverlayInto(_root);

        Assert.True(laid.IsSuccess, laid.IsSuccess ? "" : laid.Refusal!.Message);
        Assert.False(File.Exists(worn), "yesterday's colour is still being applied");
        Assert.False(File.Exists(Path.Combine(_root, "perianth-costume-colours.txt")));
    }

    [Fact]
    public void A_file_the_pane_did_not_write_is_left_alone()
    {
        // The overlay is shared with texture and material authoring, which is
        // somebody's work rather than a selection. Deleting by a rule about
        // paths would eventually take one of those with it.
        string authored = Path.Combine(_root, "camel", "textures", "mine.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        File.WriteAllBytes(authored, [4, 5, 6]);
        File.WriteAllText(Path.Combine(_root, "perianth-costume-colours.txt"),
            "camel/equipment/suit.editordata\n");

        CostumeViewModel pane = new();
        Assert.True(pane.OverlayInto(_root).IsSuccess);

        Assert.True(File.Exists(authored), "an authored file was deleted");
    }

    [Fact]
    public void No_record_of_a_previous_export_leaves_the_overlay_untouched()
    {
        // The ordinary first run, and every run of the texture pane alone.
        string authored = Path.Combine(_root, "camel", "textures", "mine.dds");
        Directory.CreateDirectory(Path.GetDirectoryName(authored)!);
        File.WriteAllBytes(authored, [7]);

        CostumeViewModel pane = new();
        Assert.True(pane.OverlayInto(_root).IsSuccess);

        Assert.True(File.Exists(authored));
    }
}
