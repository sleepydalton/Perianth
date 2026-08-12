using System.Collections.Immutable;
using System.Linq;
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

    [Fact]
    public void Pruning_keeps_what_draws_and_what_holds_it()
    {
        // A rig carries a joint for every part the game might show, and an
        // export shows one appearance: 3,865 nodes to draw 37 meshes on a real
        // character. Here, node 0 holds node 1 which draws, and node 2 holds
        // nothing at all.
        SceneGraph graph = new(
            [
                new SceneNode("root", [1, 2], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
                Attachment("drawn", 0),
                new SceneNode("empty", [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
            ],
            [0]);

        (SceneGraph pruned, _) = graph.Prune([]);

        Assert.Equal(["root", "drawn"], pruned.Nodes.Select(n => n.Name));
        Assert.Equal([1], pruned.Nodes[0].Children);
        Assert.Equal([0], pruned.Roots);
    }

    [Fact]
    public void Pruning_keeps_an_animated_node_that_draws_nothing()
    {
        // A joint the clip moves matters even with no mesh on it: its children
        // move with it, and dropping it would freeze them.
        SceneGraph graph = new(
            [
                new SceneNode("root", [1, 2], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
                new SceneNode("hip", [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
                new SceneNode("empty", [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
            ],
            [0]);

        Animation clip = new("clip", [0f, 1f],
            [new AnimationTrack(1, TrackPath.Translation, TrackInterpolation.Linear, [0, 0, 0, 1, 0, 0])]);

        (SceneGraph pruned, ImmutableArray<Animation> moved) = graph.Prune([clip]);

        Assert.Equal(["root", "hip"], pruned.Nodes.Select(n => n.Name));

        // Remapped, not merely kept: the target is a node index, and renumbering
        // the nodes without it would drive whatever landed at that index.
        Assert.Equal(1, moved[0].Tracks[0].Node);
    }

    [Fact]
    public void Pruning_a_graph_with_nothing_to_drop_changes_nothing()
    {
        SceneGraph graph = new(
            [
                new SceneNode("root", [1], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, null),
                Attachment("drawn", 0),
            ],
            [0]);

        (SceneGraph pruned, _) = graph.Prune([]);

        Assert.Same(graph, pruned);
    }

    private static SceneNode Attachment(string name, int mesh) =>
        new(name, [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, mesh);
}
