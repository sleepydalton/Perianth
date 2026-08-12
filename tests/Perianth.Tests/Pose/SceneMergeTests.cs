using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Geometry;
using Perianth.Formats.Mmb;
using Perianth.Core.Materials;
using Perianth.Core.Pose;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Pose;

/// <summary>
/// Combining a character and what it wears into one scene.
/// </summary>
/// <remarks>
/// Every test here is about an index being shifted or deliberately not shifted.
/// That is the whole risk in this operation: a wrong offset produces a file that
/// opens, draws, and puts the wrong surface on the wrong part — which nothing
/// downstream can detect and no refusal will fire over.
/// </remarks>
public sealed class SceneMergeTests
{
    [Fact]
    public void One_scene_is_returned_unchanged()
    {
        ExportScene only = Scene(parts: 2, images: 1, materials: 1);

        ExportScene merged = Merged([only]);

        Assert.Same(only.Model, merged.Model);
        Assert.Same(only.Materials, merged.Materials);
    }

    [Fact]
    public void Parts_images_and_surfaces_are_concatenated_in_order()
    {
        ExportScene merged = Merged([
            Scene(parts: 2, images: 1, materials: 1),
            Scene(parts: 3, images: 2, materials: 2)]);

        Assert.Equal(5, merged.Model.Parts.Length);
        Assert.Equal(3, merged.Materials.Images.Length);
        Assert.Equal(3, merged.Materials.Materials.Length);
    }

    [Fact]
    public void A_surface_still_points_at_its_own_image()
    {
        // The failure this prevents: the second model's materials keep their
        // original image indices and dress its parts in the first model's
        // textures. The export is valid and the character wears somebody else's
        // clothes.
        ExportScene merged = Merged([
            Scene(parts: 1, images: 2, materials: 1, imageIndex: 1, imageName: "first"),
            Scene(parts: 1, images: 2, materials: 1, imageIndex: 1, imageName: "second")]);

        Assert.Equal("first-1", merged.Materials.Images[merged.Materials.Materials[0].ImageIndex!.Value].Name);
        Assert.Equal("second-1", merged.Materials.Images[merged.Materials.Materials[1].ImageIndex!.Value].Name);
    }

    [Fact]
    public void A_part_still_wears_its_own_surface()
    {
        ExportScene merged = Merged([
            Scene(parts: 1, images: 1, materials: 1, materialOfPart: [0]),
            Scene(parts: 1, images: 1, materials: 1, materialOfPart: [0])]);

        Assert.Equal([0, 1], merged.Materials.MaterialOfPart);
    }

    [Fact]
    public void An_untextured_part_stays_untextured()
    {
        // -1 says "no surface" and is not an index. Shifting it names a real
        // material, so a bare part silently gains another model's texture —
        // which looks like a feature rather than a fault.
        ExportScene merged = Merged([
            Scene(parts: 2, images: 1, materials: 1, materialOfPart: [0, -1]),
            Scene(parts: 1, images: 1, materials: 1, materialOfPart: [-1])]);

        Assert.Equal([0, -1, -1], merged.Materials.MaterialOfPart);
    }

    [Fact]
    public void Nodes_keep_their_own_children_meshes_and_roots()
    {
        // Distinct joint names, or these two would be wearing each other: a
        // shared joint means one replaces the other, which is a different test.
        ExportScene merged = Merged([
            Scene(parts: 2, images: 1, materials: 1, nodeNames: ["chest", "skin"]),
            Scene(parts: 3, images: 1, materials: 1, nodeNames: ["head", "mask"])]);

        Assert.NotNull(merged.Graph);
        Assert.Equal(4, merged.Graph!.Nodes.Length);

        // First scene untouched; second shifted by its own node and part counts.
        Assert.Equal([1], merged.Graph.Nodes[0].Children);
        Assert.Equal([3], merged.Graph.Nodes[2].Children);
        Assert.Equal(0, merged.Graph.Nodes[1].Mesh);
        Assert.Equal(2, merged.Graph.Nodes[3].Mesh);
        Assert.Equal([0, 2], merged.Graph.Roots);
    }

