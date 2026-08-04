using System;
using System.Collections.Immutable;

namespace Perianth.Formats.Mmb;

/// <summary>
/// One model-part record: its envelope, its descriptor, and whatever index
/// buffer it stored.
/// </summary>
public sealed class MmbModelPart
{
    internal MmbModelPart(
        int sourceOrdinal,
        ByteRange envelope,
        string label,
        ReadOnlyMemory<byte> labelBytes,
        ImmutableArray<float> values,
        int declarationCount,
        ReadOnlyMemory<byte> declarationBytes,
        MmbGeometryDescriptor descriptor,
        ReadOnlyMemory<byte> payload,
        ImmutableArray<int> storedIndices)
    {
        Payload = payload;
        SourceOrdinal = sourceOrdinal;
        Envelope = envelope;
        Label = label;
        LabelBytes = labelBytes;
        Values = values;
        DeclarationCount = declarationCount;
        DeclarationBytes = declarationBytes;
        Descriptor = descriptor;
        StoredIndices = storedIndices;
    }

    /// <summary>Position among the records found, in byte order, from zero.</summary>
    public int SourceOrdinal { get; }

    /// <summary>Where the whole envelope sits in the MMB.</summary>
    public ByteRange Envelope { get; }

    /// <summary>The label, decoded as ASCII.</summary>
    /// <remarks>
    /// Hierarchy binding later splits this on the first <c>|</c>, differently for
    /// each mode. The split is not performed here: this is what the bytes said.
    /// </remarks>
    public string Label { get; }

    /// <summary>The label's bytes, kept so the decode is reversible.</summary>
    public ReadOnlyMemory<byte> LabelBytes { get; }

    /// <summary>The twelve floats between the label and the declaration count.</summary>
    /// <remarks>Nothing in export reads these. The record keeps them anyway.</remarks>
    public ImmutableArray<float> Values { get; }

    /// <summary>How many declaration records the envelope announced.</summary>
    public int DeclarationCount { get; }

    /// <summary>
    /// The declaration bytes themselves, uninterpreted.
    /// </summary>
    /// <remarks>
    /// Section 5.1 says declarations are "presently carried only by count", but
    /// dropping their bytes would make them unrecoverable, and section 16 asks
    /// that a consumed-but-unread field survive verbatim.
    /// </remarks>
    public ReadOnlyMemory<byte> DeclarationBytes { get; }

    /// <summary>The ten descriptor words.</summary>
    public MmbGeometryDescriptor Descriptor { get; }

    /// <summary>
    /// The record's payload bytes, bounds-checked against the file but not
    /// interpreted.
    /// </summary>
    /// <remarks>
    /// What a position entry inside this means depends on the cameldata mode —
    /// a UInt16 local identifier in mode 3, a UInt32 pool identifier in mode 2 —
    /// and this reader has not seen the cameldata. Interpreting the payload is
    /// therefore association work, and it happens where the two files meet.
    /// </remarks>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// The stored triangle indices, with the descriptor's base bias already
    /// subtracted and every value checked against the local vertex array.
    /// </summary>
    /// <remarks>
    /// Empty for a direct record, which stores no indices at all — its topology
    /// is <c>0..vertexCount-1</c> and is generated where the vertices are, not
    /// invented here. Check <see cref="MmbGeometryDescriptor.IsIndexed"/> rather
    /// than reading emptiness as an absence of geometry.
    /// </remarks>
    public ImmutableArray<int> StoredIndices { get; }
}
