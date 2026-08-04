using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Png;

/// <summary>One decoded image: straight-alpha, row-major RGBA8.</summary>
/// <remarks>
/// The same shape a decoded DDS has, and for the same reason: it offers no way
/// to resize or resample itself, so nothing downstream can reach a resampler
/// through it.
/// </remarks>
public sealed class PngImage
{
    private readonly byte[] _pixels;

    internal PngImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Four bytes per pixel, R, G, B, A, row-major from the top-left.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;
}

/// <summary>
/// Reads a non-interlaced 8-bit truecolour PNG, with or without alpha.
/// </summary>
/// <remarks>
/// <para>
/// This exists to take an edited texture back from whatever the author drew it
/// in. The scope is deliberately the two colour types every such editor writes
/// by default: measured over the 3,279 distinct PNGs the game ships, 3,111 are
/// 8-bit RGB or RGBA. The remaining 168 — 77 interlaced, 64 greyscale with
/// alpha, 25 palette, 2 sixteen-bit — refuse by name, saying what to save
/// instead. That is a better answer than a partial decode of a file the author
/// did not realise was unusual.
/// </para>
/// <para>
/// Hand-written for the same three reasons the encoder is: no native
/// dependency to publish, no imaging library bringing a resampler within reach
/// of the bake, and a decode boundary this project controls. Inflate comes from
/// the runtime, which is the part there is no reason to write.
/// </para>
/// </remarks>
public static class PngReader
{
    private const int SignatureLength = 8;
    private const int MaxPixels = 64 << 20;

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes <paramref name="bytes"/>, the complete contents of a PNG file.</summary>
    public static Result<PngImage> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < SignatureLength || !bytes[..SignatureLength].SequenceEqual(Signature))
        {
            return Refusal.Malformed("The file does not begin with the PNG signature.");
        }

        Result<Header> read = ReadChunks(bytes, out byte[] compressed);
        if (!read.TryGetValue(out Header header, out Refusal? refusal))
        {
            return refusal;
        }

        byte[] raw;
        try
        {
            raw = Inflate(compressed, header.RawLength);
        }
        catch (InvalidDataException)
        {
            // The zlib stream is part of the file's grammar, so a stream that
            // does not inflate is a malformed file rather than a fault.
            return Refusal.Malformed("The PNG's compressed image data could not be decompressed.");
        }

        if (raw.Length < header.RawLength)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG's image data decompressed to {raw.Length} bytes, and {header.Width}x{header.Height} needs {header.RawLength}."));
        }

        return Unfilter(raw, header);
    }

    /// <summary>
    /// Walks the chunk stream, taking the header and gathering the image data.
    /// </summary>
    /// <remarks>
    /// IDAT may be split across any number of chunks, and the split may fall
    /// mid-symbol, so the parts are concatenated before a single inflate rather
    /// than inflated apiece.
    /// </remarks>
    private static Result<Header> ReadChunks(ReadOnlySpan<byte> bytes, out byte[] compressed)
    {
        compressed = [];

        Header? header = null;
        using MemoryStream data = new();
        int at = SignatureLength;

        while (at + 8 <= bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[at..]);
            if (length > int.MaxValue || at + 12L + length > bytes.Length)
            {
                return Refusal.Malformed("The PNG has a chunk running past the end of the file.");
            }

            ReadOnlySpan<byte> kind = bytes.Slice(at + 4, 4);
            ReadOnlySpan<byte> payload = bytes.Slice(at + 8, (int)length);

            if (kind.SequenceEqual("IHDR"u8))
            {
                Result<Header> read = ReadHeader(payload);
                if (!read.TryGetValue(out Header parsed, out Refusal? refusal))
                {
                    return refusal;
                }

                header = parsed;
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                data.Write(payload);
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                break;
            }

            at += 12 + (int)length;
        }

        if (header is not { } found)
        {
            return Refusal.Malformed("The PNG carries no IHDR chunk.");
        }

        if (data.Length == 0)
        {
            return Refusal.Malformed("The PNG carries no image data.");
        }

        compressed = data.ToArray();
        return Result.Ok(found);
    }

    private static Result<Header> ReadHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 13)
        {
            return Refusal.Malformed("The PNG's IHDR chunk is truncated.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(payload);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        byte depth = payload[8];
        byte colourType = payload[9];
        byte compression = payload[10];
        byte filterMethod = payload[11];
        byte interlace = payload[12];

        if (width == 0 || height == 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG declares dimensions {width}x{height}, and an image cannot be empty."));
        }

        if (compression != 0 || filterMethod != 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG declares compression method {compression} and filter method {filterMethod}; only 0 and 0 are defined."));
        }

        if (interlace != 0)
        {
            return Refusal.Unsupported(
                "This PNG is interlaced. Save it without interlacing, sometimes called progressive.");
        }

        if (depth != 8)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This PNG is {depth} bits per channel. Save it as 8-bit."));
        }

        int channels = colourType switch
        {
            2 => 3,
            6 => 4,
            _ => 0,
        };

        if (channels == 0)
        {
            string what = colourType switch
            {
                0 => "greyscale",
                3 => "indexed colour",
                4 => "greyscale with alpha",
                _ => "an unknown colour type",
            };

            return Refusal.Unsupported(
                $"This PNG is {what}. Save it as RGB or RGBA, which is what an image editor writes by default.");
        }

        if ((long)width * height > MaxPixels)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The PNG is {width}x{height}, which is more than this build will decode."));
        }

        // Each scanline carries one leading filter byte, which is why this is
        // not simply width * height * channels.
        long rawLength = ((long)width * channels + 1) * height;

        return Result.Ok(new Header((int)width, (int)height, channels, (int)rawLength));
    }

    private static byte[] Inflate(byte[] compressed, int expected)
    {
        using MemoryStream input = new(compressed);
        using ZLibStream stream = new(input, CompressionMode.Decompress);
        using MemoryStream output = new(expected);

        stream.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Reverses the per-scanline filters and widens to RGBA.
    /// </summary>
    /// <remarks>
    /// The filters are defined against the reconstructed bytes of the line
    /// above and the pixel to the left, so this walks in place over the
    /// decompressed buffer: each line is unfiltered before the next reads it.
    /// </remarks>
    private static Result<PngImage> Unfilter(byte[] raw, Header header)
    {
        int stride = header.Width * header.Channels;
        int step = header.Channels;
        byte[] pixels = new byte[header.Width * header.Height * 4];

        for (int y = 0; y < header.Height; y++)
        {
            int line = (y * (stride + 1)) + 1;
            byte filter = raw[line - 1];
            int above = line - stride - 1;

            switch (filter)
            {
                case 0:
                    break;

                case 1:
                    for (int x = step; x < stride; x++)
                    {
                        raw[line + x] += raw[line + x - step];
                    }

                    break;

                case 2:
                    if (y > 0)
                    {
                        for (int x = 0; x < stride; x++)
                        {
                            raw[line + x] += raw[above + x];
                        }
                    }

                    break;

                case 3:
                    for (int x = 0; x < stride; x++)
                    {
                        int left = x >= step ? raw[line + x - step] : 0;
                        int up = y > 0 ? raw[above + x] : 0;
                        raw[line + x] += (byte)((left + up) / 2);
                    }

                    break;

                case 4:
                    for (int x = 0; x < stride; x++)
                    {
                        int left = x >= step ? raw[line + x - step] : 0;
                        int up = y > 0 ? raw[above + x] : 0;
                        int corner = y > 0 && x >= step ? raw[above + x - step] : 0;
                        raw[line + x] += (byte)Paeth(left, up, corner);
                    }

                    break;

                default:
                    return Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The PNG's scanline {y} declares filter type {filter}, and only 0 to 4 are defined."));
            }

            int to = y * header.Width * 4;
            for (int x = 0; x < header.Width; x++)
            {
                int from = line + (x * step);
                pixels[to] = raw[from];
                pixels[to + 1] = raw[from + 1];
                pixels[to + 2] = raw[from + 2];
                pixels[to + 3] = step == 4 ? raw[from + 3] : (byte)0xFF;
                to += 4;
            }
        }

        return Result.Ok(new PngImage(header.Width, header.Height, pixels));
    }

    /// <summary>
    /// The Paeth predictor: whichever of left, above and corner the linear
    /// estimate is nearest to.
    /// </summary>
    private static int Paeth(int left, int up, int corner)
    {
        int estimate = left + up - corner;
        int fromLeft = Math.Abs(estimate - left);
        int fromUp = Math.Abs(estimate - up);
        int fromCorner = Math.Abs(estimate - corner);

        if (fromLeft <= fromUp && fromLeft <= fromCorner)
        {
            return left;
        }

        return fromUp <= fromCorner ? up : corner;
    }

    private readonly record struct Header(int Width, int Height, int Channels, int RawLength);
}
