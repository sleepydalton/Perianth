using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Pose;

/// <summary>
/// Posing a model with one hierarchy and filling the rest from another.
/// </summary>
/// <remarks>
/// For a model with no setup of its own, where a relative's hierarchy poses most
/// of it and stops: a real one comes out as a correct body with no head, because
/// the donor names none of its head nodes.
/// </remarks>
public sealed class BorrowedPoseTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"borrow-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void The_donor_supplies_only_the_parts_the_primary_cannot_name()
    {
        // The primary names body and one sleeve; it has never heard of "head".
        AnimFile primary = Setup(["body", "sleeve"], [Root, Root], [Shown, Shown]);
        AnimFile donor = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        GeometryModel model = Model(("body", 0), ("sleeve", 1), ("head", 2));

        PosedScene scene = BorrowedPose.Pose(model, primary, donor, null, 0.0).Value;

        // body and sleeve from the primary, head from the donor, nothing twice.
        Assert.Equal([0, 1, 2], scene.Keep);
        Assert.Empty(scene.UnriggedParts);
    }

    [Fact]
    public void A_part_the_primary_hides_is_not_reinstated_by_the_donor()
    {
        // The rule this pins, and the reason it is "cannot name" rather than
        // "does not show". Both hierarchies name the sleeve; the primary hides
        // it. Letting the donor overturn that draws two variants of one thing --
        // which is exactly the doubled sleeve seen when this was done by hand.
        AnimFile primary = Setup(["body", "sleeve"], [Root, Root], [Shown, Hidden]);
        AnimFile donor = Setup(["body", "sleeve", "head"], [Root, Root, Root], [Shown, Shown, Shown]);
        GeometryModel model = Model(("body", 0), ("sleeve", 1), ("head", 2));

        PosedScene scene = BorrowedPose.Pose(model, primary, donor, null, 0.0).Value;

        Assert.Contains(0, scene.Keep);
        Assert.Contains(2, scene.Keep);
        Assert.DoesNotContain(1, scene.Keep);
    }

    [Fact]
    public void A_borrowed_part_is_placed_where_the_donor_puts_it()
    {
        // Borrowed parts are placed rather than parented: there is no node in the
        // primary's tree to hang them from, so each carries the donor's world
        // transform and sits at the scene root.
        AnimFile primary = Setup(["body"], [Root], [Shown]);
        AnimFile donor = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        GeometryModel model = Model(("body", 0), ("head", 1));

        PosedScene scene = BorrowedPose.Pose(model, primary, donor, null, 0.0).Value;

        SceneNode borrowed = scene.Graph.Nodes[^1];
        Assert.Equal("label:head", borrowed.Name);
        Assert.Contains(scene.Graph.Nodes.Length - 1, scene.Graph.Roots);
        Assert.NotNull(borrowed.Mesh);
    }

    [Fact]
    public void A_donor_with_nothing_to_add_returns_the_primary_pose_unchanged()
    {
        AnimFile primary = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        AnimFile donor = Setup(["body"], [Root], [Shown]);
        GeometryModel model = Model(("body", 0), ("head", 1));

        PosedScene scene = BorrowedPose.Pose(model, primary, donor, null, 0.0).Value;
        PosedScene alone = SetupPose.Pose(model, primary, null, 0.0, allowMissingParts: true).Value;

        Assert.Equal(alone.Keep, scene.Keep);
        Assert.Equal(alone.Graph.Nodes.Length, scene.Graph.Nodes.Length);
    }

    [Fact]
    public void Disagreement_separates_a_compatible_donor_from_a_scattered_one()
    {
        // The check that stops a donor being chosen on name coverage alone: the
        // hierarchy naming the most of one model's head placed its parts tens of
        // units apart, because it poses a crowd rather than a character.
        // Built directly rather than through an ANIM, because what is under test
        // is the comparison of two posed scenes, not how either was produced.
        PosedScene a = Scene(0.0);
        PosedScene b = Scene(0.0);
        PosedScene scattered = Scene(40.0);

        Assert.Equal(0.0, BorrowedPose.Disagreement(a, b)!.Value.Worst, 6);
        Assert.Equal(40.0, BorrowedPose.Disagreement(a, scattered)!.Value.Worst, 6);
        Assert.Null(BorrowedPose.Disagreement(a, Scene(0.0, part: 7)));
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Shown = 0xFFFF;
    private const int Hidden = 0xFFFE;

    /// <summary>A one-part posed scene whose part sits at <paramref name="x"/>.</summary>
    private static PosedScene Scene(double x, int part = 0) => new(
        [part],
        new SceneGraph(
            [
                new SceneNode("root", [1], new AnimVec3(x, 0, 0), AnimQuat.Identity, AnimVec3.One, null),
                new SceneNode("label:body", [], AnimVec3.Zero, AnimQuat.Identity, AnimVec3.One, 0),
            ],
            [0]),
        []);

    private AnimFile Setup(string[] names, int[] parents, int[] scai)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(24f));
        Write32(bytes, 0x10, 1);
        Write32(bytes, 0x1C, 5);
        Write32(bytes, 0x24, names.Length);

        bytes.AddRange(Encoding.ASCII.GetBytes("SCAI"));
        foreach (int v in scai)
        {
            bytes.Add((byte)(v & 0xFF));
            bytes.Add((byte)(v >> 8 & 0xFF));
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("PRNT"));
        foreach (int p in parents)
        {
            int v = p < 0 ? 0xFFFF : p;
            bytes.Add((byte)(v & 0xFF));
            bytes.Add((byte)(v >> 8 & 0xFF));
        }

        string path = Path.Combine(_directory.FullName, $"s{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        return AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy: true).Value;
    }

    private static GeometryModel Model(params (string Binding, int Ordinal)[] parts) =>
        new(3,
        [
            .. parts.Select(p => new GeometryPart(
                p.Ordinal,
                $"mode3-record-{p.Ordinal}",
                $"label:{p.Binding}",
                p.Binding,
                [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
                [0, 1, 2],
                [],
                [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)])),
        ], false);

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
