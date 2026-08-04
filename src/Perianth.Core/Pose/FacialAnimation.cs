using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// Composes fixed facial atlas states over a body clip's timeline, as one native
/// animation whose tracks address the scene's nodes.
/// </summary>
/// <remarks>
/// Where <see cref="ClipAnimation"/> tracks the channels the clip's own selector
/// drives, this tracks the channels whose value actually changes across the clip
/// once the facial layers are overlaid: a facial-owned channel that moves is
/// stepped, a body channel is interpolated. A fixed facial layer holds one sample
/// for the whole clip, so it contributes a still overlay on the resting pose and,
/// being constant, adds no track of its own.
/// </remarks>
public static class FacialAnimation
{
    private static readonly AnimChannel[] Channels = [AnimChannel.Translation, AnimChannel.Rotation, AnimChannel.Scale];
    private static readonly TrackPath[] Paths = [TrackPath.Translation, TrackPath.Rotation, TrackPath.Scale];

    /// <summary>Animates <paramref name="model"/> under the body <paramref name="clip"/> with facial <paramref name="layers"/> overlaid.</summary>
    public static Result<AnimatedScene> Animate(
        GeometryModel model, AnimFile setup, AnimFile clip, ImmutableArray<FacialLayer> layers)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(setup);
        System.ArgumentNullException.ThrowIfNull(clip);

        if (clip.SampleCount == 0)
        {
            return Refusal.Unsupported("The clip ANIM contains no stored samples.");
        }

        if (!(double.IsFinite(clip.Fps) && clip.Fps > 0.0))
        {
            return Refusal.Malformed("The ANIM sampling rate is invalid.");
        }

        Result<bool> validated = PoseSampling.ValidateFacialLayers(setup, layers);
        if (!validated.IsSuccess)
        {
            return validated.Refusal;
        }

        Result<PoseSampling.Association> association = PoseSampling.Associate(model, setup);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        // The timeline is the clip's sample times plus every in-range facial
        // interval boundary, so a stepped facial change lands exactly on the frame
        // it begins. The stored time is on the float32 grid the glTF sampler uses,
        // but the pose is sampled at the original binary64 time behind it.
        Result<Timeline> timelineResult = BuildTimeline(clip, layers);
        if (!timelineResult.TryGetValue(out Timeline timeline, out Refusal? timelineRefusal))
        {
            return timelineRefusal;
        }

        ImmutableArray<float> times = timeline.Times;
        int frames = times.Length;
        PoseSampling.LocalPose[][] sampled = new PoseSampling.LocalPose[frames][];
        bool[][] visible = new bool[frames][];
        for (int frame = 0; frame < frames; frame++)
        {
            double seconds = timeline.Evaluation[frame];

            Result<PoseSampling.LocalPose[]> pose = PoseSampling.LayeredPoseValues(setup, clip, seconds, layers);
            if (!pose.TryGetValue(out PoseSampling.LocalPose[]? local, out Refusal? poseRefusal))
            {
                return poseRefusal;
            }

            Result<bool[]> vis = PoseSampling.LayeredVisibility(setup, clip, seconds, layers);
            if (!vis.TryGetValue(out bool[]? visibility, out Refusal? visRefusal))
            {
                return visRefusal;
            }

            sampled[frame] = local;
            visible[frame] = visibility;
        }

        Result<bool> composed = PoseSampling.ValidateWorldComposition(setup, sampled[0]);
        if (!composed.IsSuccess)
        {
            return composed.Refusal;
        }

        ImmutableArray<int>.Builder keepBuilder = ImmutableArray.CreateBuilder<int>();
        for (int part = 0; part < model.Parts.Length; part++)
        {
            int node = bindings.NodeOfPart[part];
            if (node < 0)
            {
                continue;
            }

            for (int frame = 0; frame < frames; frame++)
            {
                if (visible[frame][node])
                {
                    keepBuilder.Add(part);
                    break;
                }
            }
        }

        ImmutableArray<int> keep = keepBuilder.ToImmutable();

        Result<bool> hidden = PoseSampling.RefuseIfClipHidEverything(setup, clip, keep, bindings.NodeOfPart, 0.0, layers);
        if (!hidden.IsSuccess)
        {
            return hidden.Refusal;
        }

        if (keep.Length == 0)
        {
            return Refusal.Unsupported("The setup hierarchy and visibility select no mesh parts.");
        }

        HashSet<(int Node, int Channel)> facialOwned = FacialOwned(setup, layers);
        List<AnimationTrack> tracks = ChangingTracks(setup, sampled, facialOwned);
        AnimVec3[] attachmentScales = PoseSampling.AttachmentScales(keep, bindings.NodeOfPart, visible, setup.NodeCount, tracks);

        if (tracks.Count == 0)
        {
            return Refusal.Unsupported("The body clip and facial layers contain no changing transform or visibility channels.");
        }