    [Fact]
    public void What_is_worn_replaces_what_it_covers()
    {
        // Both hang a mesh off the joint named "chest", so they are competing
        // for one place on the body. Drawing both is what put a costume beneath
        // a character and out of sight.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["chest", "skin"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["chest", "shirt"]);

        ExportScene merged = Merged([character, worn]);

        Assert.Single(merged.Model.Parts);
        Assert.Single(merged.Materials.MaterialOfPart);
    }

    [Fact]
    public void What_is_worn_on_top_replaces_nothing_beneath_it()
    {
        // Face paint hangs off the joint the character's own skull hangs from,
        // and a joint holds many parts, so replacing there deletes the head
        // rather than covering it. Measured in the archives: one makeup piece
        // adds a single decal at the eye joint and was taking all five of the
        // character's eye meshes with it.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "face"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "paint"])
            with { Replaces = false };

        ExportScene merged = Merged([character, worn]);

        Assert.Equal(2, merged.Model.Parts.Length);
        Assert.Equal(2, merged.Materials.MaterialOfPart.Length);
    }

    [Fact]
    public void What_is_worn_elsewhere_replaces_nothing()
    {
        // A mask's meshes hang off a joint the character does not use, which is
        // why headwear correctly takes nothing away.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["chest", "skin"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["head", "mask"]);

        ExportScene merged = Merged([character, worn]);

        Assert.Equal(2, merged.Model.Parts.Length);
    }

