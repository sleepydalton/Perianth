using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Dds;

/// <summary>One level of a texture: straight-alpha, row-major RGBA8.</summary>
/// <param name="Width">Width in texels.</param>
/// <param name="Height">Height in texels.</param>
/// <param name="Pixels">Four bytes per texel, R, G, B, A, from the top-left.</param>
public readonly record struct DdsLevel(int Width, int Height, ReadOnlyMemory<byte> Pixels);

/// <summary>
/// Writes an uncompressed 32bpp DDS.
/// </summary>
/// <remarks>
/// <para>
/// This exists so a texture can be authored without a block encoder. The engine
/// loads an uncompressed DDS for a material — tested in game, Roadmap §6.9 —
/// which means the whole of BC1, BC3 and BC7 encoding, the part that would need
/// a rate-distortion search and would still not match the shipped encoder,
/// simply does not have to be written. A header and a byte copy replace it.
/// </para>
/// <para>
/// Deliberately not a general DDS writer. It emits one layout: 32 bits per
/// pixel, BGRA byte order, straight alpha, the shape
/// <c>textures/modules/graphics/cloud_noise.dds</c> already has and the engine
/// already loads. Offering the caller a choice of pixel format would be
/// offering a choice between one tested path and several untested ones.
/// </para>
/// <para>
/// BGRA rather than RGBA because that is the order every uncompressed texture
/// the game ships uses, and the one the in-game test was run with. The reader
/// accepts both, so this is a choice about staying near the shipped files, not
/// a constraint.
/// </para>
/// </remarks>
public static class DdsWriter
{
    private const int HeaderLength = 128;
    private const uint HeaderSize = 124;
    private const uint PixelFormatSize = 32;

    private const uint FlagCaps = 0x1;
    private const uint FlagHeight = 0x2;
    private const uint FlagWidth = 0x4;
    private const uint FlagPitch = 0x8;
    private const uint FlagPixelFormat = 0x1000;
    private const uint FlagMipMapCount = 0x20000;

    private const uint CapsTexture = 0x1000;
    private const uint CapsComplex = 0x8;
    private const uint CapsMipMap = 0x400000;

    private const uint PixelFormatAlphaPixels = 0x1;
    private const uint PixelFormatRgb = 0x40;

    private const uint BgraRedMask = 0x00FF0000;
    private const uint BgraGreenMask = 0x0000FF00;
    private const uint BgraBlueMask = 0x000000FF;
    private const uint BgraAlphaMask = 0xFF000000;

    /// <summary>Writes a single-level texture.</summary>
    public static Result<byte[]> Write(DdsLevel level) => Write([level]);

    /// <summary>
    /// Writes <paramref name="levels"/> as one file, level zero first.
    /// </summary>
    /// <remarks>
    /// The chain is checked rather than trusted. A DDS has no way to say that
    /// level three is an arbitrary size — the format's whole statement about
    /// mips is the count, and every reader derives the rest by halving. So a
    /// chain that does not halve would be written as a file whose bytes say
    /// something the caller did not mean, which is the one outcome a writer
    /// must never produce.
    /// </remarks>
    public static Result<byte[]> Write(IReadOnlyList<DdsLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);

        if (levels.Count == 0)
        {
            return Refusal.Unsupported("A texture needs at least one level.");
        }

        Result<int> checkedChain = CheckChain(levels);
        if (!checkedChain.TryGetValue(out int total, out Refusal? refusal))
        {
            return refusal;
        }

        byte[] bytes = new byte[HeaderLength + total];
        DdsLevel first = levels[0];
        bool mipped = levels.Count > 1;

        bytes[0] = (byte)'D';
        bytes[1] = (byte)'D';
        bytes[2] = (byte)'S';
        bytes[3] = (byte)' ';

        Write(bytes, 4, HeaderSize);
        Write(bytes, 8, FlagCaps | FlagHeight | FlagWidth | FlagPitch | FlagPixelFormat
            | (mipped ? FlagMipMapCount : 0));
        Write(bytes, 12, (uint)first.Height);
        Write(bytes, 16, (uint)first.Width);
        Write(bytes, 20, (uint)(first.Width * 4));
        Write(bytes, 28, (uint)levels.Count);

        Write(bytes, 76, PixelFormatSize);
        Write(bytes, 80, PixelFormatAlphaPixels | PixelFormatRgb);
        Write(bytes, 84, 0);
        Write(bytes, 88, 32);
        Write(bytes, 92, BgraRedMask);
        Write(bytes, 96, BgraGreenMask);
        Write(bytes, 100, BgraBlueMask);
        Write(bytes, 104, BgraAlphaMask);
        Write(bytes, 108, CapsTexture | (mipped ? CapsComplex | CapsMipMap : 0));

        int at = HeaderLength;
        foreach (DdsLevel level in levels)
        {
            ReadOnlySpan<byte> rgba = level.Pixels.Span;
            for (int i = 0; i < rgba.Length; i += 4)
            {
                bytes[at + i] = rgba[i + 2];
                bytes[at + i + 1] = rgba[i + 1];
                bytes[at + i + 2] = rgba[i];
                bytes[at + i + 3] = rgba[i + 3];
            }

            at += rgba.Length;
        }

        return Result.Ok(bytes);
    }

    /// <summary>
    /// Checks each level's size and dimensions, and totals the payload.
    /// </summary>
    private static Result<int> CheckChain(IReadOnlyList<DdsLevel> levels)
    {
        long total = 0;

        for (int i = 0; i < levels.Count; i++)
        {
            DdsLevel level = levels[i];

            if (level.Width <= 0 || level.Height <= 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Level {i} is {level.Width}x{level.Height}, and a texture level cannot be empty."));
            }

            long expected = (long)level.Width * level.Height * 4;
            if (level.Pixels.Length != expected)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Level {i} is {level.Width}x{level.Height}, needing {expected} bytes of RGBA, but carries {level.Pixels.Length}."));
            }

            if (i > 0)
            {
                DdsLevel previous = levels[i - 1];
                int width = Math.Max(1, previous.Width / 2);
                int height = Math.Max(1, previous.Height / 2);

                if (level.Width != width || level.Height != height)
                {
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"Level {i} is {level.Width}x{level.Height}, but a mip chain halves: after {previous.Width}x{previous.Height} it must be {width}x{height}."));
                }
            }

            total += expected;

            if (total + HeaderLength > int.MaxValue)
            {
                return Refusal.Resource(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The texture needs more than {int.MaxValue} bytes, which does not fit in one buffer."));
            }
        }

        return Result.Ok((int)total);
    }

    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
