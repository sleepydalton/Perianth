using System.Collections.Immutable;
using System.Linq;
using Perianth.Formats.Anim;

namespace Perianth.Core.Pose;

/// <summary>
/// One node in a posed scene: a placed hierarchy node, or a mesh attachment
/// beneath one.
/// </summary>
/// <remarks>
/// This is neutral. It carries local transforms and a child list, not a glTF
/// node — the writer decides what an identity transform or a mesh reference
/// becomes. A setup node carries a transform and children and no mesh; an
/// attachment node carries a mesh and sits beneath its setup node.
/// </remarks>
/// <param name="Name">The node's name: a setup node's own name, or a mesh part's source label.</param>
/// <param name="Children">Indices of child nodes, setup children then attachments.</param>
/// <param name="Translation">Local translation.</param>
/// <param name="Rotation">Local rotation.</param>
/// <param name="Scale">Local scale.</param>
/// <param name="Mesh">The mesh this node draws, or null for a placement-only node.</param>
public sealed record SceneNode(
    string Name,
    ImmutableArray<int> Children,
    AnimVec3 Translation,
    AnimQuat Rotation,
    AnimVec3 Scale,
    int? Mesh);

/// <summary>
/// A posed scene: a node hierarchy and its roots, addressing meshes by index.
/// </summary>
/// <remarks>
/// The mesh indices are dense and refer to the drawn geometry's parts in order,
/// so a scene graph and the model it was posed from stay aligned without a
/// side table.
/// </remarks>
public sealed record SceneGraph(ImmutableArray<SceneNode> Nodes, ImmutableArray<int> Roots)
{
    /// <summary>
    /// Rewrites the mesh indices after some meshes were dropped, keeping every
    /// node in place.
    /// </summary>
    /// <remarks>
    /// A later stage — the emissive merge — removes meshes the pose had already
    /// attached. Their old indices are remapped to dense new ones over
    /// <paramref name="kept"/>, in that order; a node whose mesh is gone keeps its
    /// transform and children but draws nothing, so the hierarchy it anchors
    /// survives. <paramref name="kept"/> lists the surviving mesh indices in draw
    /// order, and <paramref name="meshCount"/> is how many meshes there were.
    /// </remarks>
    /// <summary>
    /// Drops nodes that draw nothing, animate nothing, and hold nothing that
    /// does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A source hierarchy is a rig, and a rig carries a joint for every part the
    /// game might show. An export shows one appearance, so nearly all of them
    /// end up empty: a main character export carries <b>3,865 nodes to draw 37
    /// meshes</b>, and 92 of those nodes are on a path to a mesh. The rest hold
    /// nothing.
    /// </para>
    /// <para>
    /// Dropping them is safe here and would not be in a skinned file. Nothing is
    /// skinned — parts are parented to nodes and no primitive carries
    /// <c>JOINTS_0</c> — so a node with no mesh, no animation and no meshed
    /// descendant has no effect on what is drawn. In a skinned file the same
    /// node would be a joint a vertex weights against, and removing it would
    /// deform the model.
    /// </para>
    /// <para>
    /// Animations are remapped rather than dropped: their targets are node
    /// indices, and renumbering the nodes without renumbering the targets would
    /// drive whatever landed at that index instead.
    /// </para>
    /// </remarks>
    public (SceneGraph Graph, ImmutableArray<Animation> Animations) Prune(
        ImmutableArray<Animation> animations)
    {
        int[] parent = new int[Nodes.Length];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = -1;
        }

        for (int i = 0; i < Nodes.Length; i++)
        {
            foreach (int child in Nodes[i].Children)
            {
                parent[child] = i;
            }
        }

        bool[] keep = new bool[Nodes.Length];
        void Need(int node)
        {
            for (int i = node; i >= 0 && !keep[i]; i = parent[i])
            {
                keep[i] = true;
            }
        }

        for (int i = 0; i < Nodes.Length; i++)
        {
            if (Nodes[i].Mesh is not null)
            {
                Need(i);
            }
        }

        if (!animations.IsDefaultOrEmpty)
        {
            foreach (Animation animation in animations)
            {
                foreach (AnimationTrack track in animation.Tracks)
                {
                    if (track.Node >= 0 && track.Node < Nodes.Length)
                    {
                        Need(track.Node);
                    }
                }
            }
        }

        int[] renumbered = new int[Nodes.Length];
        int next = 0;
        for (int i = 0; i < Nodes.Length; i++)
        {
            renumbered[i] = keep[i] ? next++ : -1;
        }

        if (next == Nodes.Length)
        {
            return (this, animations);
        }

        ImmutableArray<SceneNode>.Builder nodes = ImmutableArray.CreateBuilder<SceneNode>(next);
        for (int i = 0; i < Nodes.Length; i++)
        {
            if (!keep[i])
            {
                continue;
            }

            // A kept node's children are those of its children that survived.
            // Their own transforms are unchanged, so what is left hangs exactly
            // where it did.
            nodes.Add(Nodes[i] with
            {
                Children = [.. Nodes[i].Children.Where(c => keep[c]).Select(c => renumbered[c])],
            });
        }

        ImmutableArray<Animation> moved = animations.IsDefaultOrEmpty
            ? animations
            :
            [
                .. animations.Select(animation => animation with
                {
                    Tracks =
                    [
                        .. animation.Tracks
                            .Where(track => track.Node >= 0 && track.Node < renumbered.Length && renumbered[track.Node] >= 0)
                            .Select(track => track with { Node = renumbered[track.Node] }),
                    ],
                }),
            ];

        return (new SceneGraph(nodes.MoveToImmutable(), [.. Roots.Where(r => keep[r]).Select(r => renumbered[r])]), moved);
    }

    public SceneGraph RemapMeshes(ImmutableArray<int> kept, int meshCount)
    {
        int[] newIndexOf = new int[meshCount];
        for (int i = 0; i < meshCount; i++)
        {
            newIndexOf[i] = -1;
        }

        for (int i = 0; i < kept.Length; i++)
        {
            newIndexOf[kept[i]] = i;
        }

        ImmutableArray<SceneNode>.Builder rebuilt = ImmutableArray.CreateBuilder<SceneNode>(Nodes.Length);
        foreach (SceneNode node in Nodes)
        {
            int? mesh = node.Mesh is int m && newIndexOf[m] >= 0 ? newIndexOf[m] : null;
            rebuilt.Add(node with { Mesh = mesh });
        }

        return this with { Nodes = rebuilt.MoveToImmutable() };
    }
}
