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

public sealed class ClipAnimationTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"clip-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_clip_that_never_moves_exports_the_pose_it_sets_rather_than_refusing()
    {
        // The setup rests the node; the clip names it but drives nothing (all
        // sentinels) and its visibility never changes, so there is no movement
        // to emit. That is not a failure and no longer refuses.
        //
        // 632 of the game's 9,469 ANIMs are authored this way -- prop states,
        // idles, loops that hold while something else moves -- so this is an
        // ordinary authoring choice rather than a broken file. The scene already
        // holds the pose the clip sets, so the export is what was asked for
        // minus the moving part, and the pipeline says so with a warning. The
        // same file exports happily alongside another, which is what made
        // refusing here indefensible.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["root"], scai: [Active], parents: [Root]);
        AnimFile clip = Anim(hierarchy: false, sampleCount: 2, fps: 24,
            names: ["root"], scai: [Active], parents: null);

        Result<AnimatedScene> result = ClipAnimation.Animate(Model(("root", 0)), setup, clip);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Animations);
        Assert.Single(result.Value.Scene.Keep);
    }

    [Fact]
    public void Several_clips_become_several_named_animations()
    {
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);

        Result<AnimatedScene> result = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("walk", ShowsOnly(0)), new NamedClip("idle", ShowsOnly(1))]);

        Assert.True(result.IsSuccess);
        Assert.Equal(["walk", "idle"], result.Value.Animations.Select(a => a.Name));
    }

    [Fact]
    public void The_parts_are_the_union_of_what_every_clip_shows()
    {
        // Each clip shows one part and hides the other, so a scene built from
        // either alone is missing a piece under the other. Exporting a
        // front-facing idle beside a back-facing one is this, for real.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);
        GeometryModel model = Model(("a", 0), ("b", 1));

        // Alone, each of these is a pose and carries no animation at all;
        // together they are two Actions, which is the whole point.
        Assert.Empty(ClipAnimation.Animate(model, setup, ShowsOnly(0)).Value.Animations);

        Result<AnimatedScene> both = ClipAnimation.Animate(
            model, setup, [new NamedClip("a", ShowsOnly(0)), new NamedClip("b", ShowsOnly(1))]);

        Assert.Equal(2, both.Value.Scene.Keep.Length);
    }

    [Fact]
    public void A_clip_matching_the_baked_pose_still_states_it_when_another_differs()
    {
        // The scene bakes the first animation's arrangement, so the second has
        // to hide what it does not show -- and the first needs no track at all,
        // because the graph already says what it wants. Getting this wrong
        // leaves the first Action's parts showing through the second.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);

        Result<AnimatedScene> both = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("first", ShowsOnly(0)), new NamedClip("second", ShowsOnly(1))]);

        Assert.Empty(both.Value.Animations[0].Tracks);
        Assert.Equal(2, both.Value.Animations[1].Tracks.Length);
        Assert.All(both.Value.Animations[1].Tracks, t => Assert.Equal(TrackInterpolation.Step, t.Interpolation));
    }

    [Fact]
    public void Every_pinned_visibility_scenario_is_what_the_sampler_produces()
    {
        // spec_vectors.json pinned these four and nothing executed them, so the
        // file recorded the rule without holding anyone to it. They run here.
        // Each names its nodes in the setup; "child" hangs off "parent" so
        // inheritance is exercised, and every other node is a root.
        foreach (System.Text.Json.JsonElement scenario in
            SpecVectors.Group("visibility_sentinels").GetProperty("scenarios").EnumerateArray())
        {
            string what = scenario.GetProperty("name").GetString()!;

            List<string> names = [];
            List<int> scai = [];
            foreach (System.Text.Json.JsonProperty selector in scenario.GetProperty("setup").EnumerateObject())
            {
                names.Add(selector.Name);
                scai.Add(selector.Value.GetUInt16());
            }

            int[] parents = [.. names.Select(n =>
                string.Equals(n, "child", StringComparison.Ordinal) ? names.IndexOf("parent") : Root)];
            AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24, [.. names], [.. scai], parents);

            AnimFile? clip = null;
            if (scenario.GetProperty("clip").ValueKind is not System.Text.Json.JsonValueKind.Null)
            {
                List<string> clipNames = [];
                List<int> clipScai = [];
                foreach (System.Text.Json.JsonProperty selector in scenario.GetProperty("clip").EnumerateObject())
                {
                    clipNames.Add(selector.Name);
                    clipScai.Add(selector.Value.GetUInt16());
                }

                // A clip naming nothing still has to be a well-formed file, so it
                // carries one node the setup does not have.
                if (clipNames.Count == 0)
                {
                    clipNames.Add("elsewhere");
                    clipScai.Add(Active);
                }

                clip = Anim(hierarchy: false, sampleCount: 1, fps: 24, [.. clipNames], [.. clipScai], parents: null);
            }

            bool[] visible = PoseSampling.Visibility(setup, clip, 0.0).Value;
            foreach (System.Text.Json.JsonProperty expected in scenario.GetProperty("visible").EnumerateObject())
            {
                Assert.Equal(expected.Value.GetBoolean(), visible[names.IndexOf(expected.Name)]);
            }

            Assert.NotEmpty(what);
        }
    }

    [Fact]
    public void The_clips_last_declared_sample_is_a_terminator_and_is_not_played()
    {
        // A clip's final sample resets every channel keyed on it to the node's
        // rest pose, so it is the end marker rather than the end of the motion.
        // Playing it parks whatever it names where the setup keeps it, and a
        // hand is parked well away from the arm -- which is what a viewer then
        // holds for every frame after the clip. Three declared samples, two
        // played, so the timeline stops one frame interval short of the count.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);

        AnimFile clip = ShowsOnly(1);
        Assert.Equal(3, clip.SampleCount);

        AnimatedScene scene = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("first", ShowsOnly(0)), new NamedClip("second", clip)]).Value;

        Assert.Equal([0f, 1f / 24f], scene.Animations[1].Times);
    }

    [Fact]
    public void A_queue_joins_the_clips_into_one_timeline()
    {
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);

        Result<AnimatedScene> queued = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("first", ShowsOnly(0)), new NamedClip("second", ShowsOnly(1))],
            queued: true, name: "run");

        Animation one = Assert.Single(queued.Value.Animations);
        Assert.Equal("run", one.Name);

        // Two two-sample clips at 24fps become four rising instants, the second
        // clip opening one frame interval after the first closes rather than on
        // top of it. A shared instant would be two values at one time.
        Assert.Equal(4, one.Times.Length);
        Assert.Equal([0f, 1f / 24f, 2f / 24f, 3f / 24f], one.Times);
    }

    [Fact]
    public void A_queued_clip_can_be_named_twice_and_plays_twice()
    {
        // The reason the queue is a list rather than a set of ticks.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);
        AnimFile first = ShowsOnly(0);
        AnimFile second = ShowsOnly(1);

        Result<AnimatedScene> queued = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("a", first), new NamedClip("b", second), new NamedClip("a again", first)],
            queued: true, name: "run");

        Assert.Equal(6, Assert.Single(queued.Value.Animations).Times.Length);
    }

    [Fact]
    public void A_queue_switches_visibility_at_the_seam()
    {
        // The parts one clip shows and the next does not have to change exactly
        // where the clips meet -- and as a STEP, so they cut rather than fade.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["a", "b"], scai: [Active, Active], parents: [Root, Root]);

        Result<AnimatedScene> queued = ClipAnimation.Animate(
            Model(("a", 0), ("b", 1)), setup,
            [new NamedClip("first", ShowsOnly(0)), new NamedClip("second", ShowsOnly(1))],
            queued: true, name: "run");

        AnimationTrack track = Assert.Single(
            Assert.Single(queued.Value.Animations).Tracks,
            t => t.Interpolation == TrackInterpolation.Step && t.Values[0] != 0.0);

        // Visible for the first clip's two samples, hidden for the second's.
        Assert.Equal([1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0], track.Values);
    }

    [Fact]
    public void No_clips_at_all_refuses()
    {
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["root"], scai: [Active], parents: [Root]);

        Assert.True(ClipAnimation.Animate(Model(("root", 0)), setup, []).IsRefused);
    }

    [Fact]
    public void A_clip_with_no_samples_refuses()
    {
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["root"], scai: [Active], parents: [Root]);
        AnimFile clip = Anim(hierarchy: false, sampleCount: 0, fps: 24,
            names: ["root"], scai: [Active], parents: null);

        Assert.True(ClipAnimation.Animate(Model(("root", 0)), setup, clip).IsRefused);
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Active = 0xFFFF;

    /// <summary>The selector that hides a node, as against 0xFFFF's "leave alone".</summary>
    private const int Hidden = 0xFFFE;

    /// <summary>A two-node clip showing one part and hiding the other.</summary>
    // Three declared samples, so two of them play: the last sample of a clip is a
    // terminator that resets each node it keys to its rest pose, not a frame. A
    // two-sample fixture would leave one playable frame and stop testing the seam.
    private AnimFile ShowsOnly(int node) => Anim(hierarchy: false, sampleCount: 3, fps: 24,
        names: ["a", "b"], scai: node == 0 ? [Active, Hidden] : [Hidden, Active], parents: null);

    private AnimFile Anim(bool hierarchy, int sampleCount, float fps, string[] names, int[] scai, int[]? parents)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(fps));
        Write32(bytes, 0x10, sampleCount);
        Write32(bytes, 0x1C, 5);
        Write32(bytes, 0x24, names.Length);

        bytes.AddRange(Chunk("SCAI", U16(scai)));
        bytes.AddRange(Chunk("NAME", Names(names)));
        if (parents is not null)
        {
            bytes.AddRange(Chunk("PRNT", U16([.. parents.Select(p => p < 0 ? 0xFFFF : p)])));
        }

        string path = Path.Combine(_directory.FullName, $"a{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        return AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy).Value;
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
