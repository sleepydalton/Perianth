using System.Collections.Generic;
using System.Collections.Immutable;

namespace Perianth.Core.Geometry;

/// <summary>
/// One drawable part: its vertices resolved, its topology settled, and its UV0
/// and normals derived.
/// </summary>
public sealed class GeometryPart
{
    internal GeometryPart(
        int sourceOrdinal,
        string name,
        string sourceLabel,
        string hierarchyBindingName,
        ImmutableArray<Vector3D> positions,
        ImmutableArray<int> indices,
        ImmutableArray<Vector2D> uv0,
        ImmutableArray<Vector3D> normals,
        ImmutableArray<int> poolSlots = default)
    {
        SourceOrdinal = sourceOrdinal;
        Name = name;
        SourceLabel = sourceLabel;
        HierarchyBindingName = hierarchyBindingName;
        Positions = positions;
        Indices = indices;
        PoolSlots = poolSlots.IsDefault ? [] : poolSlots;
        Uv0 = uv0;
        Normals = normals;
    }

    /// <summary>The model part's ordinal in the MMB, from zero.</summary>
    public int SourceOrdinal { get; }

    /// <summary>The mesh name, <c>mode2-record-N</c> or <c>mode3-record-N</c>.</summary>
    public string Name { get; }

    /// <summary>The complete label the MMB carried, kept alongside the name.</summary>
    public string SourceLabel { get; }

    /// <summary>
    /// The part of the label that must equal a setup node's name exactly.
    /// </summary>
    /// <remarks>
    /// Mode 2 binds on the text after the first <c>|</c> and mode 3 on the text
    /// before it. Nothing here matches on a suffix or a basename; the equality
    /// is exact, and hierarchy association in a later step depends on it.
    /// </remarks>
    public string HierarchyBindingName { get; }

    /// <summary>Vertex positions, in source vertex order.</summary>
    public ImmutableArray<Vector3D> Positions { get; }

    /// <summary>Triangle indices into <see cref="Positions"/>.</summary>
    public ImmutableArray<int> Indices { get; }

    /// <summary>
    /// Which entry of the source's shared pool each vertex reads.
    /// </summary>
    /// <remarks>
    /// Carried so an import can be told rather than having to infer it. Matching
    /// a vertex back to its pool entry by its position in this list is only true
    /// while whatever edited the mesh writes vertices back in the order it read
    /// them; stating the entry instead means a tool that reorders or re-welds
    /// them moves nothing. Empty when the source did not supply them.
    /// </remarks>
    public ImmutableArray<int> PoolSlots { get; }

    /// <summary>
    /// UV0 in source vertex order, or empty when this part has none.
    /// </summary>
    /// <remarks>
    /// Duplicate direct vertices keep their own entries rather than being
    /// merged.
    /// </remarks>
    public ImmutableArray<Vector2D> Uv0 { get; }

    /// <summary>Area-weighted vertex normals.</summary>
    public ImmutableArray<Vector3D> Normals { get; }

    /// <summary>Whether this part carries UV0.</summary>
    public bool HasUv0 => !Uv0.IsEmpty;
}

/// <summary>
/// The parts an MMB and its cameldata produced together.
/// </summary>
public sealed class GeometryModel
{
    internal GeometryModel(int mode, ImmutableArray<GeometryPart> parts, bool surfaceUv0Unavailable)
    {
        Mode = mode;
        Parts = parts;
        SurfaceUv0Unavailable = surfaceUv0Unavailable;
    }

    /// <summary>The cameldata mode the parts were resolved against.</summary>
    public int Mode { get; }

    /// <summary>The parts, in MMB order.</summary>
    public ImmutableArray<GeometryPart> Parts { get; }

    /// <summary>
    /// Whether mode-2 surface UV0 was unavailable because the constant count did
    /// not equal the record count.
    /// </summary>
    /// <remarks>
    /// Geometry still exports; this later prevents applying a material. It is
    /// not a refusal, and mode 3 treats the same mismatch as one, because there
    /// the counts are the association.
    /// </remarks>
    public bool SurfaceUv0Unavailable { get; }

    /// <summary>
    /// Returns a model holding only the parts at <paramref name="partIndices"/>,
    /// in that order.
    /// </summary>
    /// <remarks>
    /// The one caller is material reconstruction dropping emissive companion
    /// geometry: their surface is merged onto a base and never drawn on its own.
    /// The kept parts are unchanged, so this composes a new model rather than
    /// editing one.
    /// </remarks>
    public GeometryModel SelectParts(ImmutableArray<int> partIndices)
    {
        ImmutableArray<GeometryPart>.Builder kept = ImmutableArray.CreateBuilder<GeometryPart>(partIndices.Length);
        foreach (int index in partIndices)
        {
            kept.Add(Parts[index]);
        }

        return new GeometryModel(Mode, kept.MoveToImmutable(), SurfaceUv0Unavailable);
    }

    /// <summary>
    /// Returns a model whose parts at the given indices have their UV0 rewritten
    /// by the affine transform <c>(u * su + ou, v * sv + ov)</c>.
    /// </summary>
    /// <remarks>
    /// The one caller is a tile bake consuming <c>myUVRepeat</c> into the pixels:
    /// the repeat is dropped and the primitive's coordinates are moved to address
    /// the baked region. Keyed by part index, applied to the full model before
    /// any surviving-parts selection, so the indices are the source ordinals.
    /// Untouched parts are shared rather than copied.
    /// </remarks>
    public GeometryModel RewriteUv0(IReadOnlyDictionary<int, (double ScaleU, double ScaleV, double OffsetU, double OffsetV)> byPartIndex)
    {
        if (byPartIndex.Count == 0)
        {
            return this;
        }

        ImmutableArray<GeometryPart>.Builder rewritten = ImmutableArray.CreateBuilder<GeometryPart>(Parts.Length);
        for (int index = 0; index < Parts.Length; index++)
        {
            GeometryPart part = Parts[index];
            if (!byPartIndex.TryGetValue(index, out (double ScaleU, double ScaleV, double OffsetU, double OffsetV) remap))
            {
                rewritten.Add(part);
                continue;
            }

            ImmutableArray<Vector2D>.Builder uv0 = ImmutableArray.CreateBuilder<Vector2D>(part.Uv0.Length);
            foreach (Vector2D coordinate in part.Uv0)
            {
                uv0.Add(new Vector2D(
                    (coordinate.X * remap.ScaleU) + remap.OffsetU,
                    (coordinate.Y * remap.ScaleV) + remap.OffsetV));
            }

            rewritten.Add(new GeometryPart(
                part.SourceOrdinal,
                part.Name,
                part.SourceLabel,
                part.HierarchyBindingName,
                part.Positions,
                part.Indices,
                uv0.MoveToImmutable(),
                part.Normals,
                // Carried through, like everything else this rebuild does not
                // touch. Rewriting a coordinate says nothing about which pool
                // entry a vertex reads, and dropping it here would have made the
                // export state the mapping for some models and not for others.
                part.PoolSlots));
        }

        return new GeometryModel(Mode, rewritten.MoveToImmutable(), SurfaceUv0Unavailable);
    }
}