        SceneGraph graph = PoseSampling.BuildGraph(model, setup, sampled[0], bindings.NodeOfPart, keep, attachmentScales);
        Animation animation = new("clip", times, [.. tracks]);
        return Result.Ok(new AnimatedScene(new PosedScene(keep, graph, bindings.Unrigged), animation));
    }

    /// <summary>The glTF sampler times and the binary64 time each one is sampled at.</summary>
    private readonly record struct Timeline(ImmutableArray<float> Times, double[] Evaluation);

    /// <summary>
    /// Builds the evaluation timeline: the clip's sample times together with every
    /// in-range facial interval boundary, collapsed to the float32 grid the sampler
    /// stores and paired with the binary64 time to sample the pose at.
    /// </summary>
    private static Result<Timeline> BuildTimeline(AnimFile clip, ImmutableArray<FacialLayer> layers)
    {
        double fps = clip.Fps;
        double duration = (clip.SampleCount - 1) / fps;
        double durationTime = (float)duration;

        // An explicit blink is placed against this clip, so it must fit within it.
        foreach (FacialLayer layer in layers)
        {
            if (!layer.RequireCompleteIntervals)
            {
                continue;
            }

            foreach (FacialInterval interval in layer.Intervals)
            {
                if (interval.End > durationTime)
                {
                    // Both numbers, because the caller chose one of them and can
                    // only correct it against the other. Naming the layer without
                    // an article also avoids "A explicit blink", which is what
                    // interpolating the name into a sentence produced.
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {layer.Name} interval ending at {interval.End.ToString("0.###", CultureInfo.InvariantCulture)}s "
                        + $"extends beyond the body clip, which is {durationTime.ToString("0.###", CultureInfo.InvariantCulture)}s long."));
                }
            }
        }

        List<double> raw = [];
        for (int sample = 0; sample < clip.SampleCount; sample++)
        {
            raw.Add(sample / fps);
        }

        foreach (FacialLayer layer in layers)
        {
            foreach (double boundary in layer.Boundaries())
            {
                if (boundary >= 0.0 && boundary <= duration)
                {
                    raw.Add(boundary);
                }
            }
        }

        // Collapse to the float32 grid; the last original written for a grid point
        // wins, and it is that original the pose is sampled at.
        Dictionary<float, double> byTime = [];
        foreach (double value in raw)
        {
            byTime[(float)value] = value;
        }

        float[] keys = [.. byTime.Keys];
        System.Array.Sort(keys);
        if (keys.Length == 0 || keys[0] != 0.0f)
        {
            return Refusal.Unsupported("The facial animation timeline cannot be represented as distinct glTF float times.");
        }

        for (int i = 1; i < keys.Length; i++)
        {
            if (keys[i] <= keys[i - 1])
            {
                return Refusal.Unsupported("The facial animation timeline cannot be represented as distinct glTF float times.");
            }
        }

        double[] evaluation = new double[keys.Length];
        for (int i = 0; i < keys.Length; i++)
        {
            evaluation[i] = byTime[keys[i]];
        }

        return Result.Ok(new Timeline([.. keys], evaluation));
    }

    /// <summary>Each setup node and channel a facial layer animates, which is stepped rather than interpolated.</summary>
    private static HashSet<(int, int)> FacialOwned(AnimFile setup, ImmutableArray<FacialLayer> layers)
    {
        HashSet<(int, int)> owned = [];
        foreach (FacialLayer layer in layers)
        {
            AnimFile atlas = layer.Atlas;
            for (int atlasIndex = 0; atlasIndex < atlas.NodeCount; atlasIndex++)
            {
                if (!setup.TryGetNode(atlas.Names[atlasIndex], out int setupIndex))
                {
                    continue;
                }

                for (int channel = 0; channel < Channels.Length; channel++)
                {
                    if (atlas.Selector(Channels[channel], atlasIndex) < 0x8000)
                    {
                        owned.Add((setupIndex, channel));
                    }
                }
            }
        }

        return owned;
    }

    /// <summary>
    /// One track per node and channel whose value changes across the clip, stepped
    /// for a facial-owned channel and interpolated for a body one.
    /// </summary>
    private static List<AnimationTrack> ChangingTracks(
        AnimFile setup, PoseSampling.LocalPose[][] sampled, HashSet<(int Node, int Channel)> facialOwned)
    {
        List<AnimationTrack> tracks = [];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            for (int channel = 0; channel < Channels.Length; channel++)
            {
                ImmutableArray<double> values = PoseSampling.TrackValues(sampled, node, Channels[channel]);
                if (Constant(values, channel == 1 ? 4 : 3))
                {
                    continue;
                }

                TrackInterpolation interpolation = facialOwned.Contains((node, channel))
                    ? TrackInterpolation.Step
                    : TrackInterpolation.Linear;
                tracks.Add(new AnimationTrack(node, Paths[channel], interpolation, values));
            }
        }

        return tracks;
    }

    /// <summary>Whether every keyframe row equals the first, so the channel does not move.</summary>
    private static bool Constant(ImmutableArray<double> values, int width)
    {
        for (int offset = width; offset < values.Length; offset += width)
        {
            for (int component = 0; component < width; component++)
            {
                if (values[offset + component] != values[component])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
