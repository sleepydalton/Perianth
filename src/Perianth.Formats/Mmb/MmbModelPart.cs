using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using Perianth.Formats.Diagnostics;

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
        ImmutableArray<int> storedIndices,
        int matrixCount,
        ReadOnlyMemory<byte> matrixBytes,
        ReadOnlyMemory<byte> flagBytes,
        uint lodFlags,
        ReadOnlyMemory<byte> tailBytes)
    {
        MatrixCount = matrixCount;
        MatrixBytes = matrixBytes;
        FlagBytes = flagBytes;
        LodFlags = lodFlags;
        TailBytes = tailBytes;
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

    /// <summary>
    /// The same part with a different descriptor, payload and bounding block.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one opening a resize gets, and narrow for the same reason
    /// <c>WithPositions</c> is: everything else about a part — its name, its
    /// transform, its declarations, its matrices, its tail — describes what the
    /// part <em>is</em> rather than what it draws, and a resize has no business
    /// touching any of it.
    /// </para>
    /// <para>
    /// <paramref name="values"/> is not in that category. It is
    /// <see cref="MmbPartBounds"/> — the part's own bounding box and vertex
    /// radii — so it describes exactly what a changed geometry changes, and
    /// carrying it over would leave the part claiming a volume it no longer
    /// occupies. It is required rather than optional so that a caller has to
    /// have thought about it.
    /// </para>
    /// </remarks>
    public MmbModelPart WithGeometry(
        MmbGeometryDescriptor descriptor, ReadOnlyMemory<byte> payload, ImmutableArray<float> values) =>
        new(SourceOrdinal, Envelope, Label, LabelBytes, values, DeclarationCount,
            DeclarationBytes, descriptor, payload, StoredIndices, MatrixCount,
            MatrixBytes, FlagBytes, LodFlags, RestatedTail(TailBytes, descriptor));

    /// <summary>
    /// This part with its bounding block recomputed and nothing else changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a <em>reshape</em> needs. A reshape moves pool entries and writes no
    /// payload, so for three rungs it wrote no MMB at all — and the bounding
    /// block lives in the MMB, so every reshaped part went on claiming the
    /// volume it used to fill. §10.65 put the rule in <see cref="WithGeometry"/>
    /// and only the rebuild path calls it.
    /// </para>
    /// <para>
    /// The harm has a direction. A part reshaped <em>smaller</em> claims more
    /// than it fills, which only costs a draw that could have been skipped; a
    /// part reshaped <em>larger</em> claims less, and the game may cull it while
    /// it is on screen. Enlarging something is the ordinary thing an author
    /// does, and an offline render cannot show the fault, because it does not
    /// cull.
    /// </para>
    /// </remarks>
    public MmbModelPart WithBounds(ImmutableArray<float> values) =>
        new(SourceOrdinal, Envelope, Label, LabelBytes, values, DeclarationCount,
            DeclarationBytes, Descriptor, Payload, StoredIndices, MatrixCount,
            MatrixBytes, FlagBytes, LodFlags, TailBytes);

    /// <summary>
    /// The tail, with the two words in it that restate the geometry brought up
    /// to date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tail is written back verbatim, and two of its words are not opaque:
    /// the third from last is the record's <b>vertex count</b> and the last is
    /// its <b>payload length</b>, on 433,944 parts of 433,944 (Roadmap §10.67).
    /// The record already states both in its descriptor, so a resize that
    /// updated one and not the other leaves a file disagreeing with itself.
    /// </para>
    /// <para>
    /// It lives here rather than in the caller because that is the fourth field
    /// today found stale after a geometry change, and a rule kept beside the
    /// thing it derives from cannot be forgotten by the next caller.
    /// </para>
    /// </remarks>
    private static ReadOnlyMemory<byte> RestatedTail(
        ReadOnlyMemory<byte> tail, MmbGeometryDescriptor descriptor)
    {
        // Every tail a reader produces holds at least four trailing words; a
        // shorter one cannot have come from a file this build read.
        if (tail.Length < sizeof(uint) * 3)
        {
            return tail;
        }

        byte[] restated = tail.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            restated.AsSpan(restated.Length - (sizeof(uint) * 3)), descriptor.VertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(
            restated.AsSpan(restated.Length - sizeof(uint)), descriptor.PayloadLength);
        return restated;
    }

    /// <summary>The same part under a different name.</summary>
    /// <remarks>
    /// <para>
    /// A part's label is a Maya path — <c>group454|spmPlanar1</c> — and its
    /// <b>first segment names the node it binds to</b>, on 64,103 of 64,103
    /// parts measured. So this is not cosmetic: renaming a part rebinds it, and
    /// <see cref="MmbModel.WithAppendedPart"/> checks the new name against the
    /// model's own node table rather than trusting it.
    /// </para>
    /// <para>
    /// ASCII, because that is what every label in the corpus is and what the
    /// length-prefixed encoding stores. A name that will not round-trip refuses
    /// here rather than being written as replacement characters.
    /// </para>
    /// </remarks>
    public Result<MmbModelPart> WithLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length == 0)
        {
            return Refusal.Unsupported("A part's label is what binds it to a node, so it cannot be empty.");
        }

        foreach (char character in label)
        {
            if (character is < ' ' or > '~')
            {
                return Refusal.Unsupported(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"A part's label is stored as ASCII and {nameof(label)} contains U+{(int)character:X4}."));
            }
        }

        return Result.Ok(new MmbModelPart(
            SourceOrdinal, Envelope, label, System.Text.Encoding.ASCII.GetBytes(label),
            Values, DeclarationCount, DeclarationBytes, Descriptor, Payload,
            StoredIndices, MatrixCount, MatrixBytes, FlagBytes, LodFlags, TailBytes));
    }

    /// <summary>
    /// The same part renumbered, with the one field a part that never existed
    /// before has to choose.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SourceOrdinal"/> is not provenance. It indexes the cameldata
    /// constant and the editordata section this part is paired with, so a part
    /// appended to a model takes the next ordinal or it draws another part's
    /// geometry with another part's material.
    /// </para>
    /// <para>
    /// The first word of <see cref="TailBytes"/> is the float at part offset
    /// <c>+0xa0</c>, and <b>a new part writes 1.0</b> — settled by decision on
    /// 2026-08-15, Roadmap §10.78, after fourteen readings of the field died.
    /// It is computed rather than defaulted, so this is not "leave it unset": it
    /// is a common legal value (23.7% of shipped parts carry it exactly), it
    /// never reaches the GPU, and it is the identity or the conservative choice
    /// under every reading still standing — and correct under the best-supported
    /// one, since geometry written at final scale has no freeze residual.
    /// </para>
    /// <para>
    /// A part carried over from a template keeps everything else: its
    /// declarations, its matrices, its flag bytes and its LOD flags are what
    /// make the record legal, and none of them is this operation's to invent.
    /// </para>
    /// </remarks>
    internal MmbModelPart AsAppended(int ordinal) =>
        new(ordinal, default, Label, LabelBytes, Values, DeclarationCount,
            DeclarationBytes, Descriptor, Payload, StoredIndices, MatrixCount,
            MatrixBytes, FlagBytes, LodFlags, WithNewPartField(TailBytes));

    /// <summary>The tail with its first word set to 1.0f.</summary>
    private static ReadOnlyMemory<byte> WithNewPartField(ReadOnlyMemory<byte> tail)
    {
        if (tail.Length < sizeof(float))
        {
            return tail;
        }

        byte[] written = tail.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(written, 1f);
        return written;
    }

    /// <summary>Position among the records found, in byte order, from zero.</summary>
    /// <remarks>
    /// Also the pairing key: it indexes the cameldata constant and the
    /// editordata section belonging to this part, so it is not free to differ
    /// from the part's position once a model has been edited.
    /// </remarks>
    public int SourceOrdinal { get; }

    /// <summary>Where the whole envelope sits in the MMB.</summary>
    public ByteRange Envelope { get; }

    /// <summary>The label, decoded as ASCII.</summary>
    /// <remarks>
    /// Hierarchy binding later splits this on the first <c>|</c>, differently for
    /// each mode. The split is not performed here: this is what the bytes said.
    /// </remarks>
    public string Label { get; }

    /// <summary>The node this part binds to: the label's first segment.</summary>
    /// <remarks>
    /// A part's label is a Maya path — <c>group454|spmPlanar1</c> — and the node
    /// it hangs off is the segment before the first bar, on 64,103 of 64,103
    /// parts measured. The last segment matched once and the whole string never,
    /// so this is the segment rather than a guess among several.
    /// </remarks>
    public string BindingNode
    {
        get
        {
            int bar = Label.IndexOf('|', StringComparison.Ordinal);
            return bar < 0 ? Label : Label[..bar];
        }
    }

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

    /// <summary>How many 4x4 matrices the part carries before its descriptor.</summary>
    /// <remarks>
    /// Zero on 441,864 of the 441,865 parts measured, which is why the signature
    /// scan that preceded this reader could pin it to zero and still find almost
    /// everything. The one part that carries any is in a debug object, and the
    /// scan reported its whole file as holding no records at all.
    /// </remarks>
    public int MatrixCount { get; }

    /// <summary>
    /// Those matrices as serialized, each 64 bytes followed by a short.
    /// </summary>
    /// <remarks>
    /// Uninterpreted and kept whole. Nothing in export reads them; a writer must
    /// be able to put them back, and §16 asks that a consumed-but-unread field
    /// survive verbatim rather than being reconstructed from a count.
    /// </remarks>
    public ReadOnlyMemory<byte> MatrixBytes { get; }

    /// <summary>
    /// The version-gated bytes between the transform block and the declarations.
    /// </summary>
    /// <remarks>
    /// One from version 8, a second above version 9, and none below. The scan
    /// this reader replaced called them a "zero prefix" and required them to be
    /// zero, because a printable run followed by two zero bytes was how it
    /// recognised a record. They are flags the loader reads.
    /// </remarks>
    public ReadOnlyMemory<byte> FlagBytes { get; }

    /// <summary>
    /// The word before the level-of-detail entries, from version 7.
    /// </summary>
    /// <remarks>
    /// <c>0xF0000000</c> throughout the corpus, which is why it was the tail of
    /// the scan's "exact seven-byte suffix". Below version 7 the loader derives
    /// it from the entry count instead, and this is left zero.
    /// </remarks>
    public uint LodFlags { get; }

    /// <summary>
    /// Everything between the descriptor and the next part, verbatim.
    /// </summary>
    /// <remarks>
    /// Two words, a counted block of words, a short, a version-gated short, and
    /// four more words — the last of which is the payload length below version
    /// 9, where payloads are concatenated and the loader tracks a cursor. None
    /// of it is read here and all of it must be written back, so it is kept as
    /// bytes rather than modelled into fields nobody can name.
    /// </remarks>
    public ReadOnlyMemory<byte> TailBytes { get; }
}
