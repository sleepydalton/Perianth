using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Perianth.Core.Content;
using Perianth.Core.Pose;
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

    [Fact]
    public void Export_reads_the_games_files_from_the_archives_and_writes_none_of_them()
    {
        // Someone who asked for a model did not ask for a copy of the game, and
        // used to get one: exporting wrote every file the model could want --
        // 49 for one character, 303 for another -- because the pipeline could
        // only open files on disk. It reads archive paths now, so the export
        // writes the export.
        CharacterAssets assets = Assets(
            new ResolvedAsset(Setup, AssetMatch.Exact), Idle, Destroy);

        ExportRequest request = Pane(assets, Idle).Compose("/w");

        Assert.True(request.ReadFromArchives);
        Assert.Equal(assets.Model, request.Mmb);
        Assert.Equal(Setup, request.SetupAnim);
        Assert.Equal(Idle, Assert.Single(request.ClipAnims));

        // Nothing names the working folder except the file being written.
        Assert.DoesNotContain("/w", request.Mmb, StringComparison.Ordinal);
        Assert.DoesNotContain("/w", request.SetupAnim!, StringComparison.Ordinal);
        Assert.StartsWith("/w", request.Out, StringComparison.Ordinal);
    }

    [Fact]
    public void A_content_root_is_named_only_when_the_folder_is_really_there()
    {
        // The bug this pins, reported straight after the change that caused it:
        // the export refused with "<working>/my-files is not a directory", a
        // path the user never typed. The checkbox that lays the user's own files
        // over the game's is on by default and the pane always has somewhere to
        // get them from, so the folder was named whether or not anything had
        // been written into it -- and with nothing staged, nothing had.
        //
        // Existence is the question, not intent. ApplyOwnFiles runs first and
        // creates the folder only when something lands in it.
        string working = Directory.CreateTempSubdirectory("perianth-root-").FullName;

        try
        {
            ExportViewModel pane = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle), Idle);
            Assert.Null(pane.Compose(working).ContentRoot);

            Directory.CreateDirectory(Path.Combine(working, ExportViewModel.OverridesFolder));
            Assert.NotNull(pane.Compose(working).ContentRoot);
        }
        finally
        {
            Directory.Delete(working, recursive: true);
        }
    }

    [Fact]
    public void A_borrowed_hierarchy_poses_the_model_and_the_clips_stay_clips()
    {
        // A model with no setup is normally posed by the animation chosen for it,
        // which is right for a prop. Where a hierarchy has been borrowed instead,
        // that one poses it and every chosen animation stays an animation --
        // otherwise choosing a donor would silently cost the user their first
        // clip.
        ExportViewModel pane = Pane(Assets(setup: null, Idle, Destroy), Idle);
        pane.PrimaryDonor = new DonorChoice(
            new DonorCandidate("camel/anim/anm_relative_setup.anim", 90, 46, 0, null), isGapFiller: false);

        ExportRequest request = pane.Compose("/w");

        Assert.True(Names(request.SetupAnim, "anm_relative_setup.anim"));
        Assert.True(Names(request.ClipAnim, Idle));
        Assert.True(request.AllowMissingParts);
        Assert.False(request.AllowUnposed);
    }

    [Fact]
    public void A_gap_filler_is_only_asked_for_alongside_a_borrowed_hierarchy()
    {
        // It fills what the pose cannot name, so without one there is nothing for
        // it to fill and naming it would be a request the pipeline refuses.
        ExportViewModel pane = Pane(Assets(setup: null, Idle), Idle);
        pane.GapDonor = new DonorChoice(
            new DonorCandidate("camel/anim/anm_other_setup.anim", 40, 30, 8, 0.0), isGapFiller: true);

        Assert.Null(pane.Compose("/w").GapAnim);

        pane.PrimaryDonor = new DonorChoice(
            new DonorCandidate("camel/anim/anm_relative_setup.anim", 90, 46, 0, null), isGapFiller: false);
        pane.GapDonor = new DonorChoice(
            new DonorCandidate("camel/anim/anm_other_setup.anim", 40, 30, 8, 0.0), isGapFiller: true);

        Assert.True(Names(pane.Compose("/w").GapAnim, "anm_other_setup.anim"));
    }

    [Fact]
    public void A_disagreeing_gap_filler_says_so()
    {
        // The ranking puts it last; this is for someone who picked it anyway. The
        // export comes apart and nothing else would say so until they opened it.
        DonorChoice agrees = new(
            new DonorCandidate("camel/anim/anm_near_setup.anim", 40, 30, 8, 0.0), isGapFiller: true);
        DonorChoice apart = new(
            new DonorCandidate("camel/anim/anm_crowd_setup.anim", 54, 77, 12, 10.2), isGapFiller: true);

        Assert.False(agrees.HasWarning);
        Assert.True(apart.HasWarning);
        Assert.Contains("10.2", apart.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hierarchy_the_game_names_says_so_without_losing_its_warning()
    {
        // Both halves matter together. Saying "the game names this one" is the
        // difference between a guess that scored well and a recorded fact — and
        // it must not read as an endorsement, because the record has been
        // measured and is sometimes a developer's placeholder pointing at
        // another character's hierarchy.
        DonorChoice named = new(
            new DonorCandidate("camel/anim/anm_relative_setup.anim", 90, 46, 0, 0.0, Declared: true),
            isGapFiller: false);
        DonorChoice namedButWrong = new(
            new DonorCandidate("camel/anim/anm_placeholder_setup.anim", 90, 46, 0, 10.2, Declared: true),
            isGapFiller: false);
        DonorChoice found = new(
            new DonorCandidate("camel/anim/anm_other_setup.anim", 90, 46, 0, 0.0), isGapFiller: false);

        Assert.Contains("the game names this one", named.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("the game names this one", found.Detail, StringComparison.Ordinal);
        Assert.False(named.HasWarning);
        Assert.True(namedButWrong.HasWarning);
    }

    [Fact]
    public void A_loose_folder_is_exported_from_instead_of_the_archives()
    {
        // Browsing a folder means exporting what is in it. Leaving the archives
        // switched on underneath would quietly fill a mod folder's gaps with the
        // game's own files, which is the one thing somebody checking their mod
        // is trying to find out.
        CharacterAssets assets = Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle);

        ExportViewModel pane = new();
        pane.UseFolder("/my-files", []);
        pane.Show(assets);

        ExportRequest request = pane.Compose("/w");

        Assert.Equal("/my-files", request.ContentRoot);
        Assert.Null(request.SdfRoot);

        // Still on, and the name is the trap. It means "resolve these paths"
        // rather than "look in the archives": with no SdfRoot behind it, the
        // folder is the only place resolution can reach. Off, the model path is
        // opened as a literal file and every export from a folder refuses.
        Assert.True(request.ReadFromArchives);
    }

    [Fact]
    public void Filling_the_gaps_is_asked_for_rather_than_assumed()
    {
        // A mod folder holds only what was changed, so without this there is
        // nothing to export from. With it on by default, a mod naming a texture
        // that does not exist exports perfectly here and draws nothing in the
        // game -- which is the one thing exporting it was meant to catch.
        CharacterAssets assets = Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle);

        ExportViewModel pane = new();
        pane.UseArchives("/archives", []);
        pane.UseFolder("/my-mod", []);
        pane.Show(assets);

        Assert.True(pane.CanFillFromArchives);
        Assert.False(pane.FillFromArchives);
        Assert.Null(pane.Compose("/w").SdfRoot);

        pane.FillFromArchives = true;

        Assert.Equal("/archives", pane.Compose("/w").SdfRoot);
        Assert.Equal("/my-mod", pane.Compose("/w").ContentRoot);
    }

    [Fact]
    public void The_gap_option_is_hidden_when_the_archives_are_the_source()
    {
        // Nothing to fill from a folder that is not being used, and nothing to
        // fill into. Offering it would be a switch with no meaning.
        ExportViewModel pane = new();
        pane.UseArchives("/archives", []);

        Assert.False(pane.IsFolderSource);
        Assert.False(pane.CanFillFromArchives);
    }

    [Fact]
    public async Task The_windows_extraction_takes_the_textures_too()
    {
        // The window has its own extraction path, separate from the command
        // line's. Adding the textures to only the command line left this one
        // quietly writing a kit that could not be exported from -- which is the
        // bug the user found after being told it was fixed.
        string root = Directory.CreateTempSubdirectory("perianth-kit-").FullName;

        try
        {
            byte[] editordata = new Perianth.Tests.Editordata.EditordataBuilder()
                .Section(Perianth.Tests.Editordata.MaterialSpec.Standard(diffuse: "camel/shared/tex_skin_d.dds"))
                .Build();

            string file = Path.Combine(root, "camel", "model.editordata");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, editordata);

            ExportViewModel pane = new();
            pane.UseFolder(root, [new Perianth.Formats.Sdf.SdfPathEntry("camel/shared/tex_skin_d.dds", 0, false)]);
            pane.Show(Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle));

            Assert.Contains("camel/shared/tex_skin_d.dds", await pane.KitAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void What_the_character_wears_is_drawn_into_the_same_file()
    {
        ExportViewModel pane = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle), Idle);
        pane.Equipment = () => [new WornModel("camel/equipment/body.mmb"), new WornModel("camel/equipment/hands.mmb")];
        Assert.Equal(
            ["camel/equipment/body.mmb", "camel/equipment/hands.mmb"],
            pane.Compose("/w").With.Select(worn => worn.Path));
        Assert.Contains("will be exported", pane.CostumeNote, System.StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_character_wears_goes_into_an_animation_too()
    {
        // It used to be dropped, because an animation drove only the first
        // model's copy of the hierarchy. The merge now gives those tracks to
        // every model sharing the skeleton, so the clothes move with the
        // character -- as they do in the game.
        ExportViewModel pane = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle), Idle);
        pane.Equipment = () => [new WornModel("camel/equipment/body.mmb")];
        pane.Animate = true;

        Assert.Single(pane.Compose("/w").With);
        Assert.Contains("move with it", pane.CostumeNote, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_costume_survives_the_animate_box_when_no_clip_is_chosen()
    {
        // The box is on by default and means nothing without a clip. Reading it
        // alone dropped the costume from a still export that was never going to
        // be animated.
        ExportViewModel pane = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact)), null);
        pane.Equipment = () => [new WornModel("camel/equipment/body.mmb")];
        pane.Animate = true;

        Assert.Single(pane.Compose("/w").With);
    }

    [Fact]
    public void Nothing_is_worn_into_an_unposed_export()
    {
        // The merge will not put a posed model beside an unposed one, and
        // equipment without the character's hierarchy is exactly that. Dropping
        // it here means the export goes out unposed rather than refusing over a
        // combination the user did not knowingly ask for.
        ExportViewModel pane = Pane(Assets(setup: null), null);
        pane.Equipment = () => [new WornModel("camel/equipment/body.mmb")];

        Assert.Empty(pane.Compose("/w").With);
    }

    [Fact]
    public void A_worn_piece_is_posed_by_the_same_clip_as_the_character()
    {
        // The bug this pins: a piece given only the setup stands in the rest
        // pose while the character stands in the clip's, so the costume does not
        // fit at frame one before anything has moved. A clip drives the
        // hierarchy, and every model here shares that hierarchy.
        ExportViewModel pane = Pane(Assets(new ResolvedAsset(Setup, AssetMatch.Exact), Idle), Idle);
        pane.Equipment = () => [new WornModel("camel/equipment/body.mmb")];

        ExportRequest request = pane.Compose("/w");

        Assert.Single(request.With);
        Assert.Contains(Idle, request.ClipAnims);
    }

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
    public void A_mod_folder_is_laid_out_for_the_export_to_read_first()
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

            // Under "my-files", not "extracted": the game's own files are read
            // from the archives and never written, so the only tree an export
            // creates holds what was already the user's.
            string landed = Path.Combine(working, "my-files", "camel", "baked", "tex.dds");
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
