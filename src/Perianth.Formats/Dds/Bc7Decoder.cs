using System;

namespace Perianth.Formats.Dds;

/// <summary>
/// Decodes BC7 (DX10 <c>DXGI_FORMAT_BC7_UNORM</c>).
/// </summary>
/// <remarks>
/// <para>
/// The one block format here that is a real implementation rather than an
/// afternoon: eight modes, each with its own subset count, partition width,
/// endpoint precision, P-bit scheme and index width, all read from a single
/// 128-bit little-endian bit stream whose fields have no byte alignment. The
/// per-mode numbers live in <see cref="Bc7Tables"/>; this file is the shape
/// they drive.
/// </para>
/// <para>
/// The decode is: identify the mode from the count of leading zero bits, read
/// the fields in their fixed order, assemble endpoints at the mode's precision
/// and expand them to eight bits, read one or two sets of per-texel indices,
/// then interpolate. Two details are easy to miss and silently wrong:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Anchor indices are one bit narrower.</b> The first index of every subset
/// has its high bit implied zero and is stored short. Subset zero's anchor is
/// always texel zero; the others come from tables keyed by partition. Reading
/// a full-width index there shifts every subsequent index in the block.
/// </item>
/// <item>
/// <b>Modes 4 and 5 carry two index sets</b>, one for colour and one for
/// alpha. In mode 4 the index-selection bit swaps which set drives which, and
/// the two sets have different widths, so the swap changes the weight table
/// as well as the source.
/// </item>
/// </list>
/// </remarks>
internal static class Bc7Decoder
{
    /// <summary>Bytes per compressed 4x4 block.</summary>
    internal const int BlockBytes = 16;

    private const int Texels = 16;

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
        Span<byte> texels = stackalloc byte[Texels * 4];
        DecodeTexels(block, texels);

