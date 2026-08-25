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
    /// What the rig calls a node that exists so what hangs off it can be hidden.
    /// </summary>
    /// <remarks>
    /// 50 of them in the main character's setup, every one parented to a foot
    /// joint and every one carrying the character's own feet.
    /// </remarks>
    private const string HidePin = "HIDE_PIN";

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

        // And what is worn is drawn on the character, not behind it.
        scenes = [scenes[0], .. scenes.Skip(1).Select(worn => InFront(scenes[0], worn))];

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
    /// The first model with the parts a costume stands <em>below</em> taken
    /// out — its feet, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Clothes are drawn over the body, not instead of it.</b> These are flat
    /// sheets at discrete depths and a worn piece is drawn in front of what it
    /// is worn over, so a costume's chest simply hides the character's, a sleeve
    /// hides as much of the arm as it reaches, and nothing needs removing for
    /// either to look right.
    /// </para>
    /// <para>
    /// This used to take out every mesh the character had on any joint a worn
    /// piece also drew on, which deleted whole limbs: the character's arm is
    /// <em>one</em> mesh from shoulder to wrist, so a short sleeve took the
    /// forearm with it and a dressed character had no arms below the elbow.
    /// That was the report that undid the rule.
    /// </para>
    /// <para>
    /// <b>Feet are the exception, and the game says so.</b> `myHidesFeet` is the
    /// only body-hiding field in the whole executable — there is no
    /// `myHidesArms`, no `myHidesTorso`, nothing per item naming what to take
    /// off. One flag exists for the one case drawing over cannot solve: feet
    /// stick out <em>below</em> a costume rather than behind it, so no amount of
    /// depth hides them. The character's own feet hang off 50 nodes the rig
    /// calls <c>HIDE_PIN</c> — its own word for a node that exists to be hidden
    /// at — every one parented to a foot joint a costume's feet use directly.
    /// </para>
    /// <para>
    /// So a mesh is taken out only when it hangs off a pin whose joint a worn
    /// piece draws on. Following the pin is reading the rig rather than
    /// inventing a rule, and it is followed one level only.
    /// </para>
    /// <para>
    /// <b>Do not restore the general form.</b> It was derived from geometry
    /// because the item records declare nothing about the body underneath, and
    /// the executable now confirms that nothing is there to find. Coverage was
    /// measured as a possible replacement and cannot carry one: these are
    /// alpha-carried sheets, so a bounding box is the sheet rather than the
    /// drawing, and a full-length sleeve scores 0.42 against an arm it covers
    /// completely.
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

        // Only what hangs off a pin whose joint a worn piece draws on. A mesh
        // index here is a part index: they are the same list.
        //
        // The character's feet do not hang off a foot joint: they hang off a
        // node the rig calls `HIDE_PIN`, 50 of them, every one parented to a
        // foot joint a costume's feet use directly. Following the pin is
        // reading the rig rather than inventing a rule, and it is followed one
        // level only.
        HashSet<int> replaced = [];
        string?[] parents = Parents(graph);
        int[] parentOf = ParentIndices(graph);
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].Mesh is not int mesh || parents[node] is not string joint)
            {
                continue;
            }

            // A mesh on the covered joint *itself* is deliberately kept: the
            // piece is drawn in front of it and hides as much of it as its art
            // reaches, which is what the sleeve and the forearm need.
            bool pinned = joint.StartsWith(HidePin, StringComparison.Ordinal) &&
                parentOf[node] >= 0 &&
                parents[parentOf[node]] is string above &&
                covered.Contains(above);

            if (pinned)
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

    /// <summary>
    /// <paramref name="worn"/> reflected about the plane it is drawn on if it
    /// lies entirely behind <paramref name="body"/>, and returned untouched
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These models are flat art at discrete depths, and depth is the draw
    /// order: the character's own parts run cleanly from the feet at +0.002 to
    /// the pupils at +0.048. A worn piece carries its own depths, and hair
    /// carries depths <b>behind the whole character</b> — every one of the
    /// hairstyles, around −0.043, which is further back than the shoes. The game
    /// draws hair in front of the head, plainly, in any screenshot of it.
    /// </para>
    /// <para>
    /// <b>The move is a reflection of the plane the art sits on, not of the art
    /// itself</b>: the piece is translated so its frontmost depth lands at that
    /// depth's own magnitude, which leaves every distance <em>within</em> the
    /// piece as authored. That distinction is the whole rule. Negating the
    /// geometry would land the same plane in the same place and turn a cut with
    /// a trailing part inside out, putting a ponytail through the face; a
    /// translation keeps it hanging behind.
    /// </para>
    /// <para>
    /// It is measured, not chosen. Every worn entry drawing anything was placed
    /// against the character's own range: the categories form a physically
    /// sensible ladder — skin, then makeup, then facial hair, then eyewear, then
    /// headwear — and hair is the only one authored negative. Reflected this
    /// way, <b>every hairstyle lands inside that ladder</b>, in front of the
    /// skull and behind the facial hair, eyewear and hats that are authored
    /// above it. Hair over the glasses, which an earlier rule produced by
    /// stacking each piece in front of everything, does not happen here.
    /// </para>
    /// <para>
    /// <b>The guard is that a piece must lie entirely behind to be moved at
    /// all.</b> Of the entries that draw something, those lying entirely behind
    /// are hair and nothing else; the eyewear and most headwear lie entirely in
    /// front; and the largest group interleaves — costume bodies and gloves
    /// whose parts sit between the character's chest and its arms. Moving those
    /// would reorder art nothing suggests is wrong. This cannot reach them.
    /// </para>
    /// <para>
    /// How the engine does it is still not known — no item field carries a
    /// depth, no draw-order field exists anywhere in the executable, the rig
    /// contributes none, and the depth remap the shader applies is per view, so
    /// it compresses the authored ladder without reordering it. This states what
    /// the art says rather than reconstructing a mechanism.
    /// </para>
    /// </remarks>
    private static ExportScene InFront(ExportScene body, ExportScene worn)
    {
        if (worn.Graph is not SceneGraph graph)
        {
            return worn;
        }

        (double bodyLow, _) = Depths(body);
        (_, double wornHigh) = Depths(worn);

        if (double.IsNaN(bodyLow) || double.IsNaN(wornHigh) || wornHigh >= bodyLow)
        {
            return worn;
        }

        // Reflect the plane, keep the arrangement: the frontmost depth lands at
        // its own magnitude, so a piece authored on a plane at −d is drawn on
        // the plane at +d with everything behind it still behind it.
        double forward = -2.0 * wornHigh;

        ImmutableArray<SceneNode>.Builder moved = graph.Nodes.ToBuilder();
        foreach (int root in graph.Roots)
        {
            SceneNode node = moved[root];
            moved[root] = node with
            {
                Translation = node.Translation with { Z = node.Translation.Z + forward },
            };
        }

        return worn with { Graph = new SceneGraph(moved.MoveToImmutable(), graph.Roots) };
    }

    /// <summary>The world-space depth range of everything a scene draws.</summary>
    private static (double Low, double High) Depths(ExportScene scene)
    {
        if (scene.Graph is not SceneGraph graph)
        {
            return (double.NaN, double.NaN);
        }

        double[][] world = World(graph);
        double low = double.PositiveInfinity;
        double high = double.NegativeInfinity;

        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            if (graph.Nodes[node].Mesh is not int mesh || mesh >= scene.Model.Parts.Length)
            {
                continue;
            }

            double[] matrix = world[node];
            foreach (Vector3D point in scene.Model.Parts[mesh].Positions)
            {
                double z = (point.X * matrix[2]) + (point.Y * matrix[6]) + (point.Z * matrix[10]) + matrix[14];
                low = Math.Min(low, z);
                high = Math.Max(high, z);
            }
        }

        return double.IsInfinity(low) ? (double.NaN, double.NaN) : (low, high);
    }

    /// <summary>Each node's world matrix, row-major and flattened.</summary>
    private static double[][] World(SceneGraph graph)
    {
        double[][] world = new double[graph.Nodes.Length][];
        foreach (int root in graph.Roots)
        {
            Descend(graph, root, Identity(), world);
        }

        for (int i = 0; i < world.Length; i++)
        {
            world[i] ??= Identity();
        }

        return world;
    }

    private static void Descend(SceneGraph graph, int node, double[] above, double[][] world)
    {
        double[] here = Multiply(Local(graph.Nodes[node]), above);
        world[node] = here;
        foreach (int child in graph.Nodes[node].Children)
        {
            Descend(graph, child, here, world);
        }
    }

    private static double[] Identity() =>
        [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    /// <summary>
    /// A node's own transform, for measuring depth with.
    /// </summary>
    /// <remarks>
    /// <b>A degenerate rotation or scale is read as the identity</b>, which is
    /// right for this and only this. An attachment node's scale is a visibility
    /// switch — an animated clip steps it between one and zero to turn a mesh
    /// off — so a part hidden at the sampled instant would otherwise collapse to
    /// the origin and drag the measured range with it. Where a part sits when it
    /// is drawn is the question; whether it is drawn now is not.
    /// </remarks>
    private static double[] Local(SceneNode node)
    {
        (double x, double y, double z, double w) = node.Rotation;
        if (x == 0 && y == 0 && z == 0 && w == 0)
        {
            w = 1;
        }

        double[] rotation =
        [
            1 - (2 * ((y * y) + (z * z))), 2 * ((x * y) + (z * w)), 2 * ((x * z) - (y * w)),
            2 * ((x * y) - (z * w)), 1 - (2 * ((x * x) + (z * z))), 2 * ((y * z) + (x * w)),
            2 * ((x * z) + (y * w)), 2 * ((y * z) - (x * w)), 1 - (2 * ((x * x) + (y * y))),
        ];

        double[] scale =
        [
            node.Scale.X == 0 ? 1 : node.Scale.X,
            node.Scale.Y == 0 ? 1 : node.Scale.Y,
            node.Scale.Z == 0 ? 1 : node.Scale.Z,
        ];
        double[] matrix = new double[16];
        for (int row = 0; row < 3; row++)
        {
            for (int column = 0; column < 3; column++)
            {
                matrix[(row * 4) + column] = rotation[(row * 3) + column] * scale[row];
            }
        }

        matrix[12] = node.Translation.X;
        matrix[13] = node.Translation.Y;
        matrix[14] = node.Translation.Z;
        matrix[15] = 1;
        return matrix;
    }

    private static double[] Multiply(double[] a, double[] b)
    {
        double[] result = new double[16];
        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                double sum = 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += a[(row * 4) + k] * b[(k * 4) + column];
                }

                result[(row * 4) + column] = sum;
            }
        }

        return result;
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

    /// <summary>Each node's parent index, or -1 for a root.</summary>
    private static int[] ParentIndices(SceneGraph graph)
    {
        int[] parents = new int[graph.Nodes.Length];
        System.Array.Fill(parents, -1);
        for (int node = 0; node < graph.Nodes.Length; node++)
        {
            foreach (int child in graph.Nodes[node].Children)
            {
                parents[child] = node;
            }
        }

        return parents;
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
