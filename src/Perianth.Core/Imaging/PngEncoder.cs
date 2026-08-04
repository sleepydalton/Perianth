using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Perianth.Core.Imaging;

/// <summary>
/// Writes one non-interlaced 8-bit RGBA PNG.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than taken from a library, for three reasons. Byte
/// identity with the reference encoder is off the table regardless — the
/// specification says a port's requirement is identical RGBA8 pixels and
/// deterministic bytes from its own pinned encoder, not a matching deflate
/// stream. Keeping the encoder here keeps single-file and ahead-of-time
/// publishing free of native dependencies. And it leaves the exactness split
/// under this project's control: a general imaging library brings a resampler
/// into reach of the bake path, which is precisely what must stay unreachable.
/// </para>
/// <para>
/// No source metadata, colour profile, gamma chunk or mip data is carried.
/// Alpha is straight, never premultiplied.
/// </para>
/// </remarks>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes <paramref name="image"/>.</summary>
    public static byte[] Encode(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        using MemoryStream output = new();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type: truecolour with alpha
        header[10] = 0;  // compression: deflate
        header[11] = 0;  // filter method: adaptive
        header[12] = 0;  // interlace: none
        WriteChunk(output, "IHDR"u8, header);

        WriteChunk(output, "IDAT"u8, Compress(image));
        WriteChunk(output, "IEND"u8, []);

        return output.ToArray();
    }

    /// <summary>
    /// Filters every scanline and deflates the result.
    /// </summary>
    /// <remarks>
    /// Filters are chosen by the minimum sum of absolute differences, the
    /// conventional heuristic. The choice affects only how well the image
    /// compresses, never what it decodes to, so it is free to be a judgement
    /// call — but it must be a <em>deterministic</em> one, which is why it is
    /// computed from the pixels rather than from anything ambient.
    /// </remarks>
    private static byte[] Compress(RgbaImage image)
    {
        int stride = image.Stride;
        ReadOnlySpan<byte> pixels = image.Pixels;

        byte[] raw = new byte[(long)(stride + 1) * image.Height <= int.MaxValue
            ? (stride + 1) * image.Height
            : throw new InvalidOperationException("The image is too large to filter in one buffer.")];

        byte[] scratch = new byte[stride * 5];

        for (int y = 0; y < image.Height; y++)
        {
            ReadOnlySpan<byte> line = pixels.Slice(y * stride, stride);
            ReadOnlySpan<byte> previous = y == 0 ? [] : pixels.Slice((y - 1) * stride, stride);

            int best = 0;
            long bestScore = long.MaxValue;

            for (int filter = 0; filter < 5; filter++)
            {
                Span<byte> target = scratch.AsSpan(filter * stride, stride);
                long score = Filter(filter, line, previous, target);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = filter;
                }
            }

            int destination = y * (stride + 1);
            raw[destination] = (byte)best;
            scratch.AsSpan(best * stride, stride).CopyTo(raw.AsSpan(destination + 1));
        }

        using MemoryStream compressed = new();
        using (ZLibStream stream = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            stream.Write(raw);
        }

        return compressed.ToArray();
    }

    /// <summary>
    /// Applies one PNG filter to a scanline and returns its heuristic score.
    /// </summary>
    private static long Filter(
        int filter,
        ReadOnlySpan<byte> line,
        ReadOnlySpan<byte> previous,
        Span<byte> target)
    {
        const int BytesPerPixel = 4;
        long score = 0;

        for (int i = 0; i < line.Length; i++)
        {
            int a = i >= BytesPerPixel ? line[i - BytesPerPixel] : 0;
            int b = previous.IsEmpty ? 0 : previous[i];
            int c = previous.IsEmpty || i < BytesPerPixel ? 0 : previous[i - BytesPerPixel];

            int value = filter switch
            {
                0 => line[i],
                1 => line[i] - a,
                2 => line[i] - b,
                3 => line[i] - ((a + b) / 2),
                _ => line[i] - Paeth(a, b, c),
            };

            byte encoded = (byte)value;
            target[i] = encoded;
            score += encoded < 128 ? encoded : 256 - encoded;
        }

        return score;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);

        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);

        uint crc = 0xFFFF_FFFF;
        crc = Accumulate(crc, type);
        crc = Accumulate(crc, data);

        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, crc ^ 0xFFFF_FFFF);
        output.Write(checksum);
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB8_8320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
