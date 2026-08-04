using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Gui;
using Perianth.Pipeline;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// What the export pane asks the pipeline for.
/// </summary>
/// <remarks>
/// The pane's settings and the request they compose had no test between them,
/// and it showed: a prop with animations still exported as its complete part
/// list because posing was gated on a setup ANIM, which no prop in the archive
/// has. Choosing an animation appeared to do nothing.
/// </remarks>
public sealed class ExportViewModelTests
{
    private const string Idle = "camel/animation/anm_sign_idle_intact.anim";
    private const string Destroy = "camel/animation/anm_sign_idle_destroy.anim";
    private const string Setup = "camel/animation/anm_cartman_setup.anim";

    private static CharacterAssets Assets(ResolvedAsset? setup, params string[] clips) => new(
        Name: "model",
        Model: "camel/model.mmb",
        Cameldata: "camel/model.cameldata",
        Editordata: "camel/model.editordata",
        Setup: setup,
        Mouth: null,
        Eyes: null,
        Pupils: null,
        Eyebrows: null,
        Clips: [.. clips.Select(path => new ResolvedAsset(path, AssetMatch.Exact))],
        LipsyncDatabase: null,
        Unresolved: ImmutableArray<string>.Empty);

    private static ExportViewModel Pane(CharacterAssets assets, string? chosen)
    {
        ExportViewModel pane = new();
        pane.Show(assets);
        pane.Clip = chosen is null ? null : new ClipChoice("chosen", chosen);
        return pane;
    }

    private static bool Names(string? local, string virtualPath) =>
        local is not null && local.Replace('\\', '/').EndsWith(virtualPath, System.StringComparison.Ordinal);

    [Fact]
    public void A_prop_is_posed_by_the_animation_chosen_for_it()
    {
        // No prop in the archive has a setup ANIM — the convention is a
        // character one — so the chosen animation is what poses it, exactly as
        // --setup-anim does on the command line.
        ExportRequest request = Pane(Assets(setup: null, Idle, Destroy), Idle).Compose("/w");

        Assert.True(Names(request.SetupAnim, Idle));
        Assert.False(request.AllowUnposed);

        // Not also passed as a clip: that would ask for a clip against itself.
        Assert.Null(request.ClipAnim);
    }

    [Fact]
    public void A_prop_with_no_animation_chosen_is_still_the_complete_part_list()
    {
        ExportRequest request = Pane(Assets(setup: null, Idle, Destroy), chosen: null).Compose("/w");

        Assert.Null(request.SetupAnim);
        Assert.True(request.AllowUnposed);
    }

    [Fact]
    public void A_character_keeps_its_setup_and_takes_the_choice_as_a_clip()
    {
        // The behaviour that already worked, pinned so making props work did
        // not quietly change it: a model with its own setup is posed by that,
        // and the chosen animation plays against it.
        ExportRequest request = Pane(
            Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle), Idle).Compose("/w");

        Assert.True(Names(request.SetupAnim, Setup));
        Assert.True(Names(request.ClipAnim, Idle));
        Assert.False(request.AllowUnposed);
    }

    [Fact]
    public void Turning_the_pose_off_returns_the_complete_part_list()
    {
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.Pose = false;

        ExportRequest request = pane.Compose("/w");

        Assert.Null(request.SetupAnim);
        Assert.True(request.AllowUnposed);
    }

    [Fact]
    public void The_pane_says_what_will_pose_it()
    {
        // The checkbox said "Pose with the setup ANIM" to a prop that has none,
        // which is why choosing an animation looked like it did nothing.
        ExportViewModel prop = Pane(Assets(setup: null, Idle), chosen: null);
        Assert.Equal("Pose with the chosen animation", prop.PoseLabel);
        Assert.Contains("the Animation below is what poses it", prop.PoseNote, System.StringComparison.Ordinal);

        prop.Clip = new ClipChoice("chosen", Idle);
        Assert.Equal("Posed by the animation chosen below.", prop.PoseNote);

        ExportViewModel character = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact)), chosen: null);
        Assert.Equal("Pose with the setup ANIM", character.PoseLabel);
    }

    // --- What an export will apply on top of the game's files.

    [Fact]
    public void With_nothing_to_apply_it_says_so_rather_than_doing_nothing_quietly()
    {
        // The whole reason this looked broken: writing a mod clears the
        // Textures tab, so the natural order of edit, write, look at it left
        // the checkbox with nothing to do and no way to tell.
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.StagedCount = () => 0;

        Assert.Contains("Nothing to apply", pane.ChangesNote, System.StringComparison.Ordinal);
        Assert.Contains("choose that mod folder here", pane.ChangesNote, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Unsaved_edits_are_counted()
    {
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.StagedCount = () => 1;

        Assert.Equal("Using 1 unsaved edit from the Textures tab.", pane.ChangesNote);
    }

    [Fact]
    public void A_mod_folder_and_unsaved_edits_are_both_reported()
    {
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.StagedCount = () => 2;
        pane.ModFolder = "/mods/Mine";

        Assert.Contains("Using that mod, plus 2 unsaved edits", pane.ChangesNote, System.StringComparison.Ordinal);
        Assert.Equal("Mod: Mine", pane.ModFolderLabel);
    }

    [Fact]
    public void Turning_the_changes_off_leaves_nothing_to_say()
    {
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.StagedCount = () => 3;
        pane.IncludeStagedChanges = false;

        Assert.Equal(string.Empty, pane.ChangesNote);
    }

    [Fact]
    public void A_mod_folder_is_laid_over_the_extracted_files()
    {
        // The bug this pins: there are two export paths, the ordinary one and
        // the loop that walks a facial system's states, and the first version
        // of the overlay lived only in the loop. So the Export button applied
        // nothing at all and said nothing about it.
        string working = Directory.CreateTempSubdirectory("perianth-overlay-").FullName;

        try
        {
            string mod = Path.Combine(working, "mod");
            string texture = Path.Combine(mod, "camel", "baked", "tex.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(texture)!);
            File.WriteAllBytes(texture, [1, 2, 3]);
            File.WriteAllText(Path.Combine(mod, "manifest.ini"), "name=x");

            ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
            pane.ModFolder = mod;

            Assert.True(pane.ApplyOwnFiles(working));

            string landed = Path.Combine(working, "extracted", "camel", "baked", "tex.dds");
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(landed));

            // manifest.ini stands at no archive path, so it is the loader's and
            // does not belong in a tree of the game's own files.
            Assert.False(File.Exists(Path.Combine(working, "extracted", "manifest.ini")));
        }
        finally
        {
            Directory.Delete(working, recursive: true);
        }
    }

    [Fact]
    public void Applying_nothing_when_the_box_is_off_leaves_the_extract_alone()
    {
        string working = Directory.CreateTempSubdirectory("perianth-overlay-").FullName;

        try
        {
            ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
            pane.ModFolder = Path.Combine(working, "absent");
            pane.IncludeStagedChanges = false;

            Assert.True(pane.ApplyOwnFiles(working));
            Assert.False(Directory.Exists(Path.Combine(working, "extracted")));
        }
        finally
        {
            Directory.Delete(working, recursive: true);
        }
    }

    [Fact]
    public void A_mod_folder_that_is_not_there_stops_the_export()
    {
        // Silently exporting the game's own textures when the user named a mod
        // is the failure this whole feature exists to avoid.
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.ModFolder = "/no/such/mod";

        Assert.False(pane.ApplyOwnFiles(Directory.CreateTempSubdirectory("perianth-x-").FullName));
    }
}
