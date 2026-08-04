using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Png;
using Xunit;

namespace Perianth.Tests.Png;

/// <summary>
/// Checks the PNG reader without a corpus.
/// </summary>
/// <remarks>
/// The conformance suite is the real oracle for the filters; these cover the
/// grammar and the refusals, which real shipped files cannot exercise because
/// they are all well-formed. The one round trip here is against
/// <see cref="PngEncoder"/>, and it is worth exactly what it says: that the two
/// halves of this project agree. It is not evidence about filters 1 to 4.
/// </remarks>
public sealed class PngReaderTests
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Builds a PNG with one chosen filter on every scanline.</summary>
    private static byte[] Build(
        int width,
        int height,
        int channels,
        byte[] pixels,
        byte filter = 0,
        byte bitDepth = 8,
        byte interlace = 0,
        byte? colourTypeOverride = null)
    {
        byte colourType = colourTypeOverride ?? (byte)(channels == 4 ? 6 : 2);
        int stride = width * channels;

        // Filtered as the encoder would, so the reader has something to undo.
        byte[] raw = new byte[(stride + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int line = y * (stride + 1);
            raw[line] = filter;
            for (int x = 0; x < stride; x++)
            {
                int value = pixels[(y * stride) + x];
                int left = x >= channels ? pixels[(y * stride) + x - channels] : 0;
                int up = y > 0 ? pixels[((y - 1) * stride) + x] : 0;
                int corner = y > 0 && x >= channels ? pixels[((y - 1) * stride) + x - channels] : 0;

                raw[line + 1 + x] = filter switch
                {
                    1 => (byte)(value - left),
                    2 => (byte)(value - up),
                    3 => (byte)(value - ((left + up) / 2)),
                    4 => (byte)(value - Paeth(left, up, corner)),
                    _ => (byte)value,
                };
            }
        }

        using MemoryStream output = new();
        output.Write(Signature);

        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = bitDepth;
        header[9] = colourType;
        header[12] = interlace;
        WriteChunk(output, "IHDR"u8, header);

        using MemoryStream deflated = new();
        using (ZLibStream stream = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            stream.Write(raw);
        }

        WriteChunk(output, "IDAT"u8, deflated.ToArray());
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static int Paeth(int left, int up, int corner)
    {
        int estimate = left + up - corner;
        int a = Math.Abs(estimate - left);
        int b = Math.Abs(estimate - up);
        int c = Math.Abs(estimate - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> kind, ReadOnlySpan<byte> payload)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, payload.Length);
        output.Write(length);
        output.Write(kind);
        output.Write(payload);

        // The reader does not verify CRCs, so a placeholder is honest here: a
        // real one would imply a check that is not made.
        output.Write(stackalloc byte[4]);
    }

    private static byte[] Ramp(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            bytes[i] = (byte)((i * 37) + (i / 5));
        }

        return bytes;
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    public void Every_filter_reconstructs_the_pixels(byte filter)
    {
        byte[] pixels = Ramp(5 * 4 * 4);

        Result<PngImage> read = PngReader.Read(Build(5, 4, 4, pixels, filter));

        Assert.False(read.IsRefused, read.IsRefused ? read.Refusal.Message : null);
        Assert.Equal(pixels, read.Value.Pixels.ToArray());
    }

    [Fact]
    public void An_rgb_image_is_widened_to_opaque_rgba()
    {
        byte[] pixels = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60];

        Result<PngImage> read = PngReader.Read(Build(2, 1, 3, pixels));

        Assert.Equal(
            [0x10, 0x20, 0x30, 0xFF, 0x40, 0x50, 0x60, 0xFF],
            read.Value.Pixels.ToArray());
    }

    [Fact]
    public void Image_data_split_across_chunks_reads_as_one_stream()
    {
        // A split may fall mid-symbol, so the parts have to be joined before
        // inflating rather than inflated apiece. Encoders split freely.
        byte[] whole = Build(4, 4, 4, Ramp(4 * 4 * 4), filter: 4);
        byte[] split = SplitIdat(whole);

        Assert.Equal(
            PngReader.Read(whole).Value.Pixels.ToArray(),
            PngReader.Read(split).Value.Pixels.ToArray());
    }

    [Fact]
    public void What_the_encoder_writes_the_reader_reads()
    {
        // Worth what it says and no more: the two halves agree. The filter
        // coverage that matters comes from the conformance suite.
        RgbaImage image = new(6, 3, Ramp(6 * 3 * 4));

        Result<PngImage> read = PngReader.Read(PngEncoder.Encode(image));

        Assert.Equal(image.Pixels.ToArray(), read.Value.Pixels.ToArray());
    }

    [Fact]
    public void An_interlaced_image_refuses_and_says_what_to_do()
    {
        Result<PngImage> read = PngReader.Read(Build(4, 4, 4, Ramp(64), interlace: 1));

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
        Assert.Contains("interlac", read.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData((byte)0, "greyscale")]
    [InlineData((byte)3, "indexed")]
    [InlineData((byte)4, "greyscale with alpha")]
    public void An_unsupported_colour_type_refuses_by_name(byte colourType, string expected)
    {
        Result<PngImage> read = PngReader.Read(
            Build(4, 4, 4, Ramp(64), colourTypeOverride: colourType));

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
        Assert.Contains(expected, read.Refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sixteen_bits_per_channel_refuses()
    {
        Result<PngImage> read = PngReader.Read(Build(4, 4, 4, Ramp(64), bitDepth: 16));

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
        Assert.Contains("8-bit", read.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_is_not_a_png_refuses()
    {
        Result<PngImage> read = PngReader.Read("not a picture"u8);

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Malformed, read.Refusal.Kind);
    }

    [Fact]
    public void A_truncated_image_is_malformed_rather_than_a_partial_picture()
    {
        // The one outcome that must not happen: half an image returned as
        // though it were whole.
        byte[] bytes = Build(8, 8, 4, Ramp(8 * 8 * 4), filter: 1);
        byte[] cut = ShortenIdat(bytes);

        Result<PngImage> read = PngReader.Read(cut);

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Malformed, read.Refusal.Kind);
    }

    [Fact]
    public void A_file_with_no_header_chunk_refuses()
    {
        byte[] bytes = [.. Signature];

        Result<PngImage> read = PngReader.Read(bytes);

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Malformed, read.Refusal.Kind);
    }

    [Fact]
    public void A_chunk_length_running_past_the_end_refuses()
    {
        byte[] bytes = Build(2, 2, 4, Ramp(16));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0xFFFF);

        Result<PngImage> read = PngReader.Read(bytes);

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Malformed, read.Refusal.Kind);
    }

    /// <summary>Rewrites the single IDAT as two, splitting its payload in half.</summary>
    private static byte[] SplitIdat(byte[] bytes)
    {
        (int at, int length) = FindIdat(bytes);
        ReadOnlySpan<byte> payload = bytes.AsSpan(at + 8, length);
        int half = length / 2;

        using MemoryStream output = new();
        output.Write(bytes.AsSpan(0, at));
        WriteChunk(output, "IDAT"u8, payload[..half]);
        WriteChunk(output, "IDAT"u8, payload[half..]);
        output.Write(bytes.AsSpan(at + 12 + length));
        return output.ToArray();
    }

    /// <summary>Drops the tail of the IDAT payload, keeping the file well-formed.</summary>
    private static byte[] ShortenIdat(byte[] bytes)
    {
        (int at, int length) = FindIdat(bytes);

        using MemoryStream output = new();
        output.Write(bytes.AsSpan(0, at));
        WriteChunk(output, "IDAT"u8, bytes.AsSpan(at + 8, length / 3));
        output.Write(bytes.AsSpan(at + 12 + length));
        return output.ToArray();
    }

    private static (int At, int Length) FindIdat(byte[] bytes)
    {
        int at = 8;
        while (at + 8 <= bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(at));
            if (bytes.AsSpan(at + 4, 4).SequenceEqual("IDAT"u8))
            {
                return (at, length);
            }

            at += 12 + length;
        }

        throw new InvalidOperationException("the fixture has no IDAT chunk");
    }
}
