using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// A posed scene together with the animations that drive it.
/// </summary>
/// <param name="Scene">The resting pose — the first animation's first sample — and its hierarchy.</param>
/// <param name="Animations">
/// The animations, in the order asked for, their tracks addressing the scene's
/// nodes. One is the ordinary case; several become several Actions in Blender.
/// </param>
public sealed record AnimatedScene(PosedScene Scene, ImmutableArray<Animation> Animations);

/// <summary>
/// One animation to attach, and the name it will carry in the exported file.
/// </summary>
/// <remarks>
/// The name comes from the caller because an ANIM does not contain one, and
/// because it is the only handle a user has: a viewer lists Actions by name, so
/// a path would be unreadable and an ordinal would be meaningless. Core will not
/// invent one from a file path — that is a front end's business.
/// </remarks>
/// <param name="Name">What the animation is called in the exported file.</param>
/// <param name="Animation">The clip ANIM itself.</param>
public sealed record NamedClip(string Name, AnimFile Animation);

/// <summary>
/// Attaches a clip as native local-TRS animation, one track per channel the clip
/// drives, plus a stepped visibility track for any mesh it switches over time.
/// </summary>
/// <remarks>
/// The resting pose is the clip's first sample, so a viewer that ignores the
/// animation still shows a coherent frame. A channel gets a track only where the
/// clip's own selector animates it; a channel the clip leaves alone keeps the
/// setup's value and no track. Visibility that changes across the clip becomes a
/// STEP scale on the mesh's attachment node, leaving the authored transforms
/// intact. Quaternion keys are made sign-continuous so the shortest path is taken
/// between them.
/// </remarks>
public static class ClipAnimation
{
    /// <summary>
    /// The name a lone animation carries, kept because it is observable output:
    /// it is what a viewer's Action list shows, and what the baseline records.
    /// </summary>
    private const string SingleName = "clip";

    /// <summary>
    /// At or above this, the selector is a marker rather than a value: 0xFFFF
    /// leaves the channel alone and 0xFFFE hides the node. Neither names a
    /// transform to read, and hiding is carried by the visibility tracks instead.
    /// </summary>
    private const ushort Marker = 0xFFFE;

    /// <summary>At or above this, the selector names one value rather than a stream.</summary>
    private const ushort Constant = 0x8000;

    private static readonly AnimChannel[] Channels = [AnimChannel.Translation, AnimChannel.Rotation, AnimChannel.Scale];
    private static readonly TrackPath[] Paths = [TrackPath.Translation, TrackPath.Rotation, TrackPath.Scale];

    /// <summary>Animates <paramref name="model"/> under <paramref name="setup"/> with <paramref name="clip"/>.</summary>
    public static Result<AnimatedScene> Animate(GeometryModel model, AnimFile setup, AnimFile clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Animate(model, setup, [new NamedClip(SingleName, clip)]);
    }

