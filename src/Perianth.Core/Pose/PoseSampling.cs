using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// The sampling shared by the static pose and the animated clip: association,
/// local pose and visibility at a time, and the node hierarchy that carries them.
/// </summary>
/// <remarks>
/// A pose can draw from two ANIMs. The setup states the resting hierarchy; a
/// clip overrides the nodes it names. The two overlay differently for transforms
/// and for visibility, and both rules are reproduced here exactly: a transform
/// takes the clip only where the clip's selector is not a sentinel, while
/// visibility takes the clip's state whenever the clip names the node at all.
/// </remarks>
internal static class PoseSampling
{
    private const double UnriggedShareLimit = 0.10;

    internal readonly record struct LocalPose(AnimVec3 Translation, AnimQuat Rotation, AnimVec3 Scale);

    internal readonly record struct Association(int[] NodeOfPart, ImmutableArray<string> Unrigged);

    /// <summary>Binds each part to the setup node its binding name equals exactly.</summary>
    internal static Result<Association> Associate(GeometryModel model, AnimFile setup)
    {
        int[] nodeOfPart = new int[model.Parts.Length];
        List<string> unrigged = [];
        for (int part = 0; part < model.Parts.Length; part++)
        {
            nodeOfPart[part] = setup.TryGetNode(model.Parts[part].HierarchyBindingName, out int node) ? node : -1;
            if (nodeOfPart[part] < 0)
            {
                unrigged.Add(model.Parts[part].SourceLabel);
            }
        }

        if (unrigged.Count > model.Parts.Length * UnriggedShareLimit)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{unrigged.Count} of {model.Parts.Length} model parts have no node in the setup hierarchy, so it does not describe this model."));
        }

