using System;

namespace Perianth.Formats.Mmb;

/// <summary>
/// One entry of the node table that precedes the model parts.
/// </summary>
/// <remarks>
/// Nothing in export reads these: the posed hierarchy comes from the setup
/// ANIM, which is a different file and a settled decision. They are kept whole
/// because a writer must put them back, and because a reader that walks past a
/// field it does not model can only be caught by a writer trying to restore it.
/// </remarks>
/// <param name="NameBytes">The name, as serialized, without its length prefix.</param>
/// <param name="MatrixBytes">Sixty-four bytes, a 4x4 matrix the loader initialises to identity.</param>
/// <param name="Trailer">The short after the matrix, uninterpreted.</param>
public readonly record struct MmbNode(
    ReadOnlyMemory<byte> NameBytes,
    ReadOnlyMemory<byte> MatrixBytes,
    ushort Trailer)
{
    /// <summary>How many bytes a node's matrix occupies.</summary>
    public const int MatrixByteCount = 64;

    /// <summary>
    /// The <see cref="Trailer"/> value a root node carries.
    /// </summary>
    /// <remarks>
    /// The trailer is a parent index, in range on all 6,586,579 nodes measured,
    /// with 7,136 roots — about three per file (Roadmap §10.61). This is also
    /// the value the game's loader writes into the field <em>before</em> reading
    /// it, so it is the engine's own sentinel and not only the corpus's
    /// (Roadmap §10.81).
    /// </remarks>
    public const ushort NoParent = 0xFFFF;

    /// <summary>A 4x4 identity matrix, as a new node's transform.</summary>
    /// <remarks>
    /// What 98.8% of shipped nodes are within 1e-6 of, because the hierarchy
    /// lives in the parent index and the pose comes from the setup. None is
    /// bit-exact identity; that is an artefact of their exporter rather than
    /// something a writer should reproduce — and the loader itself initialises
    /// the field to identity before reading over it, so writing exact identity
    /// matches the engine rather than merely approximating the corpus
    /// (Roadmap §10.81).
    /// </remarks>
    public static ReadOnlyMemory<byte> Identity { get; } = BuildIdentity();

    private static byte[] BuildIdentity()
    {
        byte[] bytes = new byte[MatrixByteCount];
        for (int diagonal = 0; diagonal < 4; diagonal++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                bytes.AsSpan((diagonal * 5) * sizeof(float)), 1f);
        }

        return bytes;
    }
}
