using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// Poses a model with one hierarchy and fills what that hierarchy cannot name
/// from a second one.
/// </summary>
/// <remarks>
/// <para>
/// For the 29 of the game's 918 characters that have no setup ANIM of their own.
/// A relative's hierarchy poses most of such a model and stops somewhere: one
/// measured case comes out as a correct body with no head, because the donor
/// names none of its 57 head nodes. A second donor supplies
/// those, and the result is a whole character built entirely from the model's
/// own parts — only the *selection* is borrowed.
/// </para>
/// <para>
/// <b>The rule is "parts the primary cannot name", not "parts the primary does
/// not show".</b> Where the primary names a node it has an opinion about that
/// part, and its opinion stands even when the answer is "hidden"; only where it
/// is silent does the donor speak. Doing it the other way lets the donor
/// overturn visibility the primary decided, which shows as a doubled sleeve —
/// two rigs choosing different variants of one thing and both drawing.
/// </para>
/// <para>
/// <b>Borrowed parts are placed, not parented.</b> Each is emitted at the world
/// transform the donor gives it, as a root of the scene, because the two
/// hierarchies are different trees and there is no node in the primary's to hang
/// it from. That is sound for a still and has a consequence a caller must state:
/// a borrowed part does not follow the primary's animation. Reparenting by name
/// would fix that and is a larger change; it is deliberately not done here.
/// </para>
/// <para>
/// The donors must agree about where the nodes they share sit, or the result
/// comes apart. Two single-character rigs measured 0.000 apart; a crowd rig,
/// which poses several characters across a scene, measured 10.2. Nothing here
/// checks that — <see cref="Disagreement"/> reports it so a caller can.
/// </para>
/// </remarks>
public static class BorrowedPose
{
    /// <summary>
    /// Poses <paramref name="model"/> under <paramref name="primary"/>, taking
    /// parts it cannot name from <paramref name="donor"/>.
    /// </summary>
    /// <param name="model">The geometry to pose.</param>
    /// <param name="primary">The hierarchy that poses the model.</param>
    /// <param name="donor">The hierarchy consulted for parts the primary cannot name.</param>
    /// <param name="clip">An animation to take channels from, as an ordinary pose.</param>
    /// <param name="seconds">The moment to sample.</param>
    public static Result<PosedScene> Pose(
        GeometryModel model, AnimFile primary, AnimFile donor, AnimFile? clip, double seconds)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(primary);
        System.ArgumentNullException.ThrowIfNull(donor);

        Result<PoseSampling.Association> association =
            PoseSampling.Associate(model, primary, allowMissingParts: true);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        Result<PosedScene> primaryPosed = SetupPose.Pose(model, primary, clip, seconds, allowMissingParts: true);
        if (!primaryPosed.TryGetValue(out PosedScene? placed, out Refusal? primaryRefusal))
        {
            return primaryRefusal;
        }

        // The donor is read at its own rest pose. A clip belongs to the model's
        // own animation set and names the primary's nodes; running it against a
        // different hierarchy would be a guess about which of its channels apply.
        Result<PosedScene> donorPosed = SetupPose.Pose(model, donor, clip: null, seconds: 0.0, allowMissingParts: true);
        if (!donorPosed.TryGetValue(out PosedScene? donated, out Refusal? donorRefusal))
        {
            return donorRefusal;
        }

        ImmutableArray<int> borrowed =
            [.. donated.Keep.Where(part => bindings.NodeOfPart[part] < 0)];

        if (borrowed.IsEmpty)
        {
            // Nothing to add. Not a failure — the primary already accounts for
            // every part the donor would have contributed — but a caller that
            // asked for a donor should be able to tell, so the scene comes back
            // unchanged rather than pretending otherwise.
            return Result.Ok(placed);
        }

        int[] nodeOfMesh = NodeOfMesh(donated.Graph);
        List<SceneNode> added = [];
        ImmutableArray<int>.Builder keep = ImmutableArray.CreateBuilder<int>(placed.Keep.Length + borrowed.Length);
        keep.AddRange(placed.Keep);

        foreach (int part in borrowed)
        {
            int mesh = donated.Keep.IndexOf(part);
            Result<(AnimVec3 T, AnimQuat R, AnimVec3 S)> world =
                WorldOf(donated.Graph, nodeOfMesh[mesh]);
            if (!world.TryGetValue(out (AnimVec3 T, AnimQuat R, AnimVec3 S) placement, out Refusal? worldRefusal))
            {
                return worldRefusal;
            }

            added.Add(new SceneNode(
                model.Parts[part].SourceLabel,
                [],
                placement.T,
                placement.R,
                placement.S,
                Mesh: keep.Count));
            keep.Add(part);
        }

