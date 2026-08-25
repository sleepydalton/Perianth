using System;
using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Mmb;

namespace Perianth.Core.Content;

/// <summary>
/// A model, its coordinates and its materials, after gaining a part.
/// </summary>
/// <param name="Model">The MMB with the new record appended.</param>
/// <param name="Cameldata">The cameldata with the paired constant appended.</param>
/// <param name="Editordata">
/// The editordata with the paired section appended, or null where the caller
/// supplied none. A model whose editordata is left behind still loads and still
/// draws; the new part takes no material, which
/// <see cref="PartAdd.Duplicate(MmbModel, Mode3Cameldata, EditordataFile, int, string)"/>
/// warns about rather than silently accepting.
/// </param>
/// <param name="Ordinal">The new part's ordinal, in all three files.</param>
public sealed record PartAddOutcome(
    MmbModel Model,
    Mode3Cameldata Cameldata,
    EditordataFile? Editordata,
    int Ordinal);

/// <summary>
/// Gives a model a part it did not have.
/// </summary>
/// <remarks>
/// <para>
/// The three files a part lives in are paired <b>by ordinal</b> and by nothing
/// else: part <c>N</c> of the MMB draws the coordinates of constant <c>N</c> of
/// the cameldata and is painted by section <c>N</c> of the editordata. So adding
/// a part is one operation over three files, not three operations, and this
/// exists so a caller cannot do half of it. Appending to one file alone leaves a
/// model that loads and draws wrongly, which is the failure this whole area is
/// careful about.
/// </para>
/// <para>
/// Everything is appended rather than inserted, which is what keeps it cheap: a
/// record's slice of a pool ends where the next record's base begins, so a
/// record added at the end takes a base equal to the old pool length and nothing
/// re-bases. Inserting in the middle would re-pair every part after the
/// insertion across all three files.
/// </para>
/// <para>
/// <b>Duplicating an existing part is the whole of it for now</b>, and that is
/// deliberate rather than a stub. It is the template route §6.3 names: a part
/// cloned from a sibling carries declarations, flag bytes and LOD flags that are
/// constants of the format and that nothing here would be able to invent
/// correctly. Giving the new part a mesh of its own is
/// <see cref="GeometryReplace"/>'s job, applied to the part afterwards, so the
/// two operations compose instead of one growing a copy of the other.
/// </para>
/// </remarks>
public static class PartAdd
{
    /// <summary>
    /// Appends a copy of an existing part, bound to a node the model declares.
    /// </summary>
    /// <param name="model">The model to grow.</param>
    /// <param name="cameldata">Its coordinates, which gain the paired constant.</param>
    /// <param name="editordata">Its materials, which gain the paired section.</param>
    /// <param name="source">Which part to copy, by ordinal.</param>
    /// <param name="label">
    /// The new part's label. Its first segment names the binding node and is
    /// checked against the model's own table.
    /// </param>
    public static Result<PartAddOutcome> Duplicate(
        MmbModel model,
        Mode3Cameldata cameldata,
        EditordataFile editordata,
        int source,
        string label)
    {
        ArgumentNullException.ThrowIfNull(editordata);
        Result<PartAddOutcome> added = Duplicate(model, cameldata, source, label);
        if (!added.TryGetValue(out PartAddOutcome? outcome, out Refusal? refusal))
        {
            return refusal;
        }

        if (source >= editordata.Sections.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Part {source} has no editordata section among the {editordata.Sections.Length} declared, so there is no material to copy."));
        }

        // The ordinal is the pairing key and the section carries its own copy of
        // it, so a duplicated section that kept the source's would name a part
        // that already has one.
        EditordataSection section = editordata.Sections[source] with
        {
            Ordinal = outcome.Ordinal,
        };

        return Result.Ok(outcome with
        {
            Editordata = editordata with { Sections = editordata.Sections.Add(section) },
        });
    }

