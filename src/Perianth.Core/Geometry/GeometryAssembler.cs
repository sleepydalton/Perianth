using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Perianth.Formats.Binary;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Mmb;

namespace Perianth.Core.Geometry;

/// <summary>
/// Joins an MMB's model-part records to a cameldata file and resolves the
/// vertices they describe between them.
/// </summary>
/// <remarks>
/// Neither file means anything alone. The MMB stores identifiers whose width and
/// meaning the cameldata mode decides, and the cameldata stores pools whose
/// consumers are the MMB's records; this is where those two facts meet, which is
/// why the mode-dependent half of the descriptor rules is enforced here rather
/// than in the reader that could not have known the mode.
/// </remarks>
public static class GeometryAssembler
{
    private const int Mode2EntrySize = sizeof(uint);
    private const int Mode3EntrySize = sizeof(ushort);

    /// <summary>Resolves every model part against <paramref name="cameldata"/>.</summary>
    public static Result<GeometryModel> Assemble(MmbModel model, CameldataFile cameldata)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(cameldata);

        return cameldata switch
        {
            Mode2Cameldata mode2 => AssembleMode2(model, mode2),
            Mode3Cameldata mode3 => AssembleMode3(model, mode3),
            _ => Refusal.Unsupported("The cameldata is of a mode this build cannot assemble."),
        };
    }

    private static Result<GeometryModel> AssembleMode2(MmbModel model, Mode2Cameldata cameldata)
    {
        // Mode 2 tolerates a count mismatch: geometry still exports and only UV0
        // is lost, which later prevents applying a material. Mode 3 cannot,
        // because there the ordinals are the association itself.
        bool uv0Available = model.Parts.Length == cameldata.Constants.Length;

        ImmutableArray<GeometryPart>.Builder parts =
            ImmutableArray.CreateBuilder<GeometryPart>(model.Parts.Length);

        foreach (MmbModelPart part in model.Parts)
        {
            Result<ImmutableArray<Vector3D>> positions = ReadMode2Positions(part, cameldata);
            if (!positions.IsSuccess)
            {
                return positions.Refusal;
            }

            Result<ImmutableArray<int>> indices = ResolveIndices(part, Mode2EntrySize, mode3: false);
            if (!indices.IsSuccess)
            {
                return indices.Refusal;
            }

            ImmutableArray<Vector2D> uv0 = [];
            if (uv0Available)
            {
                Uv0Projection.SurfaceTerms terms =
                    Uv0Projection.SurfaceTerms.From(cameldata.Constants[part.SourceOrdinal]);

                ImmutableArray<Vector2D>.Builder projected =
                    ImmutableArray.CreateBuilder<Vector2D>(positions.Value.Length);
                foreach (Vector3D position in positions.Value)
                {
                    projected.Add(Uv0Projection.Surface(position, terms));
                }

                uv0 = projected.MoveToImmutable();
            }

            Result<GeometryPart> assembled = Finish(part, 2, positions.Value, indices.Value, uv0);
            if (!assembled.IsSuccess)
            {
                return assembled.Refusal;
            }

            parts.Add(assembled.Value);
        }

        return Result.Ok(new GeometryModel(2, parts.MoveToImmutable(), !uv0Available));
    }

    private static Result<GeometryModel> AssembleMode3(MmbModel model, Mode3Cameldata cameldata)
    {
        if (model.Parts.Length != cameldata.Constants.Length)
        {
            // No second association rule is known, and guessing one would attach
            // the wrong surface to the wrong geometry silently.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The model has {model.Parts.Length} records and the cameldata has {cameldata.Constants.Length} constants, and mode 3 associates them one to one."));
        }

        ImmutableArray<GeometryPart>.Builder parts =
            ImmutableArray.CreateBuilder<GeometryPart>(model.Parts.Length);

        foreach (MmbModelPart part in model.Parts)
        {
            Mode3Constant constant = cameldata.Constants[part.SourceOrdinal];

            Result<ImmutableArray<int>> localIds = ReadMode3LocalIds(part);
            if (!localIds.IsSuccess)
            {
                return localIds.Refusal;
            }

            Result<ImmutableArray<Vector3D>> positions = ResolveMode3Positions(localIds.Value, constant, cameldata, part.SourceOrdinal);
            if (!positions.IsSuccess)
            {
                return positions.Refusal;
            }

            Result<ImmutableArray<int>> indices = ResolveIndices(part, Mode3EntrySize, mode3: true);
            if (!indices.IsSuccess)
            {
                return indices.Refusal;
            }

            Result<ImmutableArray<Vector2D>> uv0 = constant.UsesUnifiedUv0
                ? ResolveUnifiedUv0(localIds.Value, constant, cameldata, part.SourceOrdinal)
                : ProjectSurfaceUv0(positions.Value, Uv0Projection.SurfaceTerms.From(constant));
            if (!uv0.IsSuccess)
            {
                return uv0.Refusal;
            }

            Result<GeometryPart> assembled = Finish(part, 3, positions.Value, indices.Value, uv0.Value, localIds.Value);
            if (!assembled.IsSuccess)
            {
                return assembled.Refusal;
            }

            parts.Add(assembled.Value);
        }

        return Result.Ok(new GeometryModel(3, parts.MoveToImmutable(), surfaceUv0Unavailable: false));
    }

    private static Result<ImmutableArray<Vector3D>> ReadMode2Positions(MmbModelPart part, Mode2Cameldata cameldata)
    {
        MmbGeometryDescriptor descriptor = part.Descriptor;
        ReadOnlySpan<byte> payload = part.Payload.Span;

        if (!BoundedRange.TryResolve(
                payload.Length, descriptor.Stream0Offset, descriptor.VertexCount, Mode2EntrySize,
                out int start, out _))
        {
            return Malformed(part, "has position data that does not lie inside its payload");
        }

        SpanReader reader = new(payload);
        if (!reader.TrySeek(start))
        {
            return Malformed(part, "has position data that does not lie inside its payload");
        }

        ImmutableArray<Vector3D>.Builder positions =
            ImmutableArray.CreateBuilder<Vector3D>((int)descriptor.VertexCount);

        for (uint vertex = 0; vertex < descriptor.VertexCount; vertex++)
        {
            if (!reader.TryReadUInt32(out uint entry))
            {
                return Malformed(part, "has a truncated position stream");
            }

            if ((entry >> 16) != 0)
            {
                return Malformed(part, "stores a mode-2 position identifier whose high half is not zero");
            }

            int id = (int)(entry & 0xFFFF);
            if (id >= cameldata.Positions.Length)
            {
                return Malformed(part, "references a position beyond the cameldata pool");
            }

            Vector3 pooled = cameldata.Positions[id];
            positions.Add(new Vector3D(pooled.X, pooled.Y, pooled.Z));
        }

        return Result.Ok(positions.MoveToImmutable());
    }

    /// <summary>
    /// The pool identifier each of a record's vertices reads, in vertex order.
    /// </summary>
    /// <remarks>
    /// Internal because the geometry edit inverts exactly this: it needs the same
    /// identifiers, resolved by the same checks, or it would write positions into
    /// slots the export never read. Two implementations of one mapping is the way
    /// an importer and an exporter come to disagree.
    /// </remarks>
    internal static Result<ImmutableArray<int>> LocalIds(MmbModelPart part) => ReadMode3LocalIds(part);

    private static Result<ImmutableArray<int>> ReadMode3LocalIds(MmbModelPart part)
    {
        MmbGeometryDescriptor descriptor = part.Descriptor;

        if (descriptor.Stream0Offset != 0)
        {
            return Malformed(part, "is mode 3 but names a nonzero stream-0 offset");
        }

        if (descriptor.Mode3Reserved != 0)
        {
            return Malformed(part, "is mode 3 but its reserved descriptor word is not zero");
        }

        if (part.DeclarationCount == 1 && descriptor.SecondaryVertexCount != descriptor.VertexCount)
        {
            return Malformed(part, "is mode 3 with one declaration but its check field is not its vertex count");
        }

        long declaredEnd = (long)descriptor.IndexOffset + ((long)descriptor.IndexCount * Mode3EntrySize);
        if (declaredEnd != descriptor.PayloadLength)
        {
            return Malformed(part, "is mode 3 but its payload does not end exactly after its index buffer");
        }

        ReadOnlySpan<byte> payload = part.Payload.Span;
        if (!BoundedRange.TryResolve(
                payload.Length, 0, descriptor.VertexCount, Mode3EntrySize, out _, out _))
        {
            return Malformed(part, "has position data that does not lie inside its payload");
        }

        SpanReader reader = new(payload);
        ImmutableArray<int>.Builder ids = ImmutableArray.CreateBuilder<int>((int)descriptor.VertexCount);
        for (uint vertex = 0; vertex < descriptor.VertexCount; vertex++)
        {
            if (!reader.TryReadUInt16(out ushort id))
            {
                return Malformed(part, "has a truncated position stream");
            }

            ids.Add(id);
        }

        return Result.Ok(ids.MoveToImmutable());
    }

    private static Result<ImmutableArray<Vector3D>> ResolveMode3Positions(
        ImmutableArray<int> localIds,
        Mode3Constant constant,
        Mode3Cameldata cameldata,
        int ordinal)
    {
        BitReader packed = new(cameldata.PackedZ.AsSpan());
        int width = constant.ZBitWidth;

        ImmutableArray<Vector3D>.Builder positions =
            ImmutableArray.CreateBuilder<Vector3D>(localIds.Length);

        foreach (int id in localIds)
        {
            long xyIndex = constant.XyBase + (long)id;
            if (xyIndex < 0 || xyIndex >= cameldata.Xy.Length)
            {
                return MalformedOrdinal(ordinal, "references an XY entry outside the cameldata array");
            }

            if (!packed.TryRead(xyIndex * width, width, out uint offset))
            {
                return MalformedOrdinal(ordinal, "reads a packed Z index outside the cameldata bit stream");
            }

            long zIndex = constant.ZBase + (long)offset;
            if (zIndex < 0 || zIndex >= cameldata.Z.Length)
            {
                return MalformedOrdinal(ordinal, "references a Z entry outside the cameldata array");
            }

            Vector2 xy = cameldata.Xy[(int)xyIndex];
            positions.Add(new Vector3D(xy.X, xy.Y, cameldata.Z[(int)zIndex]));
        }

        return Result.Ok(positions.MoveToImmutable());
    }

    private static Result<ImmutableArray<Vector2D>> ResolveUnifiedUv0(
        ImmutableArray<int> localIds,
        Mode3Constant constant,
        Mode3Cameldata cameldata,
        int ordinal)
    {
        ImmutableArray<Vector2D>.Builder uv0 = ImmutableArray.CreateBuilder<Vector2D>(localIds.Length);

        foreach (int id in localIds)
        {
            // The shader reads uv0Offset + Gfx_PosId: the position identifier,
            // the same one the XY array is indexed by, not the draw vertex.
            long index = constant.Uv0Base + (long)id;
            if (index < 0 || index >= cameldata.Uv0.Length)
            {
                return MalformedOrdinal(ordinal, "references a UV0 entry outside the cameldata array");
            }

            Result<Vector2D> component = Uv0Projection.Unified(cameldata.Uv0[(int)index], constant.Uv0ScaleIndex);
            if (!component.IsSuccess)
            {
                return component.Refusal;
            }

            uv0.Add(component.Value);
        }

        return Result.Ok(uv0.MoveToImmutable());
    }

    private static Result<ImmutableArray<Vector2D>> ProjectSurfaceUv0(
        ImmutableArray<Vector3D> positions,
        Uv0Projection.SurfaceTerms terms)
    {
        ImmutableArray<Vector2D>.Builder uv0 = ImmutableArray.CreateBuilder<Vector2D>(positions.Length);
        foreach (Vector3D position in positions)
        {
            uv0.Add(Uv0Projection.Surface(position, terms));
        }

        return Result.Ok(uv0.MoveToImmutable());
    }

    private static Result<ImmutableArray<int>> ResolveIndices(MmbModelPart part, int entrySize, bool mode3)
    {
        MmbGeometryDescriptor descriptor = part.Descriptor;

        if (!mode3 && descriptor.IsIndexed)
        {
            long positionsEnd = (long)descriptor.Stream0Offset + ((long)descriptor.VertexCount * entrySize);
            long indexStart = descriptor.IndexOffset;
            if (indexStart > positionsEnd)
            {
                long auxiliary = descriptor.AuxiliaryStreamOffset;
                if (auxiliary == 0 || auxiliary < positionsEnd || auxiliary >= indexStart)
                {
                    // Described bytes between the positions and the indices that
                    // no auxiliary stream accounts for. The layout is coherent
                    // and simply not understood, so this is unsupported.
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Model part {part.SourceOrdinal} leaves an unexplained gap between its positions and its indices, which is a local-position layout this build does not read."));
                }
            }
        }

        if (descriptor.IsIndexed)
        {
            return Result.Ok(part.StoredIndices);
        }

        // A direct record stores no indices; its topology is its vertex order.
        ImmutableArray<int>.Builder indices = ImmutableArray.CreateBuilder<int>((int)descriptor.VertexCount);
        for (int vertex = 0; vertex < descriptor.VertexCount; vertex++)
        {
            indices.Add(vertex);
        }

        return Result.Ok(indices.MoveToImmutable());
    }

    private static Result<GeometryPart> Finish(
        MmbModelPart part,
        int mode,
        ImmutableArray<Vector3D> positions,
        ImmutableArray<int> indices,
        ImmutableArray<Vector2D> uv0,
        ImmutableArray<int> poolSlots = default)
    {
        Result<ImmutableArray<Vector3D>> normals =
            VertexNormals.Compute(positions, indices, part.SourceOrdinal);
        if (!normals.IsSuccess)
        {
            return normals.Refusal;
        }

        return Result.Ok(new GeometryPart(
            part.SourceOrdinal,
            string.Create(CultureInfo.InvariantCulture, $"mode{mode}-record-{part.SourceOrdinal}"),
            part.Label,
            HierarchyBindingName(part.Label, mode),
            positions,
            indices,
            uv0,
            normals.Value,
            poolSlots));
    }

    /// <summary>
    /// The part of a label that must equal a setup node's name exactly.
    /// </summary>
    /// <remarks>
    /// Mode 2 takes what follows the first pipe and mode 3 takes what precedes
    /// it. A label without a pipe is its own binding name in both modes.
    /// </remarks>
    private static string HierarchyBindingName(string label, int mode)
    {
        // The char overload is ordinal by definition; no comparison to specify.
        int pipe = label.IndexOf('|');
        if (pipe < 0)
        {
            return label;
        }

        return mode == 2 ? label[(pipe + 1)..] : label[..pipe];
    }

    private static Refusal Malformed(MmbModelPart part, string problem) =>
        MalformedOrdinal(part.SourceOrdinal, problem);

    private static Refusal MalformedOrdinal(int ordinal, string problem) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture, $"Model part {ordinal} {problem}."));
}
