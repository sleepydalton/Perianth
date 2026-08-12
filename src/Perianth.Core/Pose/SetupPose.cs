using System.Collections.Immutable;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Core.Pose;

/// <summary>
/// The parts a setup pose keeps, and the node hierarchy that places them.
/// </summary>
/// <param name="Keep">
/// The indices, into the posed model's parts, of the meshes that survive
/// association and visibility, in draw order.
/// </param>
/// <param name="Graph">The node hierarchy, with mesh indices dense over <paramref name="Keep"/>.</param>
/// <param name="UnriggedParts">
/// The source labels of parts the hierarchy declares no node for, omitted and
/// reported rather than placed somewhere invented.
/// </param>
public sealed record PosedScene(
    ImmutableArray<int> Keep,
    SceneGraph Graph,
    ImmutableArray<string> UnriggedParts);

/// <summary>
/// Places a decoded model beneath a setup hierarchy at its resting pose.
/// </summary>
/// <remarks>
/// Each part binds to the setup node whose name equals its hierarchy binding
/// name exactly. A part the hierarchy does not name is omitted and reported; if
/// more than a tenth of the parts are unnamed the setup is not this model's and
/// the whole export refuses, unless the caller has explicitly asked to proceed
/// with a borrowed hierarchy. Visibility is the proven SCAI rule, evaluated at the
/// resting sample; the world composition is computed only to reject a hierarchy
/// that produces a degenerate placement, since the GLB stores local transforms.
/// </remarks>
public static class SetupPose
{
    /// <summary>
    /// Poses <paramref name="model"/> under <paramref name="setup"/> at
    /// <paramref name="seconds"/>, taking <paramref name="clip"/> where it drives
    /// a channel.
    /// </summary>
    /// <remarks>
    /// With no clip and time zero this is the resting pose. A clip overrides the
    /// nodes it names, and a time selects a single frame of whichever ANIM is
    /// sampled — a still, not an animation. A time past the end of the sampled
    /// ANIM refuses as unsupported: the file is intact and another time works.
    /// </remarks>
    public static Result<PosedScene> Pose(
        GeometryModel model, AnimFile setup, AnimFile? clip, double seconds,
        bool allowMissingParts = false)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(setup);

        Result<PoseSampling.Association> association =
            PoseSampling.Associate(model, setup, allowMissingParts);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        Result<PoseSampling.LocalPose[]> localResult = PoseSampling.PoseValues(setup, clip, seconds);
        if (!localResult.TryGetValue(out PoseSampling.LocalPose[]? local, out Refusal? localRefusal))
        {
            return localRefusal;
        }

        Result<bool> composed = PoseSampling.ValidateWorldComposition(setup, local);
        if (!composed.IsSuccess)
        {
            return composed.Refusal;
        }

        Result<bool[]> visibleResult = PoseSampling.Visibility(setup, clip, seconds);
        if (!visibleResult.TryGetValue(out bool[]? visible, out Refusal? visibleRefusal))
        {
            return visibleRefusal;
        }

        ImmutableArray<int>.Builder keep = ImmutableArray.CreateBuilder<int>();
        for (int part = 0; part < model.Parts.Length; part++)
        {
            if (bindings.NodeOfPart[part] >= 0 && visible[bindings.NodeOfPart[part]])
            {
                keep.Add(part);
            }
        }

        ImmutableArray<int> kept = keep.ToImmutable();

        Result<bool> hidden = PoseSampling.RefuseIfClipHidEverything(setup, clip, kept, bindings.NodeOfPart, seconds);
        if (!hidden.IsSuccess)
        {
            return hidden.Refusal;
        }

        if (kept.Length == 0)
        {
            return Refusal.Unsupported(
                "The setup hierarchy and visibility select no mesh parts.",
                DiagnosticIds.PoseSelectsNothing);
        }

        // A static pose draws every kept mesh, so each attachment stands at full
        // scale; only an animated clip switches a mesh off over time.
        AnimVec3[] attachmentScales = new AnimVec3[kept.Length];
        System.Array.Fill(attachmentScales, AnimVec3.One);

        SceneGraph graph = PoseSampling.BuildGraph(model, setup, local, bindings.NodeOfPart, kept, attachmentScales);
        return Result.Ok(new PosedScene(kept, graph, bindings.Unrigged));
    }

    /// <summary>
    /// Reports whether <paramref name="setup"/> is a setup ANIM that rigs
    /// <paramref name="model"/> — the association question, answered without
    /// posing anything.
    /// </summary>
    /// <remarks>
    /// This is what decides whether a neighbouring ANIM is this model's forgotten
    /// setup: the real association rule, including its unrigged-share limit, run
    /// against the candidate. A candidate that fails any part of it — a hierarchy
    /// that will not parse, or one that names too few of the parts — is answered
    /// "no" rather than refused, because the caller is scanning files it was never
    /// given, so an unrelated neighbour is not an error.
    /// </remarks>
    public static bool DescribesModel(GeometryModel model, SourceFile setup)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(setup);

        Result<AnimFile> parsed = AnimReader.Read(setup, hierarchy: true);
        if (!parsed.TryGetValue(out AnimFile? hierarchy, out _))
        {
            return false;
        }

        return PoseSampling.Associate(model, hierarchy).IsSuccess;
    }
}
