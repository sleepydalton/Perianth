using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// A posed scene together with the clip animation that drives it.
/// </summary>
/// <param name="Scene">The resting pose — the clip's first sample — and its hierarchy.</param>
/// <param name="Animation">The one clip animation, its tracks addressing the scene's nodes.</param>
public sealed record AnimatedScene(PosedScene Scene, Animation Animation);

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
    private static readonly AnimChannel[] Channels = [AnimChannel.Translation, AnimChannel.Rotation, AnimChannel.Scale];
    private static readonly TrackPath[] Paths = [TrackPath.Translation, TrackPath.Rotation, TrackPath.Scale];

    /// <summary>Animates <paramref name="model"/> under <paramref name="setup"/> with <paramref name="clip"/>.</summary>
    public static Result<AnimatedScene> Animate(GeometryModel model, AnimFile setup, AnimFile clip)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(clip);

        if (clip.SampleCount == 0)
        {
            return Refusal.Unsupported("The clip ANIM contains no stored samples.");
        }

        if (!(double.IsFinite(clip.Fps) && clip.Fps > 0.0))
        {
            return Refusal.Malformed("The ANIM sampling rate is invalid.");
        }

        Result<PoseSampling.Association> association = PoseSampling.Associate(model, setup);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        // Every sample's composed pose and visibility, indexed [sample][node].
        PoseSampling.LocalPose[][] sampled = new PoseSampling.LocalPose[clip.SampleCount][];
        bool[][] visible = new bool[clip.SampleCount][];
        for (int sample = 0; sample < clip.SampleCount; sample++)
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

        Result<bool> composed = PoseSampling.ValidateWorldComposition(setup, sampled[0]);
        if (!composed.IsSuccess)
        {
            return composed.Refusal;
        }

        // A part is drawn if it is rigged and visible in at least one sample.
        ImmutableArray<int>.Builder keepBuilder = ImmutableArray.CreateBuilder<int>();
        for (int part = 0; part < model.Parts.Length; part++)
        {
            int node = bindings.NodeOfPart[part];
            if (node < 0)
            {
                continue;
            }

            bool everVisible = false;
            for (int sample = 0; sample < clip.SampleCount; sample++)
            {
                if (visible[sample][node])
                {
                    everVisible = true;
                    break;
                }
            }

            if (everVisible)
            {
                keepBuilder.Add(part);
            }
        }

        ImmutableArray<int> keep = keepBuilder.ToImmutable();

        Result<bool> hidden = PoseSampling.RefuseIfClipHidEverything(setup, clip, keep, bindings.NodeOfPart, withoutClipSeconds: 0.0);
        if (!hidden.IsSuccess)
        {
            return hidden.Refusal;
        }

        if (keep.Length == 0)
        {
            return Refusal.Unsupported("The setup hierarchy and visibility select no mesh parts.");
        }

        Result<ImmutableArray<float>> timesResult = PoseSampling.SampleTimes(clip);
        if (!timesResult.TryGetValue(out ImmutableArray<float> times, out Refusal? timesRefusal))
        {
            return timesRefusal;
        }

        List<AnimationTrack> tracks = TransformTracks(setup, clip, sampled);
        AnimVec3[] attachmentScales = PoseSampling.AttachmentScales(keep, bindings.NodeOfPart, visible, setup.NodeCount, tracks);

        if (tracks.Count == 0)
        {
            return Refusal.Unsupported("The clip ANIM contains no animated transform or visibility channels.");
        }

        SceneGraph graph = PoseSampling.BuildGraph(model, setup, sampled[0], bindings.NodeOfPart, keep, attachmentScales);
        Animation animation = new("clip", times, [.. tracks]);
        return Result.Ok(new AnimatedScene(new PosedScene(keep, graph, bindings.Unrigged), animation));
    }

    private static List<AnimationTrack> TransformTracks(AnimFile setup, AnimFile clip, PoseSampling.LocalPose[][] sampled)
    {
        List<AnimationTrack> tracks = [];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            if (!clip.TryGetNode(setup.Names[node], out int clipNode))
            {
                continue;
            }

            for (int channel = 0; channel < Channels.Length; channel++)
            {
                // Only a channel the clip itself animates gets a track; anything
                // else holds the resting pose.
                if (clip.Selector(Channels[channel], clipNode) >= 0x8000)
                {
                    continue;
                }

                tracks.Add(new AnimationTrack(node, Paths[channel], TrackInterpolation.Linear, PoseSampling.TrackValues(sampled, node, Channels[channel])));
            }
        }

        return tracks;
    }
}