    /// <summary>
    /// Animates <paramref name="model"/> under <paramref name="setup"/> with every
    /// animation in <paramref name="clips"/>, as one scene carrying several.
    /// </summary>
    /// <remarks>
    /// Two things differ from the single case, and both follow from one scene
    /// having to serve every animation at once.
    /// <para>
    /// <b>The parts are the union.</b> Each animation shows its own set — a
    /// character's front-facing idle and its back-facing one do not agree — so a
    /// scene built from any one of them would be missing pieces under the others.
    /// The mesh set is therefore every part any animation shows, and each
    /// animation hides what it does not use through its visibility tracks.
    /// </para>
    /// <para>
    /// <b>A channel any animation sets gets a track in all of them.</b> With one
    /// animation the constants are baked into the scene graph and need no track.
    /// With several, the graph can only hold the first one's, so an animation that
    /// leaves a channel alone must still state the value it wants, or the previous
    /// Action's pose would show through. The union is taken over what each
    /// animation <em>sets</em> — anything but a sentinel — so channels no
    /// animation touches still cost nothing.
    /// </para>
    /// </remarks>
    public static Result<AnimatedScene> Animate(
        GeometryModel model, AnimFile setup, ImmutableArray<NamedClip> clips,
        bool queued = false, string name = "queue", bool allowMissingParts = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(setup);

        if (clips.IsDefaultOrEmpty)
        {
            return Refusal.Unsupported("No animation was given to animate with.");
        }

        foreach (NamedClip named in clips)
        {
            if (named.Animation.SampleCount == 0)
            {
                return Refusal.Unsupported("The clip ANIM contains no stored samples.");
            }

            if (!(double.IsFinite(named.Animation.Fps) && named.Animation.Fps > 0.0))
            {
                return Refusal.Malformed("The ANIM sampling rate is invalid.");
            }
        }

        Result<PoseSampling.Association> association =
            PoseSampling.Associate(model, setup, allowMissingParts);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        // Every animation's every sample, indexed [animation][sample][node].
        PoseSampling.LocalPose[][][] sampledPer = new PoseSampling.LocalPose[clips.Length][][];
        bool[][][] visiblePer = new bool[clips.Length][][];
        for (int c = 0; c < clips.Length; c++)
        {
            AnimFile clip = clips[c].Animation;
            PoseSampling.LocalPose[][] sampled = new PoseSampling.LocalPose[clip.PlayableSamples][];
            bool[][] visible = new bool[clip.PlayableSamples][];
            for (int sample = 0; sample < clip.PlayableSamples; sample++)
            {
                // Double throughout: dividing by the float fps loses precision and can
                // push the last sample a hair past the end when multiplied back.
                double seconds = sample / (double)clip.Fps;

                Result<PoseSampling.LocalPose[]> pose = PoseSampling.PoseValues(setup, clip, seconds);
                if (!pose.TryGetValue(out PoseSampling.LocalPose[]? local, out Refusal? poseRefusal))
                {
                    return poseRefusal;
                }

                Result<bool[]> vis = PoseSampling.Visibility(setup, clip, seconds);
                if (!vis.TryGetValue(out bool[]? visibility, out Refusal? visRefusal))
                {
                    return visRefusal;
                }

                sampled[sample] = local;
                visible[sample] = visibility;
            }

            sampledPer[c] = sampled;
            visiblePer[c] = visible;
        }

        Result<bool> composed = PoseSampling.ValidateWorldComposition(setup, sampledPer[0][0]);
        if (!composed.IsSuccess)
        {
            return composed.Refusal;
        }

        // A part is drawn if it is rigged and visible in at least one sample of at
        // least one animation.
        ImmutableArray<int>.Builder keepBuilder = ImmutableArray.CreateBuilder<int>();
        for (int part = 0; part < model.Parts.Length; part++)
        {
            int node = bindings.NodeOfPart[part];
            if (node < 0)
            {
                continue;
            }

            bool everVisible = false;
            for (int c = 0; c < clips.Length && !everVisible; c++)
            {
                foreach (bool[] visibility in visiblePer[c])
                {
                    if (visibility[node])
                    {
                        everVisible = true;
                        break;
                    }
                }
            }

            if (everVisible)
            {
                keepBuilder.Add(part);
            }
        }

        ImmutableArray<int> keep = keepBuilder.ToImmutable();

        Result<bool> hidden = PoseSampling.RefuseIfClipHidEverything(setup, clips[0].Animation, keep, bindings.NodeOfPart, withoutClipSeconds: 0.0);
        if (!hidden.IsSuccess)
        {
            return hidden.Refusal;
        }

        if (keep.Length == 0)
        {
            return Refusal.Unsupported(
                "The setup hierarchy and visibility select no mesh parts.",
                DiagnosticIds.PoseSelectsNothing);
        }

        // The scene bakes the first animation's opening frame, so a viewer that
        // ignores animation still shows a coherent one.
        AnimVec3[] attachmentScales = new AnimVec3[keep.Length];
        for (int attachment = 0; attachment < keep.Length; attachment++)
        {
            attachmentScales[attachment] =
                visiblePer[0][0][bindings.NodeOfPart[keep[attachment]]] ? AnimVec3.One : AnimVec3.Zero;
        }

        bool[]? forced = clips.Length > 1 ? ChannelsAnySets(setup, clips) : null;

        ImmutableArray<Animation>.Builder animations = ImmutableArray.CreateBuilder<Animation>(queued ? 1 : clips.Length);
        int totalTracks = 0;

        if (queued && clips.Length > 1)
        {
            // One timeline: every clip's samples end to end, each starting a frame
            // after the last one closed. Laying the samples out first and building
            // tracks from the whole run — rather than joining finished tracks —
            // is what keeps quaternions sign-continuous across a seam, so a
            // sampler slerps the short way there as it does everywhere else.
            List<PoseSampling.LocalPose[]> runSamples = [];
            List<bool[]> runVisible = [];
            ImmutableArray<float>.Builder runTimes = ImmutableArray.CreateBuilder<float>();
            double offset = 0.0;

            for (int c = 0; c < clips.Length; c++)
            {
                AnimFile clip = clips[c].Animation;
                for (int sample = 0; sample < clip.PlayableSamples; sample++)
                {
                    runSamples.Add(sampledPer[c][sample]);
                    runVisible.Add(visiblePer[c][sample]);
                    runTimes.Add((float)(offset + (sample / (double)clip.Fps)));
                }

                // The next clip opens one frame interval after this one's last
                // sample, not on top of it: a shared instant would be two values
                // at one time, and a longer gap would read as a pause nobody
                // asked for.
                offset += clip.PlayableSamples / (double)clip.Fps;
            }

            ImmutableArray<float> joined = runTimes.ToImmutable();
            for (int i = 1; i < joined.Length; i++)
            {
                if (joined[i] <= joined[i - 1])
                {
                    return Refusal.Unsupported(
                        "Two frames of the queued animations fall at the same moment, so they cannot share one timeline.");
                }
            }

            List<AnimationTrack> queuedTracks = TransformTracks(setup, clip: null, [.. runSamples], forced);
            PoseSampling.VisibilityTracks(keep, bindings.NodeOfPart, [.. runVisible], setup.NodeCount, attachmentScales, queuedTracks);

            totalTracks = queuedTracks.Count;
            animations.Add(new Animation(name, joined, [.. queuedTracks]));
        }
        else
        {
            for (int c = 0; c < clips.Length; c++)
            {
                NamedClip named = clips[c];
                Result<ImmutableArray<float>> perTimes = PoseSampling.SampleTimes(named.Animation);
                if (!perTimes.TryGetValue(out ImmutableArray<float> clipTimes, out Refusal? perTimesRefusal))
                {
                    return perTimesRefusal;
                }

                List<AnimationTrack> clipTracks = TransformTracks(setup, named.Animation, sampledPer[c], forced);
                PoseSampling.VisibilityTracks(keep, bindings.NodeOfPart, visiblePer[c], setup.NodeCount, attachmentScales, clipTracks);

                totalTracks += clipTracks.Count;
                animations.Add(new Animation(named.Name, clipTimes, [.. clipTracks]));
            }
        }

        // Nothing to animate at all. This is not a failure: the scene already
        // holds the pose these animations set, so the export is exactly what was
        // asked for minus the moving part, and it goes out with a warning saying
        // so. It used to refuse, which was wrong twice over — the same file works
        // when picked alongside another, and the pose was always exportable by
        // unticking a box the refusal did not name.
        if (totalTracks == 0)
        {
            return Result.Ok(new AnimatedScene(
                new PosedScene(
                    keep,
                    PoseSampling.BuildGraph(model, setup, sampledPer[0][0], bindings.NodeOfPart, keep, attachmentScales),
                    bindings.Unrigged),
                []));
        }

        SceneGraph graph = PoseSampling.BuildGraph(model, setup, sampledPer[0][0], bindings.NodeOfPart, keep, attachmentScales);
        return Result.Ok(new AnimatedScene(new PosedScene(keep, graph, bindings.Unrigged), animations.MoveToImmutable()));
    }