        int first = placed.Graph.Nodes.Length;
        SceneGraph merged = new(
            [.. placed.Graph.Nodes, .. added],
            [.. placed.Graph.Roots, .. Enumerable.Range(first, added.Count)]);

        // Still reported: a part neither hierarchy names is omitted as before.
        ImmutableArray<string> stillMissing =
            [.. borrowed.Length == 0
                ? placed.UnriggedParts
                : placed.UnriggedParts.Where(label => !added.Any(node => node.Name == label))];

        return Result.Ok(new PosedScene(keep.ToImmutable(), merged, stillMissing));
    }

    /// <summary>
    /// How far apart two hierarchies place the parts they both name, as the
    /// median and worst distance.
    /// </summary>
    /// <remarks>
    /// The check that stops a donor being chosen on name coverage alone. The
    /// hierarchy naming the most of a model's head was a crowd rig, which poses
    /// several characters spread across a scene: it named nearly every head node
    /// and threw the parts tens of units apart. Coverage is necessary and not
    /// sufficient; this is the rest of it.
    /// </remarks>
    /// <returns>Median and worst distance, or null where they share no part.</returns>
    public static (double Median, double Worst)? Disagreement(PosedScene first, PosedScene second)
    {
        System.ArgumentNullException.ThrowIfNull(first);
        System.ArgumentNullException.ThrowIfNull(second);

        int[] firstNodes = NodeOfMesh(first.Graph);
        int[] secondNodes = NodeOfMesh(second.Graph);
        List<double> distances = [];

        for (int mesh = 0; mesh < first.Keep.Length; mesh++)
        {
            int other = second.Keep.IndexOf(first.Keep[mesh]);
            if (other < 0)
            {
                continue;
            }

            Result<(AnimVec3 T, AnimQuat R, AnimVec3 S)> a = WorldOf(first.Graph, firstNodes[mesh]);
            Result<(AnimVec3 T, AnimQuat R, AnimVec3 S)> b = WorldOf(second.Graph, secondNodes[other]);
            if (!a.IsSuccess || !b.IsSuccess)
            {
                continue;
            }

            AnimVec3 p = a.Value.T;
            AnimVec3 q = b.Value.T;
            distances.Add(System.Math.Sqrt(
                ((p.X - q.X) * (p.X - q.X)) + ((p.Y - q.Y) * (p.Y - q.Y)) + ((p.Z - q.Z) * (p.Z - q.Z))));
        }

        if (distances.Count == 0)
        {
            return null;
        }

        distances.Sort();
        return (distances[distances.Count / 2], distances[^1]);
    }

    /// <summary>Which graph node draws each mesh index.</summary>
    private static int[] NodeOfMesh(SceneGraph graph)
    {
        int meshes = 0;
        foreach (SceneNode node in graph.Nodes)
        {
            if (node.Mesh is int m && m + 1 > meshes)
            {
                meshes = m + 1;
            }
        }

        int[] nodeOfMesh = new int[meshes];
        for (int index = 0; index < graph.Nodes.Length; index++)
        {
            if (graph.Nodes[index].Mesh is int mesh)
            {
                nodeOfMesh[mesh] = index;
            }
        }

        return nodeOfMesh;
    }

    /// <summary>The world transform of one node, composed down from its root.</summary>
    private static Result<(AnimVec3 T, AnimQuat R, AnimVec3 S)> WorldOf(SceneGraph graph, int node)
    {
        int[] parent = new int[graph.Nodes.Length];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = -1;
        }

        for (int i = 0; i < graph.Nodes.Length; i++)
        {
            foreach (int child in graph.Nodes[i].Children)
            {
                parent[child] = i;
            }
        }

        List<int> chain = [];
        for (int walk = node; walk >= 0; walk = parent[walk])
        {
            chain.Add(walk);
            if (chain.Count > graph.Nodes.Length)
            {
                return Refusal.Malformed("The posed scene's parent chain does not terminate.");
            }
        }

        chain.Reverse();
        AnimVec3 t = AnimVec3.Zero;
        AnimQuat r = AnimQuat.Identity;
        AnimVec3 s = AnimVec3.One;

        foreach (int index in chain)
        {
            SceneNode self = graph.Nodes[index];
            AnimVec3 scaled = new(s.X * self.Translation.X, s.Y * self.Translation.Y, s.Z * self.Translation.Z);
            AnimVec3 moved = PoseSampling.Rotate(r, scaled);
            t = new AnimVec3(t.X + moved.X, t.Y + moved.Y, t.Z + moved.Z);
            r = PoseSampling.QNormalize(PoseSampling.QMultiply(r, self.Rotation));
            s = new AnimVec3(s.X * self.Scale.X, s.Y * self.Scale.Y, s.Z * self.Scale.Z);
        }

        return Result.Ok((t, r, s));
    }
}
