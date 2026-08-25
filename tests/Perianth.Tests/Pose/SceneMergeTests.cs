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
    public void What_is_worn_is_drawn_over_the_body_rather_than_instead_of_it()
    {
        // Both hang a mesh off the joint named "chest". This used to take the
        // character's out, which deleted whole limbs elsewhere: the arm is one
        // mesh from shoulder to wrist, so a short sleeve took the forearm with
        // it. The piece is drawn in front, so it hides as much as its art
        // reaches and the rest stays visible, which is the point.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["chest", "skin"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["chest", "shirt"]);

        ExportScene merged = Merged([character, worn]);

        Assert.Equal(2, merged.Model.Parts.Length);
        Assert.Equal(2, merged.Materials.MaterialOfPart.Length);
    }

    [Fact]
    public void What_is_worn_on_top_does_not_take_the_feet_off()
    {
        // Only a garment stands where the character's feet do. Something worn
        // on top -- face paint, spectacles -- draws over the body and takes
        // nothing off, so it must not reach the pin either. Stated on the pin
        // because that is now the only thing removal can reach: on any ordinary
        // joint both pieces are kept whatever Replaces says, so a fixture there
        // would pass without the flag being read at all.
        ExportScene character = Pinned("foot", "HIDE_PIN__8", "toes");
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["foot", "anklet"])
            with { Replaces = false };

        Assert.Equal(2, Merged([character, worn]).Model.Parts.Length);
    }

    [Fact]
    public void A_piece_lying_entirely_behind_the_character_is_reflected_onto_its_own_plane()
    {
        // Every hairstyle is authored behind the whole character, further back
        // than its shoes, and the game draws hair in front of the head. The
        // piece is reflected about the plane it sits on: its frontmost depth
        // lands at that depth's own magnitude.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "head"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["hair_jnt", "hair"], depth: -1);

        ExportScene merged = Merged([character, worn]);

        // The fixture's frontmost is depth + 0.01, so -0.99 reflects to +0.99.
        Assert.Equal(0.99, Front(merged, mesh: 1), precision: 6);
    }

    [Fact]
    public void A_reflected_piece_keeps_a_trailing_part_trailing()
    {
        // The rule reflects the plane, not the art. Negating the geometry would
        // land the same plane in the same place and turn a cut with a trailing
        // part inside out -- a ponytail authored furthest back would come out
        // furthest forward, through the face. This is what separates the two,
        // and it is the only assertion that can.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "head"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["hair_jnt", "hair"], depth: -1);

        ExportScene merged = Merged([character, worn]);

        // Authored -1.00 .. -0.99; reflected +0.98 .. +0.99. The trailing point
        // is still the rearmost of the two, which negation would reverse.
        Assert.Equal(0.98, Back(merged, mesh: 1), precision: 6);
        Assert.True(Back(merged, mesh: 1) < Front(merged, mesh: 1),
            "the part authored furthest back should still be furthest back");
    }

    [Fact]
    public void A_piece_that_interleaves_with_the_character_is_left_alone()
    {
        // 247 of the 399 entries that draw anything do this -- costume bodies
        // and gloves whose parts sit between the character's chest and its arms.
        // A rule that stacked everything worn in front would reorder all of
        // them, and nothing says they are wrong today.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "head"]);
        // Straddling: it starts behind the character's rearmost and ends before
        // the character's frontmost, which is the shape a costume body has. A
        // guard that only asked "does it end behind the front" would move this.
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["hat_jnt", "hat"], depth: -0.005);

        ExportScene merged = Merged([character, worn]);

        Assert.Equal(0.005, Front(merged, mesh: 1), precision: 6);
    }

    [Fact]
    public void A_piece_already_in_front_is_left_alone()
    {
        // The eyewear and most headwear, 78 entries. Moving them would be a
        // second, invisible change riding on the one hair needs.
        ExportScene character = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["skull", "head"]);
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["specs_jnt", "specs"], depth: 5);

        ExportScene merged = Merged([character, worn]);

        Assert.Equal(5.01, Front(merged, mesh: 1), precision: 6);
    }

    /// <summary>The frontmost depth a merged scene draws one mesh at.</summary>
    private static double Front(ExportScene merged, int mesh)
    {
        SceneGraph graph = merged.Graph!;
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].Mesh != mesh)
            {
                continue;
            }

            // Two levels: the attachment hangs off a joint, and only the root
            // above it can have been moved.
            double above = 0;
            for (int outer = 0; outer < graph.Nodes.Length; outer++)
            {
                if (graph.Nodes[outer].Children.Contains(node))
                {
                    above = graph.Nodes[outer].Translation.Z;
                    for (int root = 0; root < graph.Nodes.Length; root++)
                    {
                        if (graph.Nodes[root].Children.Contains(outer))
                        {
                            above += graph.Nodes[root].Translation.Z;
                        }
                    }
                }
            }

            return above + merged.Model.Parts[mesh].Positions.Max(p => p.Z);
        }

        throw new Xunit.Sdk.XunitException($"no node draws mesh {mesh}");
    }

    /// <summary>The rearmost depth a merged scene draws one mesh at.</summary>
    /// <remarks>
    /// The counterpart of <see cref="Front"/>, and the only way to tell a
    /// reflection of the plane from a negation of the art: both put the
    /// frontmost point in the same place and disagree about everything behind
    /// it.
    /// </remarks>
    private static double Back(ExportScene merged, int mesh) =>
        Front(merged, mesh)
        - (merged.Model.Parts[mesh].Positions.Max(p => p.Z)
            - merged.Model.Parts[mesh].Positions.Min(p => p.Z));

    [Fact]
    public void A_mesh_on_a_pin_is_on_the_joint_the_pin_hangs_from()
    {
        // The character's feet do not hang off a foot joint: they hang off a
        // node the rig calls HIDE_PIN, and there are 50 of them, every one
        // parented to a foot joint. A costume's feet hang off those joints
        // directly, so the names never meet and a dressed character exports two
        // pairs of feet.
        ExportScene character = Pinned("foot", "HIDE_PIN__8", "toes");
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["foot", "boot"]);

        Assert.Single(Merged([character, worn]).Model.Parts);
    }

    [Fact]
    public void An_ordinary_node_between_a_mesh_and_a_covered_joint_is_not_a_pin()
    {
        // The pin is followed by name because the rig names it, and only the
        // pin. Following any intermediate node would make this "remove
        // everything below a covered joint", which is a subtree rule nobody
        // measured and which would take parts nothing is standing in for.
        ExportScene character = Pinned("foot", "ankle", "toes");
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["foot", "boot"]);

        Assert.Equal(2, Merged([character, worn]).Model.Parts.Length);
    }

    [Fact]
    public void A_pin_is_followed_one_level_and_no_further()
    {
        // A mesh two nodes below the pin is not what the pin carries.
        ExportScene character = Deep("foot", "HIDE_PIN__8", "under", "toes");
        ExportScene worn = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["foot", "boot"]);

        Assert.Equal(2, Merged([character, worn]).Model.Parts.Length);
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

    /// <summary>A joint, a node under it, and a mesh on that: the pin shape.</summary>
    private static ExportScene Pinned(string joint, string pin, string mesh) =>
        Chain([joint, pin, mesh], meshAt: 2);

    /// <summary>The same, one level deeper.</summary>
    private static ExportScene Deep(string joint, string pin, string under, string mesh) =>
        Chain([joint, pin, under, mesh], meshAt: 3);

    private static ExportScene Chain(string[] names, int meshAt)
    {
        ExportScene plain = Scene(parts: 1, images: 1, materials: 1, nodeNames: ["a", "b"]);
        SceneGraph graph = new(
            [.. names.Select((n, i) => new SceneNode(
                n, i + 1 < names.Length ? [i + 1] : [], default, default, default,
                i == meshAt ? 0 : null))],
            [0]);

        return plain with { Graph = graph };
    }

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
        float[]? times = null,
        double depth = 0)
    {
        GeometryModel model = new(mode,
        [
            .. Enumerable.Range(0, parts).Select(i => new GeometryPart(
                i,
                $"mode3-record-{i}",
                $"label:p{i}",
                $"p{i}",
                [new Vector3D(0, 0, depth), new Vector3D(1, 0, depth), new Vector3D(0, 1, depth + 0.01)],
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
