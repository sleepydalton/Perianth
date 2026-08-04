using System;
using System.Buffers.Binary;

namespace Perianth.Formats.Dds;

/// <summary>
/// The eight-byte colour block shared by BC1 and BC3.
/// </summary>
/// <remarks>
/// <para>
/// Every arithmetic choice below was measured against the pinned reference
/// decoder rather than taken from a formulation of the format, because the
/// places implementations differ are all invisible until a hash mismatch:
/// </para>
/// <list type="bullet">
/// <item>
/// Endpoints expand from 5:6:5 to 8:8:8 by <em>bit replication</em>, so
/// <c>31</c> reaches <c>255</c> rather than <c>248</c>. A bare shift is wrong
/// by up to seven per channel and, worse, never reaches white.
/// </item>
/// <item>
/// The two interpolants are computed in <em>8-bit space, after expansion</em>.
/// Interpolating the 5:6:5 values first and expanding afterwards gives a
/// visibly different result: for endpoints 0xFFFF and 0x0000 it yields
/// (165,170,165) where the reference gives (170,170,170).
/// </item>
/// <item>
/// Both divisions <em>truncate</em>. Rounding to nearest is off by one on
/// roughly half of all endpoint pairs.
/// </item>
/// </list>
/// <para>
/// The one difference between the two formats is
/// <paramref name="allowPunchThrough"/>. In BC1, <c>colour0 &lt;= colour1</c>
/// selects a three-colour mode whose fourth index is fully transparent black.
/// In BC3 that same encoding is still the four-colour mode — alpha lives in
/// its own block and the colour block has no transparency to express. Sharing
/// this code without the flag would silently punch holes in every BC3 texture
/// whose endpoints happened to be ordered that way.
/// </para>
/// <para>
/// That flag is <em>not</em> exercised by the corpus: setting it wrongly for
/// BC3 and re-running the conformance suite leaves all 273 recorded DXT5
/// inputs still agreeing, so no BC3 block in the measured population has
/// reversed endpoints. It is kept because it is one argument rather than
/// machinery, because the format says so, and because the failure it prevents
/// is silent. The synthetic test is what holds it in place; do not read the
/// corpus pass as evidence for it.
/// </para>
/// </remarks>
internal static class BcColourBlock
{
    /// <summary>Bytes per colour block.</summary>
    internal const int BlockBytes = 8;

    /// <summary>
    /// Decodes one colour block into <paramref name="destination"/>, placing
    /// texels at the block's position.
    /// </summary>
    /// <remarks>
    /// There is no partial-block path and no clipping, because the grammar
    /// refuses any texture whose dimensions are not multiples of four. Every
    /// block-compressed texture in the surveyed corpus is block-aligned,
    /// including all 477 non-power-of-two ones. Writing the clip anyway would
    /// be unreachable code implying a case the reader has already excluded.
    /// </remarks>
    /// <param name="block">Exactly <see cref="BlockBytes"/> bytes.</param>
    /// <param name="destination">The RGBA8 image buffer.</param>
    /// <param name="stride">Bytes per image row.</param>
    /// <param name="blockX">Left texel column of this block.</param>
    /// <param name="blockY">Top texel row of this block.</param>
    /// <param name="allowPunchThrough">
    /// True for BC1, where reversed endpoints mean a transparent fourth index.
    /// False for BC3, where they do not.
    /// </param>
    internal static void Decode(
        ReadOnlySpan<byte> block,
        Span<byte> destination,
        int stride,
        int blockX,
        int blockY,
        bool allowPunchThrough)
    {
        ushort colour0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort colour1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);

        Span<byte> palette = stackalloc byte[16];
        BuildPalette(colour0, colour1, palette, allowPunchThrough);

        for (int row = 0; row < 4; row++)
        {
            int offset = ((blockY + row) * stride) + (blockX * 4);
            for (int column = 0; column < 4; column++)
            {
                // Two bits per texel, least significant first, row-major.
                int index = (int)((indices >> (2 * ((4 * row) + column))) & 3);
                palette.Slice(index * 4, 4).CopyTo(destination[(offset + (column * 4))..]);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="palette"/> with four RGBA8 entries.
    /// </summary>
    internal static void BuildPalette(
        ushort colour0,
        ushort colour1,
        Span<byte> palette,
        bool allowPunchThrough)
    {
        Expand(colour0, palette);
        Expand(colour1, palette[4..]);

        if (colour0 > colour1 || !allowPunchThrough)
        {
            // Four opaque colours: the endpoints and two thirds between them.
            for (int channel = 0; channel < 3; channel++)
            {
                int a = palette[channel];
                int b = palette[4 + channel];
                palette[8 + channel] = (byte)(((2 * a) + b) / 3);
                palette[12 + channel] = (byte)((a + (2 * b)) / 3);
            }

            palette[11] = 255;
            palette[15] = 255;
            return;
        }

        // Punch-through: one half-way colour, then a fully transparent texel.
        for (int channel = 0; channel < 3; channel++)
        {
            palette[8 + channel] = (byte)((palette[channel] + palette[4 + channel]) / 2);
            palette[12 + channel] = 0;
        }

        palette[11] = 255;
        palette[15] = 0;
    }

    /// <summary>
    /// Expands one 5:6:5 colour to opaque RGBA8 by bit replication.
    /// </summary>
    private static void Expand(ushort colour, Span<byte> destination)
    {
        int r = (colour >> 11) & 31;
        int g = (colour >> 5) & 63;
        int b = colour & 31;

        // Replication, not a shift: the low bits repeat the high ones so that
        // the maximum encodable value maps to 255 rather than to 248 or 252.
        destination[0] = (byte)((r << 3) | (r >> 2));
        destination[1] = (byte)((g << 2) | (g >> 4));
        destination[2] = (byte)((b << 3) | (b >> 2));
        destination[3] = 255;
    }
}