    /// <summary>
    /// Appends a copy of an existing part, leaving materials alone.
    /// </summary>
    /// <remarks>
    /// For a caller that has no editordata to hand. The part will draw untextured
    /// until a section is added for it, which is a visible result rather than a
    /// silent one — and better than inventing a material this cannot know.
    /// </remarks>
    public static Result<PartAddOutcome> Duplicate(
        MmbModel model, Mode3Cameldata cameldata, int source, string label)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cameldata);
        ArgumentNullException.ThrowIfNull(label);

        // The pairing that must already hold, checked before anything is built.
        // A model whose parts and constants already disagree cannot be grown
        // into one where they agree, and appending to both would preserve the
        // disagreement while looking like an edit that worked.
        if (model.Parts.Length != cameldata.Constants.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This model has {model.Parts.Length} parts and its cameldata {cameldata.Constants.Length} constants. They pair by ordinal, so a part cannot be added until they agree."));
        }

        if (source < 0 || source >= model.Parts.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Part {source} was asked for and this model has {model.Parts.Length}."));
        }

        Result<MmbModelPart> renamed = model.Parts[source].WithLabel(label);
        if (!renamed.TryGetValue(out MmbModelPart? part, out Refusal? naming))
        {
            return naming;
        }

        // The MMB half first, because it carries the binding-node check and so
        // refuses the commonest mistake before any pool is copied.
        Result<MmbModel> grown = model.WithAppendedPart(part);
        if (!grown.TryGetValue(out MmbModel? grownModel, out Refusal? appending))
        {
            return appending;
        }

        Mode3Constant constant = cameldata.Constants[source];
        Result<PartSlice> sliced = SliceOf(cameldata, source);
        if (!sliced.TryGetValue(out PartSlice slice, out Refusal? slicing))
        {
            return slicing;
        }

        Result<Mode3Cameldata> extended = cameldata.WithAppendedRecord(
            constant, slice.Positions, slice.Depths, slice.DepthIndices, slice.Uv0);
        if (!extended.TryGetValue(out Mode3Cameldata? grownCameldata, out Refusal? appendingRecord))
        {
            return appendingRecord;
        }

        return Result.Ok(new PartAddOutcome(
            grownModel, grownCameldata, null, model.Parts.Length));
    }

    /// <summary>One record's own slice of every pool.</summary>
    private readonly record struct PartSlice(
        ImmutableArray<System.Numerics.Vector2> Positions,
        ImmutableArray<float> Depths,
        ImmutableArray<uint> DepthIndices,
        ImmutableArray<uint> Uv0);

    /// <summary>
    /// Reads out everything a record owns, so it can be written back as a new one.
    /// </summary>
    /// <remarks>
    /// A slice runs from a record's base to the next record's, or to the pool's
    /// end for the last — the same derivation every other stage uses, kept
    /// identical here so a copied part reads exactly what the original does.
    /// </remarks>
    private static Result<PartSlice> SliceOf(Mode3Cameldata cameldata, int record)
    {
        Mode3Constant constant = cameldata.Constants[record];
        int xyStart = (int)constant.XyBase;
        int xyEnd = record + 1 < cameldata.Constants.Length
            ? (int)cameldata.Constants[record + 1].XyBase
            : cameldata.Xy.Length;
        int zStart = (int)constant.ZBase;
        int zEnd = record + 1 < cameldata.Constants.Length
            ? (int)cameldata.Constants[record + 1].ZBase
            : cameldata.Z.Length;

        if (xyStart < 0 || xyEnd > cameldata.Xy.Length || xyEnd <= xyStart ||
            zStart < 0 || zEnd > cameldata.Z.Length || zEnd <= zStart)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Constant {record} owns XY slots {xyStart} to {xyEnd} and depths {zStart} to {zEnd}, which its pools do not hold."));
        }

        int width = constant.ZBitWidth;
        ImmutableArray<uint>.Builder indices =
            ImmutableArray.CreateBuilder<uint>(xyEnd - xyStart);
        for (int slot = xyStart; slot < xyEnd; slot++)
        {
            long bit = (long)slot * width;
            uint value = 0;
            for (int at = 0; at < width; at++)
            {
                long position = bit + at;
                int word = (int)(position / 32);
                if (word >= cameldata.PackedZ.Length)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Constant {record}'s packed Z index runs past the stream at slot {slot}."));
                }

                value |= ((cameldata.PackedZ[word] >> (int)(position % 32)) & 1u) << at;
            }

            indices.Add(value);
        }

        ImmutableArray<uint> uv0 = [];
        if (constant.UsesUnifiedUv0)
        {
            int uvStart = (int)constant.Uv0Base;
            int uvEnd = uvStart + (xyEnd - xyStart);
            if (uvStart < 0 || uvEnd > cameldata.Uv0.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Constant {record} reads UV0 slots {uvStart} to {uvEnd} and the array holds {cameldata.Uv0.Length}."));
            }

            uv0 = [.. cameldata.Uv0[uvStart..uvEnd]];
        }

        return Result.Ok(new PartSlice(
            [.. cameldata.Xy[xyStart..xyEnd]],
            [.. cameldata.Z[zStart..zEnd]],
            indices.MoveToImmutable(),
            uv0));
    }
}
