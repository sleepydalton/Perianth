using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Xunit;

namespace Perianth.Tests.Pose;

public sealed class SceneGraphTests
{
    [Fact]
    public void Remapping_meshes_densifies_the_kept_indices_and_clears_the_dropped()
    {
        // Three attachment nodes drawing meshes 0, 1, 2. Mesh 1 is dropped; the
        // survivors 0 and 2 become dense 0 and 1, and node 1 keeps its place with
        // no mesh so its transform and children survive.
        SceneGraph graph = new(
            [
                Attachment("a", 0),
                Attachment("b", 1),
                Attachment("c", 2),
            ],
            [0, 1, 2]);

        SceneGraph remapped = graph.RemapMeshes([0, 2], meshCount: 3);

        Assert.Equal(0, remapped.Nodes[0].Mesh);
        Assert.Null(remapped.Nodes[1].Mesh);
        Assert.Equal(1, remapped.Nodes[2].Mesh);
    }

    [Fact]
    public void A_placement_only_node_is_untouched_by_a_remap()
    {
        SceneGraph graph = new(
            [
                new SceneNode("root", [1], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, Mesh: null),
                Attachment("mesh", 0),
            ],
            [0]);

        SceneGraph remapped = graph.RemapMeshes([0], meshCount: 1);

        Assert.Null(remapped.Nodes[0].Mesh);
        Assert.Equal("root", remapped.Nodes[0].Name);
        Assert.Equal(0, remapped.Nodes[1].Mesh);
    }

    private static SceneNode Attachment(string name, int mesh) =>
        new(name, [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, mesh);
}
