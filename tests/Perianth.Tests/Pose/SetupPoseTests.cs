using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Pose;

public sealed class SetupPoseTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"pose-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_part_under_a_hidden_ancestor_is_not_kept()
    {
        // shown is visible; belowhidden hangs under a hidden parent, so ancestor
        // visibility hides it even though its own SCAI is active.
        AnimFile setup = Setup(
            names: ["root", "shown", "hiddenparent", "belowhidden"],
            parents: [Root, 0, 0, 2],
            scai: [Active, Active, Hidden, Active]);

        GeometryModel model = Model(("shown", 0), ("belowhidden", 1));

        PosedScene pose = SetupPose.Pose(model, setup, null, 0.0).Value;

        Assert.Equal([0], pose.Keep);
        Assert.Empty(pose.UnriggedParts);
    }

    [Fact]
    public void A_part_the_hierarchy_does_not_name_is_omitted_below_the_share_limit()
    {
        // Nine of ten parts rig; one does not. A tenth is exactly the limit, which
        // is not exceeded, so it is omitted and reported rather than refused.
        AnimFile setup = Setup(
            names: ["n0", "n1", "n2", "n3", "n4", "n5", "n6", "n7", "n8"],
            parents: [Root, Root, Root, Root, Root, Root, Root, Root, Root],
            scai: [Active, Active, Active, Active, Active, Active, Active, Active, Active]);

        (string, int)[] bindings = Enumerable.Range(0, 9).Select(i => ($"n{i}", i)).Append(("orphan", 9)).ToArray();
        GeometryModel model = Model(bindings);

        PosedScene pose = SetupPose.Pose(model, setup, null, 0.0).Value;

        Assert.DoesNotContain(9, pose.Keep);
        Assert.Single(pose.UnriggedParts);
    }

    [Fact]
    public void A_setup_that_names_too_few_parts_refuses_as_a_foreign_rig()
    {
        // Two of five parts are unrigged, past a tenth, so this setup does not
        // describe this model and the whole export refuses.
        AnimFile setup = Setup(
            names: ["n0", "n1", "n2"],
            parents: [Root, Root, Root],
            scai: [Active, Active, Active]);

        GeometryModel model = Model(("n0", 0), ("n1", 1), ("n2", 2), ("orphanA", 3), ("orphanB", 4));

        Assert.True(SetupPose.Pose(model, setup, null, 0.0).IsRefused);
    }

    [Fact]
    public void A_time_a_still_setup_cannot_reach_refuses_as_unsupported()
    {
        // The setup stores no samples, so only time 0 can be asked of it: a
        // nonzero --time is a request the file cannot satisfy, not a fault in it.
        AnimFile setup = Setup(names: ["root"], parents: [Root], scai: [Active]);
        GeometryModel model = Model(("root", 0));

        Result<PosedScene> result = SetupPose.Pose(model, setup, null, 2.5);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void An_attachment_node_hangs_beneath_its_setup_node_with_the_source_label()
    {
        AnimFile setup = Setup(names: ["root", "hand"], parents: [Root, 0], scai: [Active, Active]);
        GeometryModel model = Model(("hand", 0));

        PosedScene pose = SetupPose.Pose(model, setup, null, 0.0).Value;

        // Two setup nodes then one attachment; the attachment sits under "hand".
        Assert.Equal(3, pose.Graph.Nodes.Length);
        SceneNode attachment = pose.Graph.Nodes[2];
        Assert.Equal(0, attachment.Mesh);
        Assert.Equal("label:hand", attachment.Name);
        Assert.Contains(2, pose.Graph.Nodes[1].Children);
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Active = 0xFFFF;
    private const int Hidden = 0xFFFE;

    private AnimFile Setup(string[] names, int[] parents, int[] scai)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x1C, 5);              // rotation layout selector
        Write32(bytes, 0x24, names.Length);   // node count

        bytes.AddRange(Chunk("SCAI", U16(scai)));
        bytes.AddRange(Chunk("NAME", Names(names)));
        bytes.AddRange(Chunk("PRNT", U16([.. parents.Select(p => p < 0 ? 0xFFFF : p)])));

        string path = Path.Combine(_directory.FullName, $"s{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        return AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy: true).Value;
    }

    private static GeometryModel Model(params (string Binding, int Ordinal)[] parts)
    {
        ImmutableArray<GeometryPart> built =
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
        ];

        return new GeometryModel(3, built, false);
    }

    private static byte[] Chunk(string tag, byte[] payload) => [.. Encoding.ASCII.GetBytes(tag), .. payload];

    private static byte[] U16(int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] Names(string[] names)
    {
        List<byte> bytes = [];
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
