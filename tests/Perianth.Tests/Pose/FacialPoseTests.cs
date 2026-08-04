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

/// <summary>
/// A facial atlas overlays the body pose, overriding only the channels it
/// animates on nodes the setup names. These build synthetic setups and atlases
/// to exercise the overlay and the two refusals the overlay enforces.
/// </summary>
public sealed class FacialPoseTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"facial-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_fixed_layer_overrides_the_translation_of_a_named_node()
    {
        // The setup leaves "jaw" at the identity; the mouth atlas animates its
        // translation to x=16, so the posed node carries the atlas's value.
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));

        GeometryModel model = Model(("jaw", 0));
        PosedScene pose = FacialPose.Pose(model, setup, null, 0.0, [FacialLayer.Fixed("mouth", atlas, 0)]).Value;

        SceneNode jaw = pose.Graph.Nodes.Single(n => n.Name == "jaw");
        Assert.Equal(16.0, jaw.Translation.X, 9);
    }

    [Fact]
    public void A_suppressed_translation_keeps_the_nodes_composed_placement()
    {
        // mesh-neutral: the pupil atlas animates translation, but suppressing it
        // leaves the node at the setup's identity — its mesh-authored placement —
        // rather than the atlas's x=16.
        AnimFile setup = Setup(names: ["root", "pupil"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("pupil")));

        GeometryModel model = Model(("pupil", 0));
        PosedScene pose = FacialPose.Pose(
            model, setup, null, 0.0, [FacialLayer.Fixed("pupils", atlas, 0, suppressTranslation: true)]).Value;

        SceneNode pupil = pose.Graph.Nodes.Single(n => n.Name == "pupil");
        Assert.Equal(0.0, pupil.Translation.X, 9);
    }

    [Fact]
    public void A_channel_the_atlas_leaves_static_is_not_overridden()
    {
        // The atlas names "jaw" but its only animated channel is translation, so
        // the untouched rotation stays the setup's identity.
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));

        GeometryModel model = Model(("jaw", 0));
        PosedScene pose = FacialPose.Pose(model, setup, null, 0.0, [FacialLayer.Fixed("mouth", atlas, 0)]).Value;

        SceneNode jaw = pose.Graph.Nodes.Single(n => n.Name == "jaw");
        Assert.Equal(AnimQuat.Identity, jaw.Rotation);
    }

    [Fact]
    public void A_layer_that_names_nothing_the_setup_has_is_refused()
    {
        // Every animated target is a reusable name the setup does not carry, so
        // the layer participates in nothing and would silently do nothing.
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("elsewhere")));

        GeometryModel model = Model(("jaw", 0));
        Result<PosedScene> pose = FacialPose.Pose(model, setup, null, 0.0, [FacialLayer.Fixed("mouth", atlas, 0)]);

        Assert.True(pose.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, pose.Refusal.Kind);
    }

    [Fact]
    public void A_reusable_target_absent_from_the_setup_is_ignored_not_refused()
    {
        // "spare" is a reusable library target this character does not carry; the
        // layer still participates through "jaw", so it is applied, not refused.
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 2,
            ("TRAI", Selectors(0, 1)),
            ("__RAW__", Concat(
                Chunk("TRAD", Concat(Fixed3(16, 0, 0), Fixed3(32, 0, 0), Fixed3(48, 0, 0))),
                Chunk("CHAK", Selectors(2)),
                Chunk("CAKS", Selectors(0, 1, 1)))),
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", NameBytes("jaw", "spare")));

        GeometryModel model = Model(("jaw", 0));
        Result<PosedScene> pose = FacialPose.Pose(model, setup, null, 0.0, [FacialLayer.Fixed("mouth", atlas, 0)]);

        Assert.True(pose.IsSuccess);
    }

    [Fact]
    public void Two_layers_driving_the_same_node_and_channel_are_refused()
    {
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile first = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));
        AnimFile second = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(32, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));

        GeometryModel model = Model(("jaw", 0));
        Result<PosedScene> pose = FacialPose.Pose(
            model, setup, null, 0.0,
            [FacialLayer.Fixed("mouth", first, 0), FacialLayer.Fixed("eyes", second, 0)]);

        Assert.True(pose.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, pose.Refusal.Kind);
    }

    [Fact]
    public void A_state_the_atlas_does_not_hold_is_refused()
    {
        // The builder writes eight samples, so sample 8 is one past the end.
        AnimFile setup = Setup(names: ["root", "jaw"], parents: [Root, 0], scai: [Active, Active]);
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(16, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));

        GeometryModel model = Model(("jaw", 0));
        Result<PosedScene> pose = FacialPose.Pose(model, setup, null, 0.0, [FacialLayer.Fixed("mouth", atlas, 8)]);

        Assert.True(pose.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, pose.Refusal.Kind);
    }

    [Fact]
    public void An_animated_clip_overlays_the_resting_pose_and_tracks_only_moving_channels()
    {
        // The body clip moves "a" (16 then 32 from sample 2) and holds "b" (48);
        // a fixed mouth overlays "jaw". The overlay shows in the resting pose, the
        // moving channel gets a track, and the constant ones do not.
        AnimFile setup = Setup(names: ["root", "a", "b", "jaw"], parents: [Root, 0, 0, 0], scai: [Active, Active, Active, Active]);
        AnimFile clip = Atlas(nodeCount: 2,
            ("TRAI", Selectors(0, 1)),
            ("__RAW__", Concat(
                Chunk("TRAD", Concat(Fixed3(16, 0, 0), Fixed3(32, 0, 0), Fixed3(48, 0, 0))),
                Chunk("CHAK", Selectors(2)),
                Chunk("CAKS", Selectors(0, 1, 1)))),
            ("SCAI", Selectors(0xFFFF, 0xFFFF)),
            ("NAME", NameBytes("a", "b")));
        AnimFile mouth = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Fixed3(64, 0, 0)))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("jaw")));

        GeometryModel model = Model(("a", 0), ("b", 1), ("jaw", 2));
        AnimatedScene scene = FacialAnimation.Animate(model, setup, clip, [FacialLayer.Fixed("mouth", mouth, 0)]).Value;

        SceneNode jaw = scene.Scene.Graph.Nodes.Single(n => n.Name == "jaw");
        Assert.Equal(64.0, jaw.Translation.X, 9);

        System.Collections.Generic.HashSet<int> tracked = [.. scene.Animation.Tracks.Select(t => t.Node)];
        Assert.Contains(1, tracked);        // "a" moves
        Assert.DoesNotContain(2, tracked);  // "b" is constant
        Assert.DoesNotContain(3, tracked);  // "jaw" overlay is constant
    }

    [Fact]
    public void Sample_at_selects_by_interval_and_falls_back_to_the_default()
    {
        FacialLayer layer = new("mouth", null!, [new FacialInterval(1.0, 2.0, 5)], DefaultSample: 20, SuppressTranslation: false);

        Assert.Equal(20, layer.SampleAt(0.5));   // before the interval -> default
        Assert.Equal(5, layer.SampleAt(1.0));    // inclusive start
        Assert.Equal(20, layer.SampleAt(2.0));   // exclusive end -> default
    }

    [Fact]
    public void A_lipsync_layer_builds_half_open_intervals_and_a_sample_20_fallback()
    {
        // Pairs walked pairwise: [0,10) at selector 5-1, the zero-length [10,10)
        // dropped, [10,20) at selector 3-1; the last pair is an endpoint alone.
        FacialLayer layer = FacialLayer.Lipsync(null!, [(0, 5), (10, 10), (10, 3), (20, 1)]);

        Assert.Equal(2, layer.Intervals.Length);
        Assert.Equal(20, layer.DefaultSample);
        Assert.Equal(4, layer.SampleAt(0.0));            // 0 in [0/24, 10/24)
        Assert.Equal(2, layer.SampleAt(11.0 / 24.0));    // in [10/24, 20/24)
        Assert.Equal(20, layer.SampleAt(1.0));           // past the end -> fallback
    }

    [Fact]
    public void Blink_holds_1_12_second_at_each_sorted_start_from_the_eye_state()
    {
        AnimFile atlas = Atlas(nodeCount: 1,
            ("TRAI", Selectors(0)),
            ("__RAW__", Concat(Chunk("TRAD", Concat(Fixed3(0, 0, 0), Fixed3(8, 0, 0))))),
            ("SCAI", Selectors(0xFFFF)),
            ("NAME", NameBytes("eye")));

        FacialLayer layer = FacialLayer.Blink(atlas, [1.0, 0.5], defaultSample: 0).Value;

        Assert.True(layer.RequireCompleteIntervals);
        Assert.Equal(0, layer.DefaultSample);
        Assert.Equal(2, layer.Intervals.Length);
        // Sorted, each a 1/12-second hold on atlas sample 1, boundaries on the float32 grid.
        Assert.Equal((float)0.5, (float)layer.Intervals[0].Start);
        Assert.Equal((float)(0.5 + (1.0 / 12.0)), (float)layer.Intervals[0].End);
        Assert.Equal(1, layer.Intervals[0].Sample);
        Assert.Equal((float)1.0, (float)layer.Intervals[1].Start);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.PositiveInfinity)]
    public void A_blink_that_is_not_finite_and_nonnegative_is_malformed(double start)
    {
        AnimFile atlas = EyeAtlas();

        Result<FacialLayer> layer = FacialLayer.Blink(atlas, [start], defaultSample: null);

        Assert.True(layer.IsRefused);
        Assert.Equal(RefusalKind.Malformed, layer.Refusal.Kind);
    }

    [Fact]
    public void Overlapping_blinks_are_malformed()
    {
        // 0.5 holds to ~0.583, so a second blink at 0.52 overlaps it.
        Result<FacialLayer> layer = FacialLayer.Blink(EyeAtlas(), [0.5, 0.52], defaultSample: null);

        Assert.True(layer.IsRefused);
        Assert.Equal(RefusalKind.Malformed, layer.Refusal.Kind);
    }

    [Fact]
    public void A_blink_past_the_body_clips_end_is_refused()
    {
        // The clip is eight samples at 30 fps, so it ends at 7/30 ≈ 0.233s; a blink
        // at 0.3 holds past that.
        AnimFile setup = Setup(names: ["root", "body", "eye"], parents: [Root, 0, 0], scai: [Active, Active, Active]);
        AnimFile clip = ChangingClip();
        AnimFile eyes = EyeAtlas();
        GeometryModel model = Model(("body", 0), ("eye", 1));

        FacialLayer blink = FacialLayer.Blink(eyes, [0.3], defaultSample: null).Value;
        Result<AnimatedScene> result = FacialAnimation.Animate(model, setup, clip, [blink]);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void An_off_grid_blink_boundary_enters_the_animation_timeline()
    {
        // The clip supplies eight sample times; an in-range blink off the 1/30 grid
        // adds its two boundaries, so the timeline grows by two.
        AnimFile setup = Setup(names: ["root", "body", "eye"], parents: [Root, 0, 0], scai: [Active, Active, Active]);
        AnimFile clip = ChangingClip();
        AnimFile eyes = EyeAtlas();
        GeometryModel model = Model(("body", 0), ("eye", 1));

        FacialLayer blink = FacialLayer.Blink(eyes, [0.11], defaultSample: null).Value;
        AnimatedScene scene = FacialAnimation.Animate(model, setup, clip, [blink]).Value;

        Assert.Equal(10, scene.Animation.Times.Length);
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Active = 0xFFFF;

    /// <summary>An eyes atlas whose one node animates translation, so a blink participates.</summary>
    private AnimFile EyeAtlas() => Atlas(nodeCount: 1,
        ("TRAI", Selectors(0)),
        ("__RAW__", Concat(Chunk("TRAD", Concat(Fixed3(0, 0, 0), Fixed3(8, 0, 0))))),
        ("SCAI", Selectors(0xFFFF)),
        ("NAME", NameBytes("eye")));

    /// <summary>A body clip whose "body" node changes translation at sample 2, so it yields a track.</summary>
    private AnimFile ChangingClip() => Atlas(nodeCount: 2,
        ("TRAI", Selectors(0, 1)),
        ("__RAW__", Concat(
            Chunk("TRAD", Concat(Fixed3(16, 0, 0), Fixed3(32, 0, 0), Fixed3(48, 0, 0))),
            Chunk("CHAK", Selectors(2)),
            Chunk("CAKS", Selectors(0, 1, 1)))),
        ("SCAI", Selectors(0xFFFF, 0xFFFF)),
        ("NAME", NameBytes("body", "other")));

    private AnimFile Setup(string[] names, int[] parents, int[] scai)
    {
        List<byte> bytes = Header(nodeCount: names.Length, sampleCount: 0);
        bytes.AddRange(Chunk("SCAI", Selectors(scai)));
        bytes.AddRange(Chunk("NAME", NameBytes(names)));
        bytes.AddRange(Chunk("PRNT", Selectors([.. parents.Select(p => p < 0 ? 0xFFFF : p)])));
        return ReadAnim(bytes, hierarchy: true);
    }

    private AnimFile Atlas(int nodeCount, params (string Tag, byte[] Payload)[] chunks)
    {
        List<byte> bytes = Header(nodeCount, sampleCount: 8);
        foreach ((string tag, byte[] payload) in chunks)
        {
            bytes.AddRange(tag == "__RAW__" ? payload : Chunk(tag, payload));
        }

        return ReadAnim(bytes, hierarchy: false);
    }

    private static List<byte> Header(int nodeCount, int sampleCount)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(30.0f));
        Write32(bytes, 0x10, sampleCount);
        Write32(bytes, 0x1C, 5);            // rotation layout selector
        Write32(bytes, 0x24, nodeCount);
        return bytes;
    }

    private AnimFile ReadAnim(List<byte> bytes, bool hierarchy)
    {
        string path = Path.Combine(_directory.FullName, $"a{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        Result<AnimFile> result = AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
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

    private static byte[] Chunk(string tag, byte[] payload) => Concat(Encoding.ASCII.GetBytes(tag), payload);

    private static byte[] Concat(params byte[][] parts)
    {
        List<byte> bytes = [];
        foreach (byte[] part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    private static byte[] Selectors(params int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] NameBytes(params string[] names)
    {
        List<byte> bytes = [];
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] Fixed3(int x, int y, int z)
    {
        byte[] entry = new byte[8];
        WriteI16(entry, 0, (short)x);
        WriteI16(entry, 2, (short)y);
        WriteI16(entry, 4, (short)z);
        entry[6] = 0x0F; // packed: exponent 15, low nibbles 0 -> component equals the high word
        entry[7] = 0x00;
        return entry;
    }

    private static void WriteI16(byte[] target, int offset, short value)
    {
        target[offset] = (byte)(value & 0xFF);
        target[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
