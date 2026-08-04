using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Perianth.Core.Imaging;
using Xunit;

namespace Perianth.Tests.Imaging;

public sealed class PngEncoderTests
{
    [Fact]
    public void The_signature_and_chunk_layout_are_well_formed()
    {
        byte[] png = PngEncoder.Encode(Solid(4, 4, 10, 20, 30, 255));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);

        // The chunks that must be present, in order.
        Assert.Equal(["IHDR", "IDAT", "IEND"], ChunkTypes(png));
    }

    [Fact]
    public void The_header_records_the_dimensions_and_an_rgba_eight_bit_format()
    {
        byte[] png = PngEncoder.Encode(Solid(7, 3, 0, 0, 0, 255));
        (_, ReadOnlyMemory<byte> ihdr) = Chunks(png).First();

        Assert.Equal(7, ReadBigEndian(ihdr.Span[..4]));
        Assert.Equal(3, ReadBigEndian(ihdr.Span[4..8]));
        Assert.Equal(8, ihdr.Span[8]);   // bit depth
        Assert.Equal(6, ihdr.Span[9]);   // colour type: truecolour with alpha
        Assert.Equal(0, ihdr.Span[12]);  // not interlaced
    }

    [Fact]
    public void Every_chunk_carries_a_correct_crc()
    {
        byte[] png = PngEncoder.Encode(Gradient(16, 16));

        foreach ((byte[] typeAndData, uint stored) in ChunksWithCrc(png))
        {
            Assert.Equal(Crc32(typeAndData), stored);
        }
    }

    [Fact]
    public void The_bytes_are_deterministic()
    {
        RgbaImage image = Gradient(32, 24);
        Assert.Equal(PngEncoder.Encode(image), PngEncoder.Encode(image));
    }

    [Fact]
    public void A_decoder_recovers_the_original_pixels_exactly()
    {
        // Round-trips through this project's own DDS-free path is not possible,
        // so the check is against a minimal inflate of the IDAT here: the point
        // is that the filtered, deflated payload reconstructs the source bytes.
        RgbaImage image = Gradient(19, 13);
        byte[] png = PngEncoder.Encode(image);

        byte[] recovered = DecodeRgba(png, image.Width, image.Height);
        Assert.Equal(image.Pixels.ToArray(), recovered);
    }

    [Fact]
    public void Straight_alpha_is_preserved_rather_than_premultiplied()
    {
        // A fully transparent but coloured texel keeps its colour: nothing here
        // multiplies RGB by alpha.
        RgbaImage image = Solid(2, 2, 200, 100, 50, 0);
        byte[] recovered = DecodeRgba(PngEncoder.Encode(image), 2, 2);

        Assert.Equal([200, 100, 50, 0], recovered[..4]);
    }

    private static RgbaImage Solid(int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new RgbaImage(width, height, pixels);
    }

    private static RgbaImage Gradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = ((y * width) + x) * 4;
                pixels[i] = (byte)(x * 7);
                pixels[i + 1] = (byte)(y * 11);
                pixels[i + 2] = (byte)((x + y) * 5);
                pixels[i + 3] = (byte)(255 - x);
            }
        }

        return new RgbaImage(width, height, pixels);
    }

    /// <summary>Reverses the encoder: inflate IDAT, then undo the row filters.</summary>
    private static byte[] DecodeRgba(byte[] png, int width, int height)
    {
        byte[] idat = Chunks(png).Where(c => c.Type == "IDAT").SelectMany(c => c.Data.ToArray()).ToArray();

        using MemoryStream compressed = new(idat);
        using System.IO.Compression.ZLibStream stream =
            new(compressed, System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream raw = new();
        stream.CopyTo(raw);

        byte[] filtered = raw.ToArray();
        int stride = width * 4;
        byte[] output = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            int filter = filtered[y * (stride + 1)];
            ReadOnlySpan<byte> line = filtered.AsSpan((y * (stride + 1)) + 1, stride);
            Span<byte> row = output.AsSpan(y * stride, stride);
            Span<byte> previous = y == 0 ? [] : output.AsSpan((y - 1) * stride, stride);

            for (int i = 0; i < stride; i++)
            {
                int a = i >= 4 ? row[i - 4] : 0;
                int b = previous.IsEmpty ? 0 : previous[i];
                int c = previous.IsEmpty || i < 4 ? 0 : previous[i - 4];

                int recovered = filter switch
                {
                    0 => line[i],
                    1 => line[i] + a,
                    2 => line[i] + b,
                    3 => line[i] + ((a + b) / 2),
                    _ => line[i] + Paeth(a, b, c),
                };

                row[i] = (byte)recovered;
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static System.Collections.Generic.IEnumerable<(string Type, ReadOnlyMemory<byte> Data)> Chunks(byte[] png)
    {
        int offset = 8;
        while (offset < png.Length)
        {
            int length = ReadBigEndian(png.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            yield return (type, png.AsMemory(offset + 8, length));
            offset += 12 + length;
        }
    }

    private static System.Collections.Generic.IEnumerable<(byte[] TypeAndData, uint Crc)> ChunksWithCrc(byte[] png)
    {
        int offset = 8;
        while (offset < png.Length)
        {
            int length = ReadBigEndian(png.AsSpan(offset, 4));
            byte[] typeAndData = png[(offset + 4)..(offset + 8 + length)];
            uint crc = (uint)ReadBigEndian(png.AsSpan(offset + 8 + length, 4));
            yield return (typeAndData, crc);
            offset += 12 + length;
        }
    }

    private static string[] ChunkTypes(byte[] png) => Chunks(png).Select(c => c.Type).ToArray();

    private static int ReadBigEndian(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = 0xFFFF_FFFF;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB8_8320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFF_FFFF;
    }
}