        for (int row = 0; row < 4; row++)
        {
            int offset = ((blockY + row) * stride) + (blockX * 4);
            texels.Slice(row * 16, 16).CopyTo(destination[offset..]);
        }
    }

    /// <summary>
    /// Decodes one block to sixteen RGBA8 texels in raster order.
    /// </summary>
    internal static void DecodeTexels(ReadOnlySpan<byte> block, Span<byte> texels)
    {
        BitCursor cursor = new(block);

        // The mode is a unary prefix: as many zero bits as the mode number,
        // then a one. Eight zeroes is not a mode at all. The format calls that
        // reserved and leaves the result undefined, so the reference decides
        // it: opaque black, not transparent black. Measured, because "undefined"
        // is exactly where two implementations drift apart silently. No corpus
        // input reaches this, so the synthetic test is its only guard.
        int mode = 0;
        while (mode < 8 && cursor.Read(1) == 0)
        {
            mode++;
        }

        if (mode == 8)
        {
            texels.Clear();
            for (int texel = 0; texel < Texels; texel++)
            {
                texels[(texel * 4) + 3] = 255;
            }

            return;
        }

        Bc7Mode shape = Bc7Tables.Modes[mode];

        int partition = shape.PartitionBits > 0 ? cursor.Read(shape.PartitionBits) : 0;
        int rotation = shape.RotationBits > 0 ? cursor.Read(shape.RotationBits) : 0;
        int indexSelection = shape.IndexSelectionBits > 0 ? cursor.Read(shape.IndexSelectionBits) : 0;

        int endpointCount = shape.Subsets * 2;

        // Endpoints arrive channel-major: every red, then every green, then
        // every blue, then every alpha.
        Span<int> red = stackalloc int[6];
        Span<int> green = stackalloc int[6];
        Span<int> blue = stackalloc int[6];
        Span<int> alpha = stackalloc int[6];

        for (int i = 0; i < endpointCount; i++)
        {
            red[i] = cursor.Read(shape.ColourBits);
        }

        for (int i = 0; i < endpointCount; i++)
        {
            green[i] = cursor.Read(shape.ColourBits);
        }

        for (int i = 0; i < endpointCount; i++)
        {
            blue[i] = cursor.Read(shape.ColourBits);
        }

        if (shape.AlphaBits > 0)
        {
            for (int i = 0; i < endpointCount; i++)
            {
                alpha[i] = cursor.Read(shape.AlphaBits);
            }
        }

        // P-bits extend every component's precision by one. Either each
        // endpoint carries its own, or the two endpoints of a subset share one.
        Span<int> pbits = stackalloc int[6];
        bool hasPBits = shape.EndpointPBits > 0 || shape.SharedPBits > 0;

        if (shape.EndpointPBits > 0)
        {
            for (int i = 0; i < endpointCount; i++)
            {
                pbits[i] = cursor.Read(1);
            }
        }
        else if (shape.SharedPBits > 0)
        {
            for (int subset = 0; subset < shape.Subsets; subset++)
            {
                int bit = cursor.Read(1);
                pbits[subset * 2] = bit;
                pbits[(subset * 2) + 1] = bit;
            }
        }

        int colourPrecision = shape.ColourBits + (hasPBits ? 1 : 0);
        int alphaPrecision = shape.AlphaBits + (hasPBits && shape.AlphaBits > 0 ? 1 : 0);

        Span<byte> endpoints = stackalloc byte[6 * 4];
        for (int i = 0; i < endpointCount; i++)
        {
            endpoints[(i * 4) + 0] = Expand(Combine(red[i], pbits[i], hasPBits), colourPrecision);
            endpoints[(i * 4) + 1] = Expand(Combine(green[i], pbits[i], hasPBits), colourPrecision);
            endpoints[(i * 4) + 2] = Expand(Combine(blue[i], pbits[i], hasPBits), colourPrecision);
            endpoints[(i * 4) + 3] = shape.AlphaBits > 0
                ? Expand(Combine(alpha[i], pbits[i], hasPBits), alphaPrecision)
                : (byte)255;
        }

        scoped ReadOnlySpan<byte> partitionTable = shape.Subsets switch
        {
            2 => Bc7Tables.Partitions2.AsSpan(partition * Texels, Texels),
            3 => Bc7Tables.Partitions3.AsSpan(partition * Texels, Texels),
            _ => default,
        };

        Span<int> anchors = stackalloc int[3];
        FindAnchors(shape.Subsets, partition, anchors);

        Span<byte> primary = stackalloc byte[Texels];
        ReadIndices(ref cursor, shape.IndexBits, partitionTable, anchors, primary);

        Span<byte> secondary = stackalloc byte[Texels];
        if (shape.SecondaryIndexBits > 0)
        {
            // A second index set only ever appears in single-subset modes, so
            // texel zero is the only anchor.
            ReadIndices(ref cursor, shape.SecondaryIndexBits, default, anchors, secondary);
        }

        scoped ReadOnlySpan<byte> colourWeights;
        scoped ReadOnlySpan<byte> alphaWeights;
        scoped Span<byte> colourIndices;
        scoped Span<byte> alphaIndices;

        if (shape.SecondaryIndexBits == 0)
        {
            colourWeights = alphaWeights = WeightsFor(shape.IndexBits);
            colourIndices = alphaIndices = primary;
        }
        else if (indexSelection == 0)
        {
            colourWeights = WeightsFor(shape.IndexBits);
            alphaWeights = WeightsFor(shape.SecondaryIndexBits);
            colourIndices = primary;
            alphaIndices = secondary;
        }
        else
        {
            // Mode 4 with the selection bit set: the sets swap roles, and
            // because they are different widths the weight tables swap too.
            colourWeights = WeightsFor(shape.SecondaryIndexBits);
            alphaWeights = WeightsFor(shape.IndexBits);
            colourIndices = secondary;
            alphaIndices = primary;
        }

        for (int texel = 0; texel < Texels; texel++)
        {
            int subset = partitionTable.IsEmpty ? 0 : partitionTable[texel];
            int e0 = subset * 2 * 4;
            int e1 = e0 + 4;

            int colourWeight = colourWeights[colourIndices[texel]];
            int alphaWeight = alphaWeights[alphaIndices[texel]];

            Span<byte> output = texels.Slice(texel * 4, 4);
            output[0] = Interpolate(endpoints[e0 + 0], endpoints[e1 + 0], colourWeight);
            output[1] = Interpolate(endpoints[e0 + 1], endpoints[e1 + 1], colourWeight);
            output[2] = Interpolate(endpoints[e0 + 2], endpoints[e1 + 2], colourWeight);
            output[3] = Interpolate(endpoints[e0 + 3], endpoints[e1 + 3], alphaWeight);

            // Modes 4 and 5 may store alpha in a colour channel's place.
            if (rotation != 0)
            {
                int channel = rotation - 1;
                (output[3], output[channel]) = (output[channel], output[3]);
            }
        }
    }

    private static void ReadIndices(
        ref BitCursor cursor,
        int width,
        scoped ReadOnlySpan<byte> partitionTable,
        scoped ReadOnlySpan<int> anchors,
        scoped Span<byte> indices)
    {
        for (int texel = 0; texel < Texels; texel++)
        {
            int subset = partitionTable.IsEmpty ? 0 : partitionTable[texel];

            // The anchor of each subset stores one bit fewer, because its high
            // bit is known to be zero. Getting this wrong misaligns the rest of
            // the block rather than corrupting one texel.
            bool isAnchor = texel == anchors[subset];
            indices[texel] = (byte)cursor.Read(isAnchor ? width - 1 : width);
        }
    }

    private static void FindAnchors(int subsets, int partition, scoped Span<int> anchors)
    {
        // Subset zero always anchors at texel zero.
        anchors[0] = 0;
        anchors[1] = 0;
        anchors[2] = 0;

        if (subsets == 2)
        {
            anchors[1] = Bc7Tables.Anchors2Subset1[partition];
        }
        else if (subsets == 3)
        {
            anchors[1] = Bc7Tables.Anchors3Subset1[partition];
            anchors[2] = Bc7Tables.Anchors3Subset2[partition];
        }
    }

    private static ReadOnlySpan<byte> WeightsFor(int bits) => bits switch
    {
        2 => Bc7Tables.Weights2,
        3 => Bc7Tables.Weights3,
        _ => Bc7Tables.Weights4,
    };

    private static int Combine(int value, int pbit, bool hasPBits) =>
        hasPBits ? (value << 1) | pbit : value;

    /// <summary>
    /// Widens an endpoint component to eight bits by bit replication.
    /// </summary>
    /// <remarks>
    /// The lowest precision any mode produces is five bits, so the right shift
    /// below is never negative.
    /// </remarks>
    private static byte Expand(int value, int precision) => precision >= 8
        ? (byte)value
        : (byte)((value << (8 - precision)) | (value >> ((2 * precision) - 8)));

    private static byte Interpolate(byte a, byte b, int weight) =>
        (byte)((((64 - weight) * a) + (weight * b) + 32) >> 6);

    /// <summary>
    /// A least-significant-bit-first cursor over the block's 128 bits.
    /// </summary>
    /// <remarks>
    /// Fields straddle byte boundaries freely, so this reads across them
    /// rather than requiring alignment anywhere.
    /// </remarks>
    private ref struct BitCursor(ReadOnlySpan<byte> block)
    {
        private readonly ReadOnlySpan<byte> _block = block;
        private int _position;

        internal int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = (_block[_position >> 3] >> (_position & 7)) & 1;
                value |= bit << i;
                _position++;
            }

            return value;
        }
    }
}