    [Fact]
    public void Every_model_brings_its_own_animation_into_one_timeline()
    {
        // Each is posed by the same setup and the same clip, so each has its own
        // tracks for its own nodes and merging only moves the indices. Nothing is
        // copied between them.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"],
            animatedNode: 1);
        ExportScene clothes = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"],
            animatedNode: 1);

        ExportScene merged = Merged([character, clothes]);

        Animation animation = Assert.Single(merged.Animations);
        Assert.Equal(2, animation.Tracks.Length);
        Assert.Equal(1, animation.Tracks[0].Node);
        Assert.Equal(3, animation.Tracks[1].Node);   // node 1 of a scene offset by 2
    }

    [Fact]
    public void A_model_on_a_different_timeline_is_left_out()
    {
        // Two timelines in one animation would play both at whichever rate the
        // file declares, so the odd one is dropped rather than interleaved.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"],
            animatedNode: 1);
        ExportScene slower = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"],
            animatedNode: 1, times: [0f, 2f]);

        ExportScene merged = Merged([character, slower]);

        Assert.Single(Assert.Single(merged.Animations).Tracks);
    }

    [Fact]
    public void A_models_tracks_are_not_given_to_a_model_that_has_none()
    {
        // The fault this replaced: every track was copied onto each other model
        // by joint name, including the stepped scale tracks a clip uses to switch
        // meshes. That handed each worn piece the character's visibility
        // decisions and discarded its own, leaving an arm missing and a hand
        // adrift on a clip that switches limb configurations.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"],
            animatedNode: 1);
        ExportScene still = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["root", "spine"]);

        ExportScene merged = Merged([character, still]);

        Animation animation = Assert.Single(merged.Animations);
        Assert.Single(animation.Tracks);
        Assert.Equal(1, animation.Tracks[0].Node);
    }

    [Fact]
    public void Diagnostics_are_dropped_rather_than_concatenated()
    {
        // They are keyed by source ordinal, which belongs to one model's
        // editordata. Concatenated, section 5 appears twice meaning two
        // different things, and a report built from it would be confidently
        // wrong. They are said per model before this runs.
        ExportScene merged = Merged([
            Scene(parts: 1, images: 1, materials: 1, clamped: [5]),
            Scene(parts: 1, images: 1, materials: 1, clamped: [5])]);

        Assert.Empty(merged.Materials.ClampedParts);
        Assert.Empty(merged.Materials.SurvivingParts);
    }

    [Fact]
    public void Mixing_a_posed_model_with_an_unposed_one_is_refused()
    {
        // One would be placed and the other piled at the origin, with nothing in
        // the file saying which happened.
        Result<ExportScene> merged = SceneMerge.Merge([
            Scene(parts: 1, images: 1, materials: 1, nodeCount: 1),
            Scene(parts: 1, images: 1, materials: 1, nodeCount: 0)]);

        Assert.False(merged.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, merged.Refusal!.Kind);
    }

    [Fact]
    public void Mixing_two_geometry_modes_is_refused()
    {
        Result<ExportScene> merged = SceneMerge.Merge([
            Scene(parts: 1, images: 1, materials: 1, mode: 3),
            Scene(parts: 1, images: 1, materials: 1, mode: 2)]);

        Assert.False(merged.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, merged.Refusal!.Kind);
    }

    [Fact]
    public void Nothing_to_merge_is_refused_rather_than_producing_an_empty_file()
    {
        Result<ExportScene> merged = SceneMerge.Merge([]);

        Assert.False(merged.IsSuccess);
    }

    // --- fixtures ------------------------------------------------------------

    private static ExportScene Merged(ImmutableArray<ExportScene> scenes)
    {
        Result<ExportScene> merged = SceneMerge.Merge(scenes);
        Assert.True(merged.IsSuccess, merged.IsSuccess ? "" : merged.Refusal!.Message);
        return merged.Value;
    }

    private static ExportScene Scene(
        int parts,
        int images,
        int materials,
        int mode = 3,
        int nodeCount = 0,
        int imageIndex = 0,
        string imageName = "img",
        int[]? materialOfPart = null,
        int[]? clamped = null,
        string[]? nodeNames = null,
        int? animatedNode = null,
        float[]? times = null)
    {
        GeometryModel model = new(mode,
        [
            .. Enumerable.Range(0, parts).Select(i => new GeometryPart(
                i,
                $"mode3-record-{i}",
                $"label:p{i}",
                $"p{i}",
                [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
                [0, 1, 2],
                [],
                [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)])),
        ], false);

        MaterialSet set = new(
            [.. Enumerable.Range(0, images).Select(i => new TextureImage($"{imageName}-{i}", []))],
            [.. Enumerable.Range(0, materials).Select(_ => new SurfaceMaterial(
                "m", imageIndex, new ColorRgba(1, 1, 1, 1), false, TextureWrap.Repeat, TextureScale.Identity))],
            [.. materialOfPart ?? Enumerable.Repeat(0, parts).ToArray()],
            SurvivingParts: [.. Enumerable.Range(0, parts)],
            OffsetBakedParts: [],
            ClippedParts: [],
            MergedCompanions: [],
            UnpairedCompanions: [],
            ClampedParts: [.. clamped ?? []],
            BakedParts: [],
            Uv0Remaps: [],
            OversizedOmissions: []);

        if (nodeNames is not null)
        {
            SceneGraph named = new(
                [.. nodeNames.Select((n, i) => new SceneNode(
                    n, i == 0 ? [1] : [], default, default, default, i == 0 ? null : 0))],
                [0]);

            ImmutableArray<Animation> animations = animatedNode is int node
                ? [new Animation("clip", times is null ? [0f, 1f] : [.. times],
                    [new AnimationTrack(node, TrackPath.Translation, TrackInterpolation.Linear,
                        [0, 0, 0, 1, 0, 0])])]
                : [];

            return new ExportScene(model, set, named, animations);
        }

        // A chain: node 0 is the root and node 1 carries mesh 0. Enough shape for
        // a child index and a mesh index to be visibly right or wrong.
        SceneGraph? graph = nodeCount == 0
            ? null
            : new SceneGraph(
                [.. Enumerable.Range(0, nodeCount).Select(i => new SceneNode(
                    $"n{i}",
                    i == 0 && nodeCount > 1 ? [1] : [],
                    default, default, default,
                    i == 0 && nodeCount > 1 ? null : 0))],
                [0]);

        return new ExportScene(model, set, graph);
    }
}
