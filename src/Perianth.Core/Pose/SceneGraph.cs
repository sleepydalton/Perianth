using System.Collections.Immutable;
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
