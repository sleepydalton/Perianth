using System.Collections.Immutable;

namespace Perianth.Core.Pose;

/// <summary>Which local transform channel an animation track drives.</summary>
public enum TrackPath
{
    /// <summary>Local translation, three components per key.</summary>
    Translation,

    /// <summary>Local rotation, four quaternion components per key.</summary>
    Rotation,

    /// <summary>Local scale, three components per key.</summary>
    Scale,
}

/// <summary>How an animation sampler steps between its keys.</summary>
public enum TrackInterpolation
{
    /// <summary>Linear between keys, for a continuous transform.</summary>
    Linear,

    /// <summary>Held until the next key, for a visibility switch.</summary>
    Step,
}

/// <summary>
/// One node's animated channel: a value for every key on the shared timeline.
/// </summary>
/// <param name="Node">The node index this drives.</param>
/// <param name="Path">Which channel it drives.</param>
/// <param name="Interpolation">How the keys are stepped.</param>
/// <param name="Values">
/// The keys flattened: <c>Count</c> keys of <see cref="Width"/> components each,
/// in key order. Rotation keys are four-wide, the rest three.
/// </param>
public sealed record AnimationTrack(
    int Node,
    TrackPath Path,
    TrackInterpolation Interpolation,
    ImmutableArray<double> Values)
{
    /// <summary>Components per key: four for rotation, three otherwise.</summary>
    public int Width => Path == TrackPath.Rotation ? 4 : 3;

    /// <summary>The number of keys, one per shared timeline entry.</summary>
    public int Count => Values.Length / Width;
}

/// <summary>
/// One named animation over a shared timeline, its tracks addressing scene nodes.
/// </summary>
/// <param name="Name">The animation's name, which a viewer shows.</param>
/// <param name="Times">The key times in seconds, strictly ascending.</param>
/// <param name="Tracks">The per-node channels sampled on that timeline.</param>
public sealed record Animation(
    string Name,
    ImmutableArray<float> Times,
    ImmutableArray<AnimationTrack> Tracks);