        return Result.Ok(new Association(nodeOfPart, [.. unrigged]));
    }

    /// <summary>
    /// Checks a set of facial layers against the setup before any is applied: each
    /// atlas holds the samples it asks for, and no two layers own the same node and
    /// channel.
    /// </summary>
    /// <remarks>
    /// A layer owns a node/channel only where it animates it (selector &lt; 0x8000)
    /// and the setup names the node. Targets a character's hierarchy does not name
    /// are ignored — shipped facial libraries carry reusable ones. Two layers
    /// claiming the same node and channel refuse, as does a layer that ends up
    /// owning nothing at all, since it would silently do nothing.
    /// </remarks>
    internal static Result<bool> ValidateFacialLayers(AnimFile setup, ImmutableArray<FacialLayer> layers)
    {
        Dictionary<(int Node, AnimChannel Channel), string> owners = [];
        foreach (FacialLayer layer in layers)
        {
            AnimFile atlas = layer.Atlas;
            if (atlas.SampleCount == 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"The {layer.Name} facial ANIM contains no stored samples."));
            }

            foreach (int sample in Samples(layer))
            {
                if (sample < 0 || sample >= atlas.SampleCount)
                {
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The {layer.Name} facial layer requests sample {sample}, which its atlas of {atlas.SampleCount} samples does not hold."));
                }
            }

            int active = 0;
            for (int atlasIndex = 0; atlasIndex < atlas.NodeCount; atlasIndex++)
            {
                if (!setup.TryGetNode(atlas.Names[atlasIndex], out int setupIndex))
                {
                    continue;
                }

                foreach (AnimChannel channel in Channels)
                {
                    if (!IsAnimated(atlas.Selector(channel, atlasIndex)))
                    {
                        continue;
                    }

                    (int, AnimChannel) key = (setupIndex, channel);
                    if (owners.TryGetValue(key, out string? previous))
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"The {previous} and {layer.Name} facial layers both drive node '{atlas.Names[atlasIndex]}' {Tag(channel)}."));
                    }

                    owners[key] = layer.Name;
                    active++;
                }
            }

            if (active == 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The {layer.Name} facial ANIM has no channel that participates in this setup."));
            }
        }

        return Result.Ok(true);
    }

    private static readonly AnimChannel[] Channels = [AnimChannel.Translation, AnimChannel.Rotation, AnimChannel.Scale];

    private static IEnumerable<int> Samples(FacialLayer layer)
    {
        if (layer.DefaultSample is int fallback)
        {
            yield return fallback;
        }

        foreach (FacialInterval interval in layer.Intervals)
        {
            yield return interval.Sample;
        }
    }

    private static string Tag(AnimChannel channel) => channel switch
    {
        AnimChannel.Translation => "translation",
        AnimChannel.Rotation => "rotation",
        _ => "scale",
    };

    /// <summary>
    /// Each setup node's local transform at <paramref name="seconds"/>, taking the
    /// clip where it drives the channel and the setup otherwise.
    /// </summary>
    internal static Result<LocalPose[]> PoseValues(AnimFile setup, AnimFile? clip, double seconds) =>
        LayeredPoseValues(setup, clip, seconds, []);

    /// <summary>
    /// The setup/clip pose with the facial atlases overlaid on the channels they
    /// animate. With no layers this is exactly the body pose.
    /// </summary>
    internal static Result<LocalPose[]> LayeredPoseValues(
        AnimFile setup, AnimFile? clip, double seconds, ImmutableArray<FacialLayer> layers)
    {
        AnimFile sampleSource = clip ?? setup;
        Result<double> sourcePosition = sampleSource.SamplePosition(seconds);
        if (!sourcePosition.TryGetValue(out double samplePosition, out Refusal? positionRefusal))
        {
            return positionRefusal;
        }

        LocalPose[] result = new LocalPose[setup.NodeCount];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            int clipNode = clip is not null && clip.TryGetNode(setup.Names[node], out int index) ? index : -1;

            Result<AnimVec3> translation = Vec3(AnimChannel.Translation, setup, clip, node, clipNode, seconds, samplePosition, sampleSource);
            Result<AnimQuat> rotation = Quat(setup, clip, node, clipNode, seconds, samplePosition, sampleSource);
            Result<AnimVec3> scale = Vec3(AnimChannel.Scale, setup, clip, node, clipNode, seconds, samplePosition, sampleSource);

            if (!translation.TryGetValue(out AnimVec3 t, out Refusal? tr))
            {
                return tr;
            }

            if (!rotation.TryGetValue(out AnimQuat r, out Refusal? rr))
            {
                return rr;
            }

            if (!scale.TryGetValue(out AnimVec3 s, out Refusal? sr))
            {
                return sr;
            }

            if (!IsFinite(t) || !IsFinite(r) || !IsFinite(s))
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture, $"ANIM node '{setup.Names[node]}' produces a non-finite transform value."));
            }

            result[node] = new LocalPose(t, r, s);
        }

        foreach (FacialLayer layer in layers)
        {
            Result<bool> overlaid = OverlayPose(setup, layer, seconds, result);
            if (!overlaid.IsSuccess)
            {
                return overlaid.Refusal;
            }
        }

        return Result.Ok(result);
    }

    /// <summary>
    /// Overwrites, in <paramref name="result"/>, each channel a facial layer
    /// animates on a node the setup names, leaving every other channel untouched.
    /// </summary>
    private static Result<bool> OverlayPose(AnimFile setup, FacialLayer layer, double seconds, LocalPose[] result)
    {
        int? sample = layer.SampleAt(seconds);
        if (sample is null)
        {
            return Result.Ok(true);
        }

        AnimFile atlas = layer.Atlas;
        double position = sample.Value;
        for (int atlasIndex = 0; atlasIndex < atlas.NodeCount; atlasIndex++)
        {
            if (!setup.TryGetNode(atlas.Names[atlasIndex], out int setupIndex))
            {
                continue;
            }

            // The atlas's own translation is what moves a suppressed pupil off
            // its authored placement, so mesh-neutral leaves that channel alone.
            if (!layer.SuppressTranslation && IsAnimated(atlas.Selector(AnimChannel.Translation, atlasIndex)))
            {
                Result<AnimVec3> t = atlas.TranslationAt(atlasIndex, position);
                if (!Finite(layer, atlas, atlasIndex, "TRAI", t, out AnimVec3 translation, out Refusal? tr))
                {
                    return tr;
                }

                result[setupIndex] = result[setupIndex] with { Translation = translation };
            }

            if (IsAnimated(atlas.Selector(AnimChannel.Rotation, atlasIndex)))
            {
                Result<AnimQuat> r = atlas.RotationAt(atlasIndex, position);
                if (!r.TryGetValue(out AnimQuat rotation, out Refusal? rr))
                {
                    return rr;
                }

                if (!IsFinite(rotation))
                {
                    return NonFinite(layer, atlas, atlasIndex, "ROTI");
                }

                result[setupIndex] = result[setupIndex] with { Rotation = rotation };
            }

            if (IsAnimated(atlas.Selector(AnimChannel.Scale, atlasIndex)))
            {
                Result<AnimVec3> s = atlas.ScaleAt(atlasIndex, position);
                if (!Finite(layer, atlas, atlasIndex, "SCAI", s, out AnimVec3 scale, out Refusal? sr))
                {
                    return sr;
                }

                result[setupIndex] = result[setupIndex] with { Scale = scale };
            }
        }

        return Result.Ok(true);
    }

    private static bool Finite(
        FacialLayer layer, AnimFile atlas, int atlasIndex, string tag,
        Result<AnimVec3> decoded, out AnimVec3 value, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Refusal? refusal)
    {
        if (!decoded.TryGetValue(out value, out refusal))
        {
            return false;
        }

        if (!IsFinite(value))
        {
            refusal = NonFinite(layer, atlas, atlasIndex, tag);
            return false;
        }

        return true;
    }

    private static Refusal NonFinite(FacialLayer layer, AnimFile atlas, int atlasIndex, string tag) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture,
            $"{layer.Name} ANIM node '{atlas.Names[atlasIndex]}' produces a non-finite {tag} value."));

    /// <summary>A selector that animates a channel, as opposed to a static value or a sentinel.</summary>
    private static bool IsAnimated(ushort selector) => selector < 0x8000;

    /// <summary>
    /// Each setup node's visibility at <paramref name="seconds"/>, combining the
    /// clip's channel-presence rule with the ancestor chain.
    /// </summary>
    internal static Result<bool[]> Visibility(AnimFile setup, AnimFile? clip, double seconds) =>
        LayeredVisibility(setup, clip, seconds, []);

    /// <summary>
    /// Node visibility with the facial atlases' own SCAI overriding the setup/clip
    /// where they animate it. With no layers this is exactly the body visibility.
    /// </summary>
    internal static Result<bool[]> LayeredVisibility(
        AnimFile setup, AnimFile? clip, double seconds, ImmutableArray<FacialLayer> layers)
    {
        // For each node, the source it takes its SCAI from, and where in that
        // source to sample. The clip overrides the setup, then a facial layer
        // overrides both, but only where each actually animates the channel.
        AnimFile[] source = new AnimFile[setup.NodeCount];
        int[] sourceNode = new int[setup.NodeCount];
        ushort[] selectors = new ushort[setup.NodeCount];
        double[] positions = new double[setup.NodeCount];

        for (int node = 0; node < setup.NodeCount; node++)
        {
            source[node] = setup;
            sourceNode[node] = node;
            selectors[node] = setup.Selector(AnimChannel.Scale, node);

            // A clip that names a visibility node states that node's state, even
            // at 0xFFFF: channel presence, not a request to inherit the setup.
            if (clip is not null && clip.TryGetNode(setup.Names[node], out int clipNode))
            {
                source[node] = clip;
                sourceNode[node] = clipNode;
                selectors[node] = clip.Selector(AnimChannel.Scale, clipNode);
            }
        }

        foreach (FacialLayer layer in layers)
        {
            int? sample = layer.SampleAt(seconds);
            if (sample is null)
            {
                continue;
            }

            AnimFile atlas = layer.Atlas;
            for (int atlasIndex = 0; atlasIndex < atlas.NodeCount; atlasIndex++)
            {
                ushort selector = atlas.Selector(AnimChannel.Scale, atlasIndex);
                if (!IsAnimated(selector))
                {
                    continue;
                }

                if (setup.TryGetNode(atlas.Names[atlasIndex], out int setupIndex))
                {
                    source[setupIndex] = atlas;
                    sourceNode[setupIndex] = atlasIndex;
                    selectors[setupIndex] = selector;
                    positions[setupIndex] = sample.Value;
                }
            }
        }

        bool[] local = new bool[setup.NodeCount];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            ushort selector = selectors[node];
            bool locallyVisible = selector != 0xFFFE;
            if (IsAnimated(selector))
            {
                double at = positions[node];
                if (source[node] == setup || source[node] == clip)
                {
                    Result<double> position = source[node].SamplePosition(seconds);
                    if (!position.TryGetValue(out at, out Refusal? positionRefusal))
                    {
                        return positionRefusal;
                    }
                }

                Result<AnimVec3> scale = source[node].ScaleAt(sourceNode[node], at);
                if (!scale.TryGetValue(out AnimVec3 value, out Refusal? scaleRefusal))
                {
                    return scaleRefusal;
                }

                locallyVisible = value.Z != 0.0;
            }

            local[node] = locallyVisible;
        }

        bool?[] resolved = new bool?[setup.NodeCount];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            Evaluate(setup, local, resolved, node);
        }

        bool[] visible = new bool[setup.NodeCount];
        for (int node = 0; node < setup.NodeCount; node++)
        {
            visible[node] = resolved[node]!.Value;
        }

        return Result.Ok(visible);
    }

    /// <summary>
    /// Composes each node to world space only to reject a degenerate hierarchy;
    /// the GLB stores local transforms, so nothing here is kept.
    /// </summary>
    internal static Result<bool> ValidateWorldComposition(AnimFile setup, LocalPose[] local)
    {
        (AnimVec3 T, AnimQuat R, AnimVec3 S)?[] world = new (AnimVec3, AnimQuat, AnimVec3)?[setup.NodeCount];
        for (int index = 0; index < setup.NodeCount; index++)
        {
            Result<bool> composed = Compose(setup, local, world, index);
            if (!composed.IsSuccess)
            {
                return composed.Refusal;
            }
        }

        return Result.Ok(true);
    }

    /// <summary>Builds the node hierarchy and the attachments beneath it.</summary>
    internal static SceneGraph BuildGraph(
        GeometryModel model,
        AnimFile setup,
        LocalPose[] resting,
        int[] nodeOfPart,
        ImmutableArray<int> keep,
        AnimVec3[] attachmentScales)
    {
        int setupCount = setup.NodeCount;
        List<int>[] attachments = new List<int>[setupCount];
        for (int i = 0; i < setupCount; i++)
        {
            attachments[i] = [];
        }

        List<SceneNode> attachmentNodes = [];
        for (int meshIndex = 0; meshIndex < keep.Length; meshIndex++)
        {
            int part = keep[meshIndex];
            int parent = nodeOfPart[part];
            int nodeIndex = setupCount + attachmentNodes.Count;
            attachments[parent].Add(nodeIndex);
            attachmentNodes.Add(new SceneNode(
                model.Parts[part].SourceLabel,
                [],
                AnimVec3.Zero,
                AnimQuat.Identity,
                attachmentScales[meshIndex],
                Mesh: meshIndex));
        }

        List<int>[] setupChildren = new List<int>[setupCount];
        for (int i = 0; i < setupCount; i++)
        {
            setupChildren[i] = [];
        }

        ImmutableArray<int>.Builder roots = ImmutableArray.CreateBuilder<int>();
        for (int index = 0; index < setupCount; index++)
        {
            int parent = setup.Parents[index];
            if (parent < 0)
            {
                roots.Add(index);
            }
            else
            {
                setupChildren[parent].Add(index);
            }
        }

        ImmutableArray<SceneNode>.Builder nodes = ImmutableArray.CreateBuilder<SceneNode>(setupCount + attachmentNodes.Count);
        for (int index = 0; index < setupCount; index++)
        {
            nodes.Add(new SceneNode(
                setup.Names[index],
                [.. setupChildren[index], .. attachments[index]],
                resting[index].Translation,
                resting[index].Rotation,
                resting[index].Scale,
                Mesh: null));
        }

        nodes.AddRange(attachmentNodes);
        return new SceneGraph(nodes.ToImmutable(), roots.ToImmutable());
    }

    /// <summary>
    /// Names the one empty-scene cause a clip explains: it hid every mesh a setup
    /// would otherwise have shown. Any other empty scene keeps the generic refusal.
    /// </summary>
    internal static Result<bool> RefuseIfClipHidEverything(
        AnimFile setup, AnimFile? clip, ImmutableArray<int> keep, int[] nodeOfPart, double withoutClipSeconds) =>
        RefuseIfClipHidEverything(setup, clip, keep, nodeOfPart, withoutClipSeconds, []);

    /// <summary>
    /// The same refusal for the facial paths: the without-clip visibility keeps the
    /// facial layers, so a scene the clip alone empties is still named as such.
    /// </summary>
    internal static Result<bool> RefuseIfClipHidEverything(
        AnimFile setup, AnimFile? clip, ImmutableArray<int> keep, int[] nodeOfPart, double withoutClipSeconds,
        ImmutableArray<FacialLayer> layers)
    {
        if (keep.Length > 0 || clip is null)
        {
            return Result.Ok(true);
        }

        Result<bool[]> setupOnly = LayeredVisibility(setup, clip: null, withoutClipSeconds, layers);
        if (!setupOnly.TryGetValue(out bool[]? withoutClip, out Refusal? refusal))
        {
            return refusal;
        }

        foreach (int node in nodeOfPart)
        {
            if (node >= 0 && withoutClip[node])
            {
                return Refusal.Unsupported("The clip visibility hides every exportable mesh part.");
            }
        }

        return Result.Ok(true);
    }

    /// <summary>
    /// One channel's per-sample values across an animation, flattened, with
    /// rotations made sign-continuous so a sampler slerps the short way.
    /// </summary>
    internal static ImmutableArray<double> TrackValues(LocalPose[][] sampled, int node, AnimChannel channel)
    {
        int sampleCount = sampled.Length;
        if (channel == AnimChannel.Rotation)
        {
            ImmutableArray<double>.Builder values = ImmutableArray.CreateBuilder<double>(sampleCount * 4);
            AnimQuat previous = default;
            for (int sample = 0; sample < sampleCount; sample++)
            {
                AnimQuat q = sampled[sample][node].Rotation;
                if (sample > 0 && (previous.X * q.X) + (previous.Y * q.Y) + (previous.Z * q.Z) + (previous.W * q.W) < 0.0)
                {
                    q = new AnimQuat(-q.X, -q.Y, -q.Z, -q.W);
                }

                previous = q;
                values.Add(q.X);
                values.Add(q.Y);
                values.Add(q.Z);
                values.Add(q.W);
            }

            return values.MoveToImmutable();
        }

        ImmutableArray<double>.Builder vec = ImmutableArray.CreateBuilder<double>(sampleCount * 3);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            AnimVec3 v = channel == AnimChannel.Translation ? sampled[sample][node].Translation : sampled[sample][node].Scale;
            vec.Add(v.X);
            vec.Add(v.Y);
            vec.Add(v.Z);
        }

        return vec.MoveToImmutable();
    }

    /// <summary>
    /// The initial visibility scale of each kept attachment, appending to
    /// <paramref name="tracks"/> a STEP scale for any mesh whose visibility changes.
    /// </summary>
    internal static AnimVec3[] AttachmentScales(
        ImmutableArray<int> keep, int[] nodeOfPart, bool[][] visible, int setupCount, List<AnimationTrack> tracks)
    {
        AnimVec3[] initial = new AnimVec3[keep.Length];
        for (int attachment = 0; attachment < keep.Length; attachment++)
        {
            int node = nodeOfPart[keep[attachment]];
            AnimVec3 first = visible[0][node] ? AnimVec3.One : AnimVec3.Zero;
            initial[attachment] = first;

            bool changes = false;
            for (int sample = 1; sample < visible.Length; sample++)
            {
                if (visible[sample][node] != visible[0][node])
                {
                    changes = true;
                    break;
                }
            }

            if (changes)
            {
                ImmutableArray<double>.Builder values = ImmutableArray.CreateBuilder<double>(visible.Length * 3);
                for (int sample = 0; sample < visible.Length; sample++)
                {
                    double s = visible[sample][node] ? 1.0 : 0.0;
                    values.Add(s);
                    values.Add(s);
                    values.Add(s);
                }

                tracks.Add(new AnimationTrack(setupCount + attachment, TrackPath.Scale, TrackInterpolation.Step, values.MoveToImmutable()));
            }
        }

        return initial;
    }

    /// <summary>The clip's per-sample times as float32, refusing if two collide.</summary>
    internal static Result<ImmutableArray<float>> SampleTimes(AnimFile clip)
    {
        ImmutableArray<float>.Builder times = ImmutableArray.CreateBuilder<float>(clip.SampleCount);
        for (int sample = 0; sample < clip.SampleCount; sample++)
        {
            // Double throughout: dividing by the float fps loses precision and can
            // push the last sample a hair past the end when multiplied back.
            times.Add((float)(sample / (double)clip.Fps));
        }

        for (int i = 1; i < times.Count; i++)
        {
            if (times[i] <= times[i - 1])
            {
                return Refusal.Unsupported("The clip timeline cannot be represented as distinct glTF float times.");
            }
        }

        return Result.Ok(times.MoveToImmutable());
    }

    private static Result<AnimVec3> Vec3(
        AnimChannel channel, AnimFile setup, AnimFile? clip, int setupNode, int clipNode,
        double seconds, double samplePosition, AnimFile sampleSource)
    {
        (AnimFile source, int sourceNode, bool animated) = Choose(channel, setup, clip, setupNode, clipNode);
        Result<double> position = Position(source, seconds, samplePosition, sampleSource, animated);
        if (!position.TryGetValue(out double at, out Refusal? refusal))
        {
            return refusal;
        }

        return channel == AnimChannel.Scale ? source.ScaleAt(sourceNode, at) : source.TranslationAt(sourceNode, at);
    }

    private static Result<AnimQuat> Quat(
        AnimFile setup, AnimFile? clip, int setupNode, int clipNode,
        double seconds, double samplePosition, AnimFile sampleSource)
    {
        (AnimFile source, int sourceNode, bool animated) = Choose(AnimChannel.Rotation, setup, clip, setupNode, clipNode);
        Result<double> position = Position(source, seconds, samplePosition, sampleSource, animated);
        if (!position.TryGetValue(out double at, out Refusal? refusal))
        {
            return refusal;
        }

        return source.RotationAt(sourceNode, at);
    }

    /// <summary>Picks the clip or setup for a transform channel and reports whether it animates.</summary>
    private static (AnimFile Source, int Node, bool Animated) Choose(
        AnimChannel channel, AnimFile setup, AnimFile? clip, int setupNode, int clipNode)
    {
        AnimFile source = setup;
        int sourceNode = setupNode;
        ushort selector = setup.Selector(channel, setupNode);

        if (clip is not null && clipNode >= 0)
        {
            ushort candidate = clip.Selector(channel, clipNode);
            if (candidate is not (0xFFFE or 0xFFFF))
            {
                source = clip;
                sourceNode = clipNode;
                selector = candidate;
            }
        }

        return (source, sourceNode, selector < 0x8000);
    }

    /// <summary>
    /// The position to sample a source at: only an animated channel needs it, and
    /// the sample source keeps the position already computed for it.
    /// </summary>
    private static Result<double> Position(
        AnimFile source, double seconds, double samplePosition, AnimFile sampleSource, bool animated)
    {
        if (!animated)
        {
            return Result.Ok(0.0);
        }

        return ReferenceEquals(source, sampleSource) ? Result.Ok(samplePosition) : source.SamplePosition(seconds);
    }

    private static Result<bool> Compose(
        AnimFile setup,
        LocalPose[] local,
        (AnimVec3 T, AnimQuat R, AnimVec3 S)?[] world,
        int index)
    {
        if (world[index] is not null)
        {
            return Result.Ok(true);
        }

        LocalPose self = local[index];
        int parent = setup.Parents[index];
        (AnimVec3 T, AnimQuat R, AnimVec3 S) value;

        if (parent < 0)
        {
            value = (self.Translation, self.Rotation, self.Scale);
        }
        else
        {
            Result<bool> parentComposed = Compose(setup, local, world, parent);
            if (!parentComposed.IsSuccess)
            {
                return parentComposed.Refusal;
            }

            (AnimVec3 pt, AnimQuat pr, AnimVec3 ps) = world[parent]!.Value;
            AnimVec3 scaled = new(ps.X * self.Translation.X, ps.Y * self.Translation.Y, ps.Z * self.Translation.Z);
            AnimVec3 moved = Rotate(pr, scaled);
            AnimQuat rotation = QNormalize(QMultiply(pr, self.Rotation));
            if (rotation is { X: 0, Y: 0, Z: 0, W: 0 })
            {
                return Refusal.Malformed("ANIM parent-chain composition produces a zero quaternion.");
            }

            value = (
                new AnimVec3(pt.X + moved.X, pt.Y + moved.Y, pt.Z + moved.Z),
                rotation,
                new AnimVec3(ps.X * self.Scale.X, ps.Y * self.Scale.Y, ps.Z * self.Scale.Z));
        }

        if (!IsFinite(value.T) || !IsFinite(value.R) || !IsFinite(value.S))
        {
            return Refusal.Malformed("ANIM parent-chain composition produces a non-finite value.");
        }

        world[index] = value;
        return Result.Ok(true);
    }

    private static bool Evaluate(AnimFile setup, bool[] local, bool?[] resolved, int index)
    {
        if (resolved[index] is { } cached)
        {
            return cached;
        }

        int parent = setup.Parents[index];
        bool value = local[index] && (parent < 0 || Evaluate(setup, local, resolved, parent));
        resolved[index] = value;
        return value;
    }

    private static bool IsFinite(AnimVec3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);

    private static bool IsFinite(AnimQuat q) =>
        double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);

    private static AnimQuat QMultiply(AnimQuat a, AnimQuat b) => new(
        (a.W * b.X) + (a.X * b.W) + (a.Y * b.Z) - (a.Z * b.Y),
        (a.W * b.Y) - (a.X * b.Z) + (a.Y * b.W) + (a.Z * b.X),
        (a.W * b.Z) + (a.X * b.Y) - (a.Y * b.X) + (a.Z * b.W),
        (a.W * b.W) - (a.X * b.X) - (a.Y * b.Y) - (a.Z * b.Z));

    private static AnimQuat QNormalize(AnimQuat q)
    {
        double length = Math.Sqrt((q.X * q.X) + (q.Y * q.Y) + (q.Z * q.Z) + (q.W * q.W));
        return length == 0.0 ? new AnimQuat(0, 0, 0, 0) : new AnimQuat(q.X / length, q.Y / length, q.Z / length, q.W / length);
    }

    private static AnimVec3 Rotate(AnimQuat q, AnimVec3 v)
    {
        AnimQuat result = QMultiply(QMultiply(q, new AnimQuat(v.X, v.Y, v.Z, 0.0)), new AnimQuat(-q.X, -q.Y, -q.Z, q.W));
        return new AnimVec3(result.X, result.Y, result.Z);
    }
}
