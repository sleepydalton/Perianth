using System;
using System.Buffers.Binary;

namespace Perianth.Formats.Dds;

/// <summary>
/// Decodes BC3 (legacy FourCC <c>DXT5</c>): an interpolated eight-bit alpha
/// block followed by a BC1 colour block.
/// </summary>
/// <remarks>
/// <para>
/// The colour half is <see cref="BcColourBlock"/> with punch-through disabled;
/// see there for why that flag exists. This type owns only the alpha block.
/// </para>
/// <para>
/// Alpha is two endpoints and sixteen three-bit indices. When
/// <c>alpha0 &gt; alpha1</c> the six remaining slots are evenly spaced between
/// the endpoints; otherwise only four are, and the last two are hard 0 and
/// 255. Both interpolations <em>truncate</em>, measured against the pinned
/// reference: for endpoints 255 and 0 it yields 218 and 182 where rounding
/// would give 219 and 182, and for 7 and 9 it yields 7,7,8,8 where rounding
/// would give 7,8,8,9.
/// </para>
/// </remarks>
internal static class Bc3Decoder
{
    /// <summary>Bytes per compressed 4x4 block: eight of alpha, eight of colour.</summary>
    internal const int BlockBytes = 16;

    /// <summary>
    /// Decodes one block into <paramref name="destination"/>, placing texels
    /// at the block's position.
    /// </summary>
    /// <param name="block">Exactly <see cref="BlockBytes"/> bytes.</param>
    /// <param name="destination">The RGBA8 image buffer.</param>
    /// <param name="stride">Bytes per image row.</param>
    /// <param name="blockX">Left texel column of this block.</param>
    /// <param name="blockY">Top texel row of this block.</param>
    internal static void DecodeBlock(
        ReadOnlySpan<byte> block,
        Span<byte> destination,
        int stride,
        int blockX,
        int blockY)
    {
        // Colour first, which writes an opaque alpha everywhere, then the
        // alpha block overwrites it. Doing it the other way round would need
        // the colour path to know not to touch the fourth component.
        BcColourBlock.Decode(block[8..], destination, stride, blockX, blockY, allowPunchThrough: false);

        Span<byte> alphas = stackalloc byte[8];
        BuildAlphaPalette(block[0], block[1], alphas);

        // Six bytes of three-bit indices, least significant first. Read as one
        // 48-bit little-endian word so a texel's index never straddles a byte
        // boundary in the code the way it does in the encoding.
        ulong indices = BinaryPrimitives.ReadUInt32LittleEndian(block[2..])
            | ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(block[6..]) << 32);

        for (int row = 0; row < 4; row++)
        {
            int offset = ((blockY + row) * stride) + (blockX * 4);
            for (int column = 0; column < 4; column++)
            {
                int index = (int)((indices >> (3 * ((4 * row) + column))) & 7);
                destination[offset + (column * 4) + 3] = alphas[index];
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="alphas"/> with the block's eight alpha values.
    /// </summary>
    internal static void BuildAlphaPalette(byte alpha0, byte alpha1, Span<byte> alphas)
    {
        alphas[0] = alpha0;
        alphas[1] = alpha1;

        if (alpha0 > alpha1)
        {
            for (int i = 1; i < 7; i++)
            {
                alphas[i + 1] = (byte)((((7 - i) * alpha0) + (i * alpha1)) / 7);
            }

            return;
        }

        for (int i = 1; i < 5; i++)
        {
            alphas[i + 1] = (byte)((((5 - i) * alpha0) + (i * alpha1)) / 5);
        }

        alphas[6] = 0;
        alphas[7] = 255;
    }
}