    /// <summary>
    /// Which node-and-channel pairs at least one of <paramref name="clips"/> sets,
    /// as a flat <c>node * Channels.Length + channel</c> map.
    /// </summary>
    /// <remarks>
    /// A sentinel means "leave this alone", so anything else — an animated
    /// selector or a constant one — is the animation stating a value. Those are
    /// the channels that have to appear in every animation, because one of them
    /// moving a node means the others must say where they want it instead of
    /// inheriting whatever the previous Action left behind.
    /// </remarks>
    private static bool[] ChannelsAnySets(AnimFile setup, ImmutableArray<NamedClip> clips)
    {
        bool[] forced = new bool[setup.NodeCount * Channels.Length];
        foreach (NamedClip named in clips)
        {
            for (int node = 0; node < setup.NodeCount; node++)
            {
                if (!named.Animation.TryGetNode(setup.Names[node], out int clipNode))
                {
                    continue;
                }

                for (int channel = 0; channel < Channels.Length; channel++)
                {
                    if (named.Animation.Selector(Channels[channel], clipNode) < Marker)
                    {
                        forced[(node * Channels.Length) + channel] = true;
                    }
                }
            }
        }

        return forced;
    }

    private static List<AnimationTrack> TransformTracks(
        AnimFile setup, AnimFile? clip, PoseSampling.LocalPose[][] sampled, bool[]? forced)
    {
        List<AnimationTrack> tracks = [];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            // The forced map is indexed by setup node and already accounts for
            // every clip, so a queued run needs no clip of its own to consult.
            int clipNode = -1;
            if (forced is null && !clip!.TryGetNode(setup.Names[node], out clipNode))
            {
                continue;
            }

            for (int channel = 0; channel < Channels.Length; channel++)
            {
                // With one animation, only a channel the clip itself animates gets
                // a track; anything else holds the resting pose the graph bakes.
                // With several, any channel some animation sets is written by all
                // of them — see the remarks on Animate for why inheriting is not
                // an option once a scene serves more than one Action.
                bool wanted = forced is null
                    ? clip!.Selector(Channels[channel], clipNode) < Constant
                    : forced[(node * Channels.Length) + channel];

                if (!wanted)
                {
                    continue;
                }

                tracks.Add(new AnimationTrack(node, Paths[channel], TrackInterpolation.Linear, PoseSampling.TrackValues(sampled, node, Channels[channel])));
            }
        }

        return tracks;
    }
}
