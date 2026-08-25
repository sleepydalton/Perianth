using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Core.Geometry;
using Perianth.Formats.Binary;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Mmb;

namespace Perianth.Core.Content;

/// <summary>One part's vertices as an author edited them, named as the export named it.</summary>
/// <param name="Name">The mesh name, which carries the record ordinal.</param>
/// <param name="Positions">Its positions, in the export's vertex order.</param>
/// <param name="PoolSlots">
/// Which pool entry each vertex reads, when the file said so. Empty means it did
/// not, and the vertex's position in the list is used instead.
/// </param>
/// <param name="Indices">
/// Which vertex each corner draws, when the file has an index buffer. Empty
/// means the corners are the vertices, in order.
/// </param>
/// <remarks>
/// Positions are per <em>vertex</em> and indices are per <em>corner</em>, and
/// the two differ only for a part that stores an index buffer — where a vertex
/// may be drawn several times, or not at all. Keeping them apart is what lets
/// such a part be rebuilt rather than refused.
/// </remarks>
public sealed record EditedPart(
    string Name,
    ImmutableArray<Vector3D> Positions,
    ImmutableArray<int> PoolSlots = default,
    ImmutableArray<int> Indices = default,
    ImmutableArray<Vector2D> Uv0 = default)
{
    /// <summary>The position each corner draws, resolved through the indices.</summary>
    public ImmutableArray<Vector3D> Corners()
    {
        if (Indices.IsDefaultOrEmpty)
        {
            return Positions;
        }

        ImmutableArray<Vector3D>.Builder corners =
            ImmutableArray.CreateBuilder<Vector3D>(Indices.Length);
        foreach (int index in Indices)
        {
            corners.Add(Positions[index]);
        }

        return corners.MoveToImmutable();
    }
}

/// <summary>What a reshape changed.</summary>
/// <param name="Cameldata">The edited file, ready to write.</param>
/// <param name="Model">
/// The model with the reshaped parts' bounding blocks brought up to date. A
/// reshape writes no payload, but the bounding block is derived from the
/// geometry and lives in the MMB, so the model must be written after all —
/// see <see cref="MmbModelPart.WithBounds"/> for what went wrong while it was
/// not.
/// </param>
/// <param name="Parts">How many records the edit reached.</param>
/// <param name="Slots">How many pool entries it moved.</param>
/// <param name="Depths">How many of those were depth values rather than XY.</param>
/// <param name="Uv0Slots">
/// How many UV0 entries it moved, for the records that store their own texture
/// layout. A reshaped storing part used to keep the coordinates it had while its
/// points moved underneath them, so the author's edited layout was dropped with
/// nothing written to say so.
/// </param>
/// <param name="LayoutIgnored">
/// How many reshaped parts brought a texture layout that nothing read, because
/// they work theirs out from position. Counted here as the rebuild counts it:
/// the two paths must not disagree about what they did with the same mesh.
/// </param>
public sealed record GeometryEditResult(
    Mode3Cameldata Cameldata,
    MmbModel Model,
    int Parts,
    int Slots,
    int Depths,
    int Uv0Slots = 0,
    int LayoutIgnored = 0);

/// <summary>
/// Writes edited vertex positions back into a model's cameldata.
/// </summary>
/// <remarks>
/// <para>
/// The reshape stage of import. A position is not stored in the MMB: a record's
/// payload holds pool identifiers, and the position is rebuilt from the
/// cameldata's XY and Z arrays. So a reshape that keeps the vertex count writes
/// only the cameldata, and the MMB is not touched — which is what makes it
/// possible at all, since the enclosing MMB container was never derived.
/// </para>
/// <para>
/// <strong>Each record owns a private slice of the pool</strong>, which is the
/// property that makes this safe rather than delicate: no two records claim the
/// same stretch, asserted over the corpus, so editing one part cannot move
/// another.
/// </para>
/// <para>
/// Within a record it is the other way round. Records are direct, so the same
/// pool slot is written out two to twelve times and several vertices read it.
/// Moving the slot moves all of them, which is the ordinary case and needs no
/// help. What has no answer is a group whose members were moved <em>apart</em>:
/// one slot cannot hold two positions, and giving it new ones would need new
/// slots, which changes counts, which needs the container. That refuses.
/// </para>
/// </remarks>
public static class GeometryEdit
{
    internal const string NamePrefix = "mode3-record-";

    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="cameldata"/>.
    /// </summary>
    /// <remarks>
    /// Refuses when nothing matched. A reshape that quietly changed no vertex
    /// would write a mod indistinguishable from a working one, and the author
    /// would discover it in game as art that did not move.
    /// </remarks>
    public static Result<GeometryEditResult> Reshape(
        MmbModel model, CameldataFile cameldata, IReadOnlyList<EditedPart> edits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cameldata);
        ArgumentNullException.ThrowIfNull(edits);

