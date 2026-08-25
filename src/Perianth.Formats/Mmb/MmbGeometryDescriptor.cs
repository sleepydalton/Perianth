namespace Perianth.Formats.Mmb;

/// <summary>
/// The ten descriptor words that follow a model-part envelope, exactly as
/// serialized.
/// </summary>
/// <remarks>
/// Every word is kept, including the ones export never consults, because the
/// record is meant to be a transcript rather than a summary.
/// </remarks>
/// <param name="VertexCount">Word 0. Must be nonzero.</param>
/// <param name="BaseBias">Word 1. Subtracted from every stored index.</param>
/// <param name="IndexCount">Word 2. Zero means direct; anything else means indexed.</param>
/// <param name="SecondaryVertexCount">Word 3. A secondary vertex-count or check field.</param>
/// <param name="Stream0Offset">Word 4. Position data, relative to the payload.</param>
/// <param name="AuxiliaryStreamOffset">Word 5. An auxiliary stream, or zero.</param>
/// <param name="IndexOffset">Word 6. Index data, relative to the payload.</param>
/// <param name="PayloadOffset">Word 7. Absolute offset of the payload in the MMB.</param>
/// <param name="PayloadLength">Word 8. Payload length in bytes.</param>
/// <param name="Mode3Reserved">Word 9. Reserved, and required to be zero in mode 3.</param>
public readonly record struct MmbGeometryDescriptor(
    uint VertexCount,
    uint BaseBias,
    uint IndexCount,
    uint SecondaryVertexCount,
    uint Stream0Offset,
    uint AuxiliaryStreamOffset,
    uint IndexOffset,
    uint PayloadOffset,
    uint PayloadLength,
    uint Mode3Reserved)
{
    /// <summary>
    /// Whether the record stores an index buffer.
    /// </summary>
    /// <remarks>
    /// A zero index count is the complete direct-versus-indexed predicate.
    /// Nothing else participates, and no other field may be consulted to decide
    /// it.
    /// </remarks>
    public bool IsIndexed => IndexCount != 0;

    /// <summary>
    /// Whether a mode-3 payload is exactly its identifiers and its index buffer,
    /// with nothing between them and nothing after.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The predicate that says a payload may be <em>rebuilt</em> rather than
    /// overwritten in place. A payload holding nothing else can be written afresh
    /// at a different size, because there is nothing unaccounted for to relocate
    /// or invent — and inventing is what the byte-identity oracle cannot check.
    /// </para>
    /// <para>
    /// It is one expression for both kinds. A direct record's index count is
    /// zero, so it reduces to the payload being identifiers alone. Measured over
    /// 6,139 indexed records in 2,283 files: of the 1,595 whose identifiers are
    /// <c>u16</c> and immediately followed by the index buffer — the population
    /// this project writes — <b>every one accounts for every byte</b>. The rest
    /// are a conventional interleaved vertex format, mostly effects, and mode 2.
    /// Roadmap §10.58.
    /// </para>
    /// <para>
    /// Checked per record rather than assumed from that number, so a part
    /// carrying anything else keeps the in-place path and its limits.
    /// </para>
    /// </remarks>
    public bool AccountsForEveryByte =>
        IndexOffset == (long)VertexCount * sizeof(ushort) &&
        PayloadLength == IndexOffset + ((long)IndexCount * sizeof(ushort));
}
