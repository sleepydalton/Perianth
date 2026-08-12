using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Perianth.Core.Geometry;
using Perianth.Core.Materials;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// One model's finished export, ready to be written or merged with others.
/// </summary>
/// <param name="Model">The parts that will be drawn, already selected.</param>
/// <param name="Materials">The surfaces dressing them, in the same order.</param>
/// <param name="Graph">The posed hierarchy, or null for an unposed export.</param>
/// <param name="Animations">The animations driving it, which only the first model carries.</param>
/// <param name="Replaces">
/// Whether this takes the first model's own parts off wherever it draws. True
/// for a garment, which is worn instead of what is under it; false for
/// something worn on top, like face paint or spectacles. Ignored on the first
/// model, which has nothing beneath it.
/// </param>
public readonly record struct ExportScene(
    GeometryModel Model, MaterialSet Materials, SceneGraph? Graph,
    ImmutableArray<Animation> Animations = default,
    bool Replaces = true);

/// <summary>
/// Combines several posed models into one scene, so a character and the
/// equipment it wears come out as a single file.
/// </summary>
/// <remarks>
/// <para>
/// This is possible at all because equipment is not a separate rig: measured
/// over all 1,196 equipment models, every one has its parts named by the main
/// character's hierarchy, and posing a piece with the character's own setup puts
/// it exactly where the character is — 48 of 48 shared nodes on identical
/// transforms, to four decimal places. So there is nothing to align. The work
/// here is only index arithmetic.
/// </para>
/// <para>
/// <b>Merged late, at the point the writer is called.</b> By then the UV remaps
/// are applied and the surviving parts selected, so what is left to combine is
/// exactly what the writer reads: the parts, the images, the surfaces, and which
/// surface dresses which part. Merging earlier would mean carrying every
/// intermediate index across models, which is more arithmetic and more ways to
/// be silently wrong.
/// </para>
/// <para>
/// <b>Nodes are not deduplicated.</b> Two models posed by one hierarchy produce
/// the same node names, and merging them by name would need the two to agree
/// about every shared bone — which is an assumption, where keeping them apart is
/// a fact. Each mesh hangs off its own model's copy, so duplicate names cost a
/// few nodes and can never attach a part to another model's bone.
/// </para>
/// </remarks>
public static class SceneMerge
{
    /// <summary>
    /// Combines <paramref name="scenes"/> in order, the first being the model
    /// the others are added to.
    /// </summary>
    /// <remarks>
    /// Order is kept rather than sorted, because it is the caller's statement of
    /// what the export is: the character first, then what it wears.
    /// </remarks>
    public static Result<ExportScene> Merge(IReadOnlyList<ExportScene> scenes)
    {
        ArgumentNullException.ThrowIfNull(scenes);

        if (scenes.Count == 0)
        {
            return Refusal.Unsupported("A merged export needs at least one model.");
        }

        if (scenes.Count == 1)
        {
            return Result.Ok(scenes[0]);
        }

        // What is worn replaces what it covers. Done before anything is merged,
        // so the surgery happens while the first model's indices are still its
        // own -- afterwards they would have to be found again among everyone
        // else's.
        scenes = [Uncovered(scenes), .. scenes.Skip(1)];

        // The cameldata mode decides how a part's vertex data was resolved, so
        // parts from two modes are not interchangeable in one array. No observed
        // character and equipment pair differs, which is why this refuses rather
        // than converting: a case that does not arise needs no conversion, and a
        // conversion nobody can test is worse than a refusal.
        int mode = scenes[0].Model.Mode;
        for (int i = 1; i < scenes.Count; i++)
        {
            if (scenes[i].Model.Mode != mode)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"These models are built differently and cannot go in one file. Export them separately — posed the same way, they will still line up."));
            }
        }

        bool anyPosed = false;
        bool anyUnposed = false;
        foreach (ExportScene scene in scenes)
        {
            if (scene.Graph is null)
            {
                anyUnposed = true;
            }
            else
            {
                anyPosed = true;
            }
        }

        // A posed model places its parts and an unposed one does not, so one file
        // holding both would draw a character correctly beside a heap of every
        // alternate state of something else, with nothing saying which was which.
        if (anyPosed && anyUnposed)
        {
            return Refusal.Unsupported(
                "One of these is posed and another is not, so they would not line up. Pose them all, or export the unposed one on its own.");
        }

        ImmutableArray<GeometryPart>.Builder parts = ImmutableArray.CreateBuilder<GeometryPart>();
        ImmutableArray<TextureImage>.Builder images = ImmutableArray.CreateBuilder<TextureImage>();
        ImmutableArray<SurfaceMaterial>.Builder surfaces = ImmutableArray.CreateBuilder<SurfaceMaterial>();
        ImmutableArray<int>.Builder materialOfPart = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<SceneNode>.Builder nodes = ImmutableArray.CreateBuilder<SceneNode>();
        ImmutableArray<int>.Builder roots = ImmutableArray.CreateBuilder<int>();
        List<int> nodeOffsets = [];

        bool surfaceUv0Unavailable = false;

        foreach (ExportScene scene in scenes)
        {
            int partOffset = parts.Count;
            int imageOffset = images.Count;
            int materialOffset = surfaces.Count;
            int nodeOffset = nodes.Count;
            nodeOffsets.Add(nodeOffset);

            parts.AddRange(scene.Model.Parts);
            surfaceUv0Unavailable |= scene.Model.SurfaceUv0Unavailable;

            images.AddRange(scene.Materials.Images);

            foreach (SurfaceMaterial surface in scene.Materials.Materials)
            {
                surfaces.Add(surface with
                {
                    ImageIndex = Shift(surface.ImageIndex, imageOffset),
                    EmissiveImageIndex = Shift(surface.EmissiveImageIndex, imageOffset),
                });
            }

            // -1 means "this part is untextured" and is not an index, so it must
            // survive the shift unchanged. Offsetting it would name a real
            // material and dress a bare part in another model's surface.
            foreach (int material in scene.Materials.MaterialOfPart)
            {
                materialOfPart.Add(material < 0 ? material : material + materialOffset);
            }

            if (scene.Graph is not SceneGraph graph)
            {
                continue;
            }

            foreach (SceneNode node in graph.Nodes)
            {
                ImmutableArray<int>.Builder children =
                    ImmutableArray.CreateBuilder<int>(node.Children.Length);
                foreach (int child in node.Children)
                {
                    children.Add(child + nodeOffset);
                }

                nodes.Add(node with
                {
                    Children = children.MoveToImmutable(),
                    Mesh = Shift(node.Mesh, partOffset),
                });
            }

            foreach (int root in graph.Roots)
            {
                roots.Add(root + nodeOffset);
            }
        }

        // Every array a MaterialSet carries beyond these three is a diagnostic
        // keyed by SOURCE ordinal — an editordata section number, which belongs
        // to one model and means something different in the next. Concatenating
        // them would produce a list that reads as one model's and is two. They
        // are reported per model before this runs, where each still has a name
        // attached, and are deliberately empty here.
        MaterialSet merged = new(
            images.ToImmutable(),
            surfaces.ToImmutable(),
            materialOfPart.ToImmutable(),
            SurvivingParts: [],
            OffsetBakedParts: [],
            ClippedParts: [],
            MergedCompanions: [],
            UnpairedCompanions: [],
            ClampedParts: [],
            BakedParts: [],
            Uv0Remaps: [],
            OversizedOmissions: []);

        GeometryModel model = new(mode, parts.ToImmutable(), surfaceUv0Unavailable);
        SceneGraph? hierarchy = anyPosed
            ? new SceneGraph(nodes.ToImmutable(), roots.ToImmutable())
            : null;

        return Result.Ok(new ExportScene(
            model, merged, hierarchy, Share(scenes, nodeOffsets)));
    }

    /// <summary>
    /// The first model with the parts its clothes cover taken out.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A worn piece hangs its meshes off the same hierarchy the character does,
    /// so where a piece puts meshes on a joint the character also puts meshes
    /// on, the two are competing for one place on the body. Measured over seven
    /// pieces: a costume body takes out 9 to 15 of the character's 37 meshes —
    /// the chest, the undies and both biceps — gloves take out 4 to 8, and every
    /// head or hair piece takes out none, because a mask's meshes hang off one
    /// joint the character does not use.
    /// </para>
    /// <para>
    /// <b>Derived rather than declared.</b> The item records say which equipment
    /// slots exclude each other and nothing about the body underneath, so this
    /// is read from the models. The game surely does it from data this build has
    /// not found; if that data turns up, it wins. Until then this matches what
    /// the game draws on every piece measured, and the alternative is two
    /// torsos in one place.
    /// </para>
    /// </remarks>
    private static ExportScene Uncovered(IReadOnlyList<ExportScene> scenes)
    {
        ExportScene first = scenes[0];
        if (first.Graph is not SceneGraph graph)
        {
            return first;
        }

        // Only what is worn instead of the body. A joint holds many parts, so
        // replacing at one is all-or-nothing: face paint parented to the skull
        // joint takes the skull with it, and a makeup decal at the eye joint
        // takes all five of the character's eye meshes. Both were measured.
        HashSet<string> covered = new(StringComparer.Ordinal);
        for (int i = 1; i < scenes.Count; i++)
        {
            if (scenes[i].Replaces && scenes[i].Graph is SceneGraph worn)
            {
                foreach (string joint in Joints(worn))
                {
                    covered.Add(joint);
                }
            }
        }

        if (covered.Count == 0)
        {
            return first;
        }

        // Which of the first model's meshes hang off a covered joint. A mesh
        // index here is a part index: they are the same list.
        HashSet<int> replaced = [];
        string?[] parents = Parents(graph);
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].Mesh is int mesh &&
                parents[node] is string joint &&
                covered.Contains(joint))
            {
                replaced.Add(mesh);
            }
        }

        if (replaced.Count == 0)
        {
            return first;
        }

        ImmutableArray<int> kept =
            [.. Enumerable.Range(0, first.Model.Parts.Length).Where(part => !replaced.Contains(part))];

        ImmutableArray<int> materialOfPart = first.Materials.MaterialOfPart.IsDefaultOrEmpty
            ? first.Materials.MaterialOfPart
            : [.. kept.Select(part => first.Materials.MaterialOfPart[part])];

        return first with
        {
            Model = first.Model.SelectParts(kept),
            Materials = first.Materials with { MaterialOfPart = materialOfPart },
            Graph = graph.RemapMeshes(kept, first.Model.Parts.Length),
        };
    }

    /// <summary>The names of the joints a graph hangs meshes from.</summary>
    private static IEnumerable<string> Joints(SceneGraph graph)
    {
        string?[] parents = Parents(graph);
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].Mesh is not null && parents[node] is string joint)
            {
                yield return joint;
            }
        }
    }

    /// <summary>Each node's parent name, since a graph stores only children.</summary>
    private static string?[] Parents(SceneGraph graph)
    {
        string?[] parents = new string?[graph.Nodes.Length];
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            foreach (int child in graph.Nodes[node].Children)
            {
                parents[child] = graph.Nodes[node].Name;
            }
        }

        return parents;
    }

    /// <summary>
    /// Every model's own animation, in one timeline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each model is posed by the same setup and the same clip, so each produces
    /// its own tracks for its own nodes. Merging is then only a matter of moving
    /// the node indices, and the character and what it wears move together
    /// because they were animated by the same file, not because anything was
    /// copied between them.
    /// </para>
    /// <para>
    /// <b>This replaced copying the first model's tracks onto the others by
    /// joint name, which was wrong in a way that took a report to find.</b> A
    /// clip carries visibility as well as pose — a stepped scale track for every
    /// mesh it switches — and copying every track handed each worn piece the
    /// character's visibility decisions while discarding its own. On a melee
    /// clip, which switches limb configurations, that hid parts of the costume
    /// and left an arm missing and a hand adrift. Copying was also redundant by
    /// then: it dated from when a piece was given the setup but not the clip,
    /// and so had no animation of its own to merge.
    /// </para>
    /// <para>
    /// Animations are paired by name across models. One whose timeline differs
    /// from the first model's is left out rather than interleaved, because two
    /// timelines in one animation would play both at whichever rate the file
    /// declares.
    /// </para>
    /// </remarks>
    private static ImmutableArray<Animation> Share(
        IReadOnlyList<ExportScene> scenes, List<int> nodeOffsets)
    {
        ImmutableArray<Animation> first =
            scenes[0].Animations.IsDefault ? [] : scenes[0].Animations;

        if (first.IsEmpty)
        {
            return first;
        }

        ImmutableArray<Animation>.Builder merged =
            ImmutableArray<Animation>.Empty.ToBuilder();

        foreach (Animation animation in first)
        {
            ImmutableArray<AnimationTrack>.Builder tracks =
                ImmutableArray<AnimationTrack>.Empty.ToBuilder();
            tracks.AddRange(animation.Tracks);

            for (int i = 1; i < scenes.Count; i++)
            {
                ImmutableArray<Animation> theirs =
                    scenes[i].Animations.IsDefault ? [] : scenes[i].Animations;

                foreach (Animation other in theirs)
                {
                    if (!string.Equals(other.Name, animation.Name, StringComparison.Ordinal) ||
                        !other.Times.SequenceEqual(animation.Times))
                    {
                        continue;
                    }

                    foreach (AnimationTrack track in other.Tracks)
                    {
                        tracks.Add(track with { Node = track.Node + nodeOffsets[i] });
                    }
                }
            }

            merged.Add(animation with { Tracks = tracks.ToImmutable() });
        }

        return merged.ToImmutable();
    }

    private static int? Shift(int? index, int offset) =>
        index is int value ? value + offset : null;
}