        // Mode 2 stores positions in one absolute pool that records share --
        // measured, not assumed: two of the six mode-2 models in the corpus have
        // records sharing slots. Editing one part there would silently move
        // another, and no refusal here could detect it.
        if (cameldata is not Mode3Cameldata source)
        {
            return Refusal.Unsupported(
                "This model's cameldata is mode 2, whose position pool is shared between parts, " +
                "so reshaping one part could move another. Only mode 3 can be reshaped.");
        }

        Vector2[] xy = [.. source.Xy];
        float[] z = [.. source.Z];
        uint[] uv0 = [.. source.Uv0];
        HashSet<int> touched = [];

        // What each pool slot has been asked to become, so a second vertex
        // reaching the same slot is compared rather than overwriting the first.
        Dictionary<int, Vector2> claimedXy = [];
        Dictionary<int, float> claimedZ = [];
        Dictionary<int, uint> claimedUv0 = [];
        int parts = 0;
        int layoutIgnored = 0;

        foreach (EditedPart edit in edits)
        {
            Result<int> ordinal = Ordinal(edit.Name);
            if (!ordinal.TryGetValue(out int index, out Refusal? nameRefusal))
            {
                return nameRefusal;
            }

            if (index >= model.Parts.Length || index >= source.Constants.Length)
            {
                return Refusal.Unsupported(Describe(
                    edit.Name, $"names record {index}, and this model has {model.Parts.Length}"));
            }

            Result<bool> applied = Apply(
                edit, model.Parts[index], source.Constants[index], source,
                xy, z, uv0, claimedXy, claimedZ, claimedUv0);
            if (!applied.TryGetValue(out bool ignored, out Refusal? refusal))
            {
                return refusal;
            }

            if (ignored)
            {
                layoutIgnored++;
            }

            _ = touched.Add(index);
            parts++;
        }

        if (parts == 0)
        {
            return Refusal.Unsupported(
                "None of the meshes named a part of this model, so there was nothing to reshape. " +
                $"A mesh must keep the name the export gave it, of the form {NamePrefix}N.");
        }

        int movedXy = 0;
        foreach ((int slot, Vector2 value) in claimedXy)
        {
            if (source.Xy[slot] != value)
            {
                movedXy++;
            }
        }

        int movedZ = 0;
        foreach ((int slot, float value) in claimedZ)
        {
            if (!source.Z[slot].Equals(value))
            {
                movedZ++;
            }
        }

        int movedUv0 = 0;
        foreach ((int slot, uint value) in claimedUv0)
        {
            if (source.Uv0[slot] != value)
            {
                movedUv0++;
            }
        }

        Result<Mode3Cameldata> edited = source.WithPositions([.. xy], [.. z], [.. uv0]);
        if (!edited.TryGetValue(out Mode3Cameldata? file, out Refusal? editRefusal))
        {
            return editRefusal;
        }

        Result<MmbModel> rebounded = WithFreshBounds(model, file, touched);
        if (!rebounded.TryGetValue(out MmbModel? bounded, out Refusal? boundsRefusal))
        {
            return boundsRefusal;
        }

