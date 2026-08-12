using System.Collections.Immutable;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// Poses a model under its setup with one or more facial atlases sampled over the
/// body pose, as a single still.
/// </summary>
/// <remarks>
/// This is the static counterpart to <see cref="SetupPose"/>: the same
/// association, world-composition guard and SCAI visibility, but with the facial
/// layers overlaid on the channels they animate and on the visibility they drive.
/// The animated counterpart, one clip timeline with facial states composed over
/// it, is a separate path.
/// </remarks>
public static class FacialPose
{
    /// <summary>
    /// Poses <paramref name="model"/> under <paramref name="setup"/> at
    /// <paramref name="seconds"/>, overlaying <paramref name="layers"/>.
    /// </summary>
    public static Result<PosedScene> Pose(
        GeometryModel model, AnimFile setup, AnimFile? clip, double seconds, ImmutableArray<FacialLayer> layers,
        bool allowMissingParts = false)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(setup);

        Result<bool> validated = PoseSampling.ValidateFacialLayers(setup, layers);
        if (!validated.IsSuccess)
        {
            return validated.Refusal;
        }

        Result<PoseSampling.Association> association =
            PoseSampling.Associate(model, setup, allowMissingParts);
        if (!association.TryGetValue(out PoseSampling.Association bindings, out Refusal? associateRefusal))
        {
            return associateRefusal;
        }

        Result<PoseSampling.LocalPose[]> localResult = PoseSampling.LayeredPoseValues(setup, clip, seconds, layers);
        if (!localResult.TryGetValue(out PoseSampling.LocalPose[]? local, out Refusal? localRefusal))
        {
            return localRefusal;
        }

        Result<bool> composed = PoseSampling.ValidateWorldComposition(setup, local);
        if (!composed.IsSuccess)
        {
            return composed.Refusal;
        }

        Result<bool[]> visibleResult = PoseSampling.LayeredVisibility(setup, clip, seconds, layers);
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

        Result<bool> hidden = PoseSampling.RefuseIfClipHidEverything(setup, clip, kept, bindings.NodeOfPart, seconds, layers);
        if (!hidden.IsSuccess)
        {
            return hidden.Refusal;
        }

        if (kept.Length == 0)
        {
            return Refusal.Unsupported("The setup hierarchy and visibility select no mesh parts.");
        }

        AnimVec3[] attachmentScales = new AnimVec3[kept.Length];
        System.Array.Fill(attachmentScales, AnimVec3.One);

        SceneGraph graph = PoseSampling.BuildGraph(model, setup, local, bindings.NodeOfPart, kept, attachmentScales);
        return Result.Ok(new PosedScene(kept, graph, bindings.Unrigged));
    }
}