        return Result.Ok(
            new GeometryEditResult(file, bounded, parts, movedXy, movedZ, movedUv0, layoutIgnored));
    }

    /// <summary>
    /// The model with every reshaped part's bounding block recomputed.
    /// </summary>
    /// <remarks>
    /// The block is twelve derived floats — a box, a sphere radius about the
    /// box centre and a cylinder radius about the up axis — and moving a part's
    /// vertices invalidates all of them. A part that grew and kept its old
    /// block claims less volume than it fills, which the game may cull while it
    /// is on screen.
    /// </remarks>
    private static Result<MmbModel> WithFreshBounds(
        MmbModel model, Mode3Cameldata edited, HashSet<int> touched)
    {
        ImmutableArray<MmbModelPart>.Builder parts =
            ImmutableArray.CreateBuilder<MmbModelPart>(model.Parts.Length);

        for (int record = 0; record < model.Parts.Length; record++)
        {
            if (!touched.Contains(record))
            {
                parts.Add(model.Parts[record]);
                continue;
            }

            Result<ImmutableArray<Vector3>> cloud = PointsOf(edited, record);
            if (!cloud.TryGetValue(out ImmutableArray<Vector3> points, out Refusal? refusal))
            {
                return refusal;
            }

            parts.Add(model.Parts[record].WithBounds(MmbPartBounds.Compute(points)));
        }

        return model.WithParts(parts.MoveToImmutable());
    }

    /// <summary>Every position a record owns, after the edit.</summary>
    private static Result<ImmutableArray<Vector3>> PointsOf(Mode3Cameldata file, int record)
    {
        Mode3Constant constant = file.Constants[record];
        int start = (int)constant.XyBase;
        int end = record + 1 < file.Constants.Length
            ? (int)file.Constants[record + 1].XyBase
            : file.Xy.Length;
        int width = constant.ZBitWidth;

        if (start < 0 || end > file.Xy.Length || end < start)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Record {record} owns XY slots {start} to {end}, which its pool does not hold."));
        }

        ImmutableArray<Vector3>.Builder points =
            ImmutableArray.CreateBuilder<Vector3>(end - start);

        for (int slot = start; slot < end; slot++)
        {
            long bit = (long)slot * width;
            uint index = 0;
            for (int at = 0; at < width; at++)
            {
                long position = bit + at;
                int word = (int)(position / 32);
                if (word >= file.PackedZ.Length)
                {
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Record {record}'s packed Z index runs past the stream at slot {slot}."));
                }

                index |= ((file.PackedZ[word] >> (int)(position % 32)) & 1u) << at;
            }

            int depth = (int)constant.ZBase + (int)index;
            if (depth < 0 || depth >= file.Z.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Record {record} reads depth {depth} and its pool holds {file.Z.Length}."));
            }

            points.Add(new Vector3(file.Xy[slot].X, file.Xy[slot].Y, file.Z[depth]));
        }

        return Result.Ok(points.MoveToImmutable());
    }

    /// <summary>
    /// Moves one part's vertices, and its texture coordinates where it stores
    /// them. True where it brought a layout nothing read.
    /// </summary>
    private static Result<bool> Apply(
        EditedPart edit,
        MmbModelPart part,
        Mode3Constant constant,
        Mode3Cameldata source,
        Vector2[] xy,
        float[] z,
        uint[] uv0,
        Dictionary<int, Vector2> claimedXy,
        Dictionary<int, float> claimedZ,
        Dictionary<int, uint> claimedUv0)
    {
        // The file's own answer when it has one, and the model's otherwise.
        //
        // Matching a vertex to its pool entry by position in the list is only
        // true while whatever edited the mesh writes vertices back in the order
        // it read them. The export states the entry for exactly that reason, so
        // a tool that reorders or re-welds them moves nothing -- and the failure
        // this replaces was silent, which is why it was worth the bytes.
        ImmutableArray<int> localIds;
        if (!edit.PoolSlots.IsDefaultOrEmpty)
        {
            localIds = edit.PoolSlots;
        }
        else
        {
            Result<ImmutableArray<int>> ids = GeometryAssembler.LocalIds(part);
            if (!ids.TryGetValue(out localIds, out Refusal? refusal))
            {
                return refusal;
            }
        }

        // The count is the whole contract. Adding or removing a vertex would need
        // the MMB's descriptor to change, and its payload offset is absolute, so
        // every later record would move in a file whose container is not derived.
        if (localIds.Length != edit.Positions.Length)
        {
            return Refusal.Unsupported(Describe(
                edit.Name,
                $"has {edit.Positions.Length} vertices and the model's part has {localIds.Length}. " +
                "A reshape must keep the vertex count: adding or removing one is not this operation. " +
                "If the count changed, check that the importer did not merge vertices by distance"));
        }

        // A storing record indexes UV0 by the same identifier as XY, so a
        // coordinate per vertex is exactly what it can hold. Bringing the wrong
        // number is refused rather than written short: half a layout painted
        // over the old one is worse than either.
        bool carries = constant.UsesUnifiedUv0 && !edit.Uv0.IsDefaultOrEmpty;
        if (carries && edit.Uv0.Length != edit.Positions.Length)
        {
            return Refusal.Unsupported(Describe(
                edit.Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"brought {edit.Uv0.Length} texture coordinates for {edit.Positions.Length} points, and a part that stores its own needs one for each")));
        }

        BitReader packed = new(source.PackedZ.AsSpan());
        int width = constant.ZBitWidth;

        for (int vertex = 0; vertex < localIds.Length; vertex++)
        {
            Vector3D position = edit.Positions[vertex];
            if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
            {
                return Refusal.Malformed(Describe(edit.Name, $"has a position at vertex {vertex} that is not finite"));
            }

            long xyIndex = constant.XyBase + (long)localIds[vertex];
            if (xyIndex < 0 || xyIndex >= xy.Length)
            {
                return Refusal.Malformed(Describe(edit.Name, "references an XY entry outside the cameldata array"));
            }

            if (!packed.TryRead(xyIndex * width, width, out uint offset))
            {
                return Refusal.Malformed(Describe(edit.Name, "reads a packed Z index outside the cameldata bit stream"));
            }

            long zIndex = constant.ZBase + (long)offset;
            if (zIndex < 0 || zIndex >= z.Length)
            {
                return Refusal.Malformed(Describe(edit.Name, "references a Z entry outside the cameldata array"));
            }

            // Narrowed to float deliberately and compared after: the pool is
            // float, so two doubles that differ only past float precision are
            // the same position here and must not read as a disagreement.
            Vector2 flat = new((float)position.X, (float)position.Y);
            float depth = (float)position.Z;

            if (claimedXy.TryGetValue((int)xyIndex, out Vector2 already) && already != flat)
            {
                return Refusal.Unsupported(Describe(
                    edit.Name,
                    $"moves vertex {vertex} away from another vertex it shares a position with. " +
                    "Parts store one position per shared corner, so vertices that started together must " +
                    "move together; tearing them apart would need a vertex the part does not have"));
            }

            if (claimedZ.TryGetValue((int)zIndex, out float alreadyDepth) && !alreadyDepth.Equals(depth))
            {
                return Refusal.Unsupported(Describe(
                    edit.Name,
                    $"gives vertex {vertex} a different depth from other vertices on its plane. " +
                    "A part stores few depths -- usually one -- shared by every vertex on it, so it can be " +
                    "moved in depth as a whole but its vertices cannot be given depths of their own"));
            }

            claimedXy[(int)xyIndex] = flat;
            claimedZ[(int)zIndex] = depth;
            xy[(int)xyIndex] = flat;
            z[(int)zIndex] = depth;

            if (!carries)
            {
                continue;
            }

            // The same identifier again, and deliberately so: reading UV0 by the
            // draw vertex rather than the position id would paint a repeated
            // point from whichever of its copies came last.
            long uvIndex = constant.Uv0Base + (long)localIds[vertex];
            if (uvIndex < 0 || uvIndex >= uv0.Length)
            {
                return Refusal.Malformed(Describe(edit.Name, "references a UV0 entry outside the cameldata array"));
            }

            // Packed at the record's own scale, which is not the author's to
            // choose here: a reshape rewrites entries and changes no field, so a
            // coordinate the scale cannot express refuses rather than being
            // quietly clamped into range.
            Result<uint> encoded = Uv0Projection.Pack(edit.Uv0[vertex], constant.Uv0ScaleIndex);
            if (!encoded.TryGetValue(out uint value, out Refusal? packRefusal))
            {
                return packRefusal;
            }

            if (claimedUv0.TryGetValue((int)uvIndex, out uint alreadyUv) && alreadyUv != value)
            {
                return Refusal.Unsupported(Describe(
                    edit.Name,
                    $"gives vertex {vertex} a different texture coordinate from another vertex it shares a " +
                    "position with. A storing part holds one coordinate per shared corner, so a seam there " +
                    "would need a vertex the part does not have"));
            }

            claimedUv0[(int)uvIndex] = value;
            uv0[(int)uvIndex] = value;
        }

        return Result.Ok(constant.UsesUnifiedUv0 || edit.Uv0.IsDefaultOrEmpty ? false : true);
    }

    /// <summary>The record ordinal a mesh name carries.</summary>
    /// <remarks>
    /// Mode 2 names its meshes <c>mode2-record-N</c> and is refused before this,
    /// so a name of that shape gets the mode's refusal rather than a parse error
    /// -- the author's problem is the model, not what they typed.
    /// </remarks>
    internal static Result<int> Ordinal(string name)
    {
        if (name is null || !name.StartsWith(NamePrefix, StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(NamePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal))
        {
            return Refusal.Unsupported(
                $"The mesh '{name}' does not name a part of this model. Meshes must keep the names the " +
                $"export gave them, of the form {NamePrefix}N, because that number is which part they are.");
        }

        return Result.Ok(ordinal);
    }

    private static string Describe(string name, string what) => $"The mesh '{name}' {what}.";

    /// <summary>A result carrying only success or a refusal.</summary>
    private readonly record struct Unit;
}
