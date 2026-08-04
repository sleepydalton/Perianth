using System;
using System.Globalization;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Dds;

/// <summary>
/// Decodes a DDS texture to one straight-alpha RGBA8 image at mip level zero.
/// </summary>
/// <remarks>
/// <para>
/// The supported set is three block-compressed formats and one uncompressed
/// layout; see <see cref="DdsFormat"/> for the census behind that. Everything
/// outside it refuses by name, so a texture this build cannot read produces a
/// diagnostic naming the format rather than a blank or a guess. There is no
/// codec fallback: once a texture is recognised and fails, that is the answer.
/// </para>
/// <para>
/// Uncompressed support is deliberate and was added after the fact, against an
/// explicit earlier decision to refuse it. The reason is in
/// <c>ReadUncompressed</c>; briefly, the tool now writes textures as well as
/// reading them, and the format it writes is one the engine accepts without a
/// block encoder. Reading only what the game shipped stopped being the whole
/// job.
/// </para>
/// <para>
/// Cubemap, array and volume textures refuse explicitly. Inheriting a
/// library's habit of quietly handing back face zero would export something
/// plausible and wrong. The flags are tested individually rather than by
/// asking whether <c>caps2</c> is non-zero: four files in the corpus set
/// undefined upper bits there and are ordinary 2D textures.
/// </para>
/// </remarks>
public static class DdsReader
{
    private const int MagicLength = 4;
    private const int LegacyHeaderSize = 124;
    private const int PixelFormatSize = 32;
    private const int Dx10HeaderSize = 20;

    // Offsets from the start of the file, magic included.
    private const int HeightOffset = 12;
    private const int WidthOffset = 16;
    private const int PixelFormatOffset = 76;
    private const int Caps2Offset = 112;
    private const int HeaderEnd = MagicLength + LegacyHeaderSize;

    private const uint PixelFormatFourCc = 0x4;
    private const uint PixelFormatAlphaPixels = 0x1;
    private const uint GreenMask = 0x0000FF00;
    private const uint AlphaMask = 0xFF000000;
    private const uint BgraRedMask = 0x00FF0000;
    private const uint BgraBlueMask = 0x000000FF;
    private const uint RgbaRedMask = 0x000000FF;
    private const uint RgbaBlueMask = 0x00FF0000;
    private const uint Caps2Cubemap = 0x200;
    private const uint Caps2Volume = 0x200000;
    private const uint ResourceDimensionTexture2D = 3;
    private const uint DxgiFormatBc7Unorm = 98;

    private static readonly uint FourCcDxt1 = FourCc("DXT1");
    private static readonly uint FourCcDxt5 = FourCc("DXT5");
    private static readonly uint FourCcDx10 = FourCc("DX10");

    /// <summary>
    /// Decodes <paramref name="bytes"/>, the complete contents of a DDS file.
    /// </summary>
    public static Result<DdsImage> Read(ReadOnlySpan<byte> bytes)
    {
        Result<Layout> layout = ReadLayout(bytes);
        if (!layout.TryGetValue(out Layout plan, out Refusal? refusal))
        {
            return refusal;
        }

        ReadOnlySpan<byte> payload = bytes.Slice(plan.PayloadOffset, plan.PayloadLength);

        return plan.Format switch
        {
            DdsFormat.Bc1 => Decode(payload, plan, BcColourBlock.BlockBytes),
            DdsFormat.Bc3 => Decode(payload, plan, Bc3Decoder.BlockBytes),
            DdsFormat.Uncompressed32 => CopyPixels(payload, plan),
            _ => Decode(payload, plan, Bc7Decoder.BlockBytes),
        };
    }

    /// <summary>
    /// Reads the header without decoding any texels.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Read"/> so the container grammar can be
    /// validated across thousands of files before a single block is decoded: a
    /// mismatch then names a header field rather than a pixel.
    /// </remarks>
    public static Result<DdsHeader> ReadHeader(ReadOnlySpan<byte> bytes)
    {
        Result<Layout> layout = ReadLayout(bytes);
        return layout.TryGetValue(out Layout plan, out Refusal? refusal)
            ? Result.Ok(new DdsHeader(plan.Width, plan.Height, plan.Format, plan.MipMapCount, plan.PayloadLength))
            : refusal;
    }

    private static Result<Layout> ReadLayout(ReadOnlySpan<byte> bytes)
    {
        SpanReader reader = new(bytes);

        if (!reader.TryReadBytes(MagicLength, out ReadOnlySpan<byte> magic) ||
            magic[0] != (byte)'D' || magic[1] != (byte)'D' ||
            magic[2] != (byte)'S' || magic[3] != (byte)' ')
        {
            return Refusal.Malformed("The file does not begin with the DDS magic.");
        }

        if (!reader.TryReadUInt32(out uint headerSize))
        {
            return Refusal.Malformed("The DDS header is truncated.");
        }

        if (headerSize != LegacyHeaderSize)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDS header declares a size of {headerSize} bytes rather than {LegacyHeaderSize}."));
        }

        if (bytes.Length < HeaderEnd)
        {
            return Refusal.Malformed("The DDS header is truncated.");
        }

        uint height = ReadUInt32At(bytes, HeightOffset);
        uint width = ReadUInt32At(bytes, WidthOffset);
        uint mipMapCount = ReadUInt32At(bytes, 28);

        if (ReadUInt32At(bytes, PixelFormatOffset) != PixelFormatSize)
        {
            return Refusal.Malformed("The DDS pixel-format block declares a size other than 32 bytes.");
        }

        uint caps2 = ReadUInt32At(bytes, Caps2Offset);
        if ((caps2 & Caps2Cubemap) != 0)
        {
            return Refusal.Unsupported("This DDS is a cubemap, and only 2D textures are supported.");
        }

        if ((caps2 & Caps2Volume) != 0)
        {
            return Refusal.Unsupported("This DDS is a volume texture, and only 2D textures are supported.");
        }

        Result<FormatChoice> format = ReadFormat(bytes);
        if (!format.TryGetValue(out FormatChoice choice, out Refusal? refusal))
        {
            return refusal;
        }

        if (width == 0 || height == 0)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDS declares dimensions {width}x{height}, and a texture cannot be empty."));
        }

        if (choice.UnitTexels > 1 && (width % choice.UnitTexels != 0 || height % choice.UnitTexels != 0))
        {
            // Coherent, but no partial-block path exists: every block
            // compressed texture in the surveyed corpus is block-aligned,
            // including all 477 non-power-of-two ones. An uncompressed texture
            // has no such constraint, which is why this is asked of the format
            // rather than of every texture.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDS is {width}x{height}, and block-compressed dimensions must be multiples of four."));
        }

        // Checked throughout: a header may declare any dimensions it likes, and
        // the product of two unsigned 32-bit values overflows an int long
        // before it becomes a plausible texture.
        long units = ((long)width / choice.UnitTexels) * ((long)height / choice.UnitTexels);
        long payloadLength = units * choice.UnitBytes;
        long pixelLength = (long)width * height * 4;

        if (pixelLength > int.MaxValue || payloadLength > int.MaxValue)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDS declares {width}x{height}, which does not fit in one buffer."));
        }

        // No arbitrary size ceiling: the payload has to actually be present, so
        // a header claiming enormous dimensions is caught here by the file
        // being the size it is, rather than by a limit someone has to maintain.
        if (bytes.Length - choice.PayloadOffset < payloadLength)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The DDS declares {width}x{height} of {choice.Format}, needing {payloadLength} bytes of level-zero data, but only {bytes.Length - choice.PayloadOffset} bytes follow the header."));
        }

        return Result.Ok(new Layout(
            (int)width,
            (int)height,
            choice.Format,
            (int)mipMapCount,
            choice.PayloadOffset,
            (int)payloadLength,
            choice.SwapRedBlue));
    }

    private static Result<FormatChoice> ReadFormat(ReadOnlySpan<byte> bytes)
    {
        uint pixelFlags = ReadUInt32At(bytes, PixelFormatOffset + 4);
        uint fourCc = ReadUInt32At(bytes, PixelFormatOffset + 8);

        if ((pixelFlags & PixelFormatFourCc) == 0)
        {
            return ReadUncompressed(bytes, pixelFlags);
        }

        if (fourCc == FourCcDxt1)
        {
            return Result.Ok(FormatChoice.Blocks(DdsFormat.Bc1, BcColourBlock.BlockBytes, HeaderEnd));
        }

        if (fourCc == FourCcDxt5)
        {
            return Result.Ok(FormatChoice.Blocks(DdsFormat.Bc3, 16, HeaderEnd));
        }

        if (fourCc != FourCcDx10)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS carries the pixel format '{FourCcName(fourCc)}', which is not supported."));
        }

        if (bytes.Length < HeaderEnd + Dx10HeaderSize)
        {
            return Refusal.Malformed("The DDS declares a DX10 extension header that is not present.");
        }

        uint dxgiFormat = ReadUInt32At(bytes, HeaderEnd);
        uint resourceDimension = ReadUInt32At(bytes, HeaderEnd + 4);
        uint arraySize = ReadUInt32At(bytes, HeaderEnd + 12);

        if (resourceDimension != ResourceDimensionTexture2D)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS declares resource dimension {resourceDimension}, and only 2D textures are supported."));
        }

        if (arraySize > 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS is a texture array of {arraySize} slices, and only single textures are supported."));
        }

        if (dxgiFormat != DxgiFormatBc7Unorm)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS carries DXGI format {dxgiFormat}; only {DxgiFormatBc7Unorm} (BC7_UNORM) is supported."));
        }

        return Result.Ok(FormatChoice.Blocks(DdsFormat.Bc7, 16, HeaderEnd + Dx10HeaderSize));
    }

    /// <summary>
    /// Accepts a 32bpp texture whose channel masks name a byte order this build
    /// knows, and refuses every other uncompressed layout by name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This reverses an earlier decision to refuse all uncompressed textures.
    /// That decision was right for an exporter: nine of the 47,321 files in the
    /// archives are uncompressed, all are engine textures — the console font,
    /// the colour-correction LUT, the SMAA area tables — and no material path
    /// reaches one. It stopped being right when the tool began *writing*
    /// textures. The engine loads an uncompressed DDS for a material (Roadmap
    /// §6.9, tested in game), which lets a texture be authored with no block
    /// encoder at all, and a tool that cannot read back what it just wrote is a
    /// tool with a hole in it.
    /// </para>
    /// <para>
    /// Bounded to 32bpp with alpha, in the two byte orders the shipped files
    /// use. The 24, 16 and 8 bit files still refuse, and so does a 32bpp layout
    /// with no alpha channel or unfamiliar masks: those are formats nobody has
    /// asked for, and a mask-permutation path for zero known callers is the
    /// speculative machinery this project avoids.
    /// </para>
    /// </remarks>
    private static Result<FormatChoice> ReadUncompressed(ReadOnlySpan<byte> bytes, uint pixelFlags)
    {
        uint bitCount = ReadUInt32At(bytes, PixelFormatOffset + 12);

        if (bitCount != 32 || (pixelFlags & PixelFormatAlphaPixels) == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS is uncompressed at {bitCount} bits per pixel; only 32 with an alpha channel is supported."));
        }

        uint red = ReadUInt32At(bytes, PixelFormatOffset + 16);
        uint green = ReadUInt32At(bytes, PixelFormatOffset + 20);
        uint blue = ReadUInt32At(bytes, PixelFormatOffset + 24);
        uint alpha = ReadUInt32At(bytes, PixelFormatOffset + 28);

        if (green != GreenMask || alpha != AlphaMask)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"This DDS is uncompressed with channel masks {red:X8}/{green:X8}/{blue:X8}/{alpha:X8}, which is not a supported byte order."));
        }

        // The mask says where red sits in the little-endian word, which is the
        // same thing as saying which byte it occupies: 0x00FF0000 is B,G,R,A in
        // file order and 0x000000FF is R,G,B,A.
        if (red == BgraRedMask && blue == BgraBlueMask)
        {
            return Result.Ok(FormatChoice.Pixels(swapRedBlue: true));
        }

        if (red == RgbaRedMask && blue == RgbaBlueMask)
        {
            return Result.Ok(FormatChoice.Pixels(swapRedBlue: false));
        }

        return Refusal.Unsupported(string.Create(
            CultureInfo.InvariantCulture,
            $"This DDS is uncompressed with channel masks {red:X8}/{green:X8}/{blue:X8}/{alpha:X8}, which is not a supported byte order."));
    }

    /// <summary>
    /// Reorders an uncompressed payload into the RGBA every other format
    /// decodes to.
    /// </summary>
    /// <remarks>
    /// There is nothing to decode — the texels are already texels — so this is a
    /// copy with at most a red/blue swap. Kept byte-for-byte rather than routed
    /// through anything that could resample: an uncompressed texture is the one
    /// this tool may have written itself, and a round trip that is not exact
    /// would be a silent quality loss on the modder's own work.
    /// </remarks>
    private static Result<DdsImage> CopyPixels(ReadOnlySpan<byte> payload, Layout plan)
    {
        byte[] pixels = new byte[plan.Width * plan.Height * 4];

        if (plan.SwapRedBlue)
        {
            for (int at = 0; at < pixels.Length; at += 4)
            {
                pixels[at] = payload[at + 2];
                pixels[at + 1] = payload[at + 1];
                pixels[at + 2] = payload[at];
                pixels[at + 3] = payload[at + 3];
            }
        }
        else
        {
            payload[..pixels.Length].CopyTo(pixels);
        }

        return Result.Ok(new DdsImage(plan.Width, plan.Height, plan.Format, pixels));
    }

    private static Result<DdsImage> Decode(ReadOnlySpan<byte> payload, Layout plan, int blockBytes)
    {
        byte[] pixels = new byte[plan.Width * plan.Height * 4];
        int stride = plan.Width * 4;
        int blocksAcross = plan.Width / 4;
        int blockCount = payload.Length / blockBytes;

        // Blocks run left to right, then top to bottom.
        for (int block = 0; block < blockCount; block++)
        {
            ReadOnlySpan<byte> bytes = payload.Slice(block * blockBytes, blockBytes);
            int blockX = (block % blocksAcross) * 4;
            int blockY = (block / blocksAcross) * 4;

            switch (plan.Format)
            {
                case DdsFormat.Bc1:
                    BcColourBlock.Decode(bytes, pixels, stride, blockX, blockY, allowPunchThrough: true);
                    break;
                case DdsFormat.Bc3:
                    Bc3Decoder.DecodeBlock(bytes, pixels, stride, blockX, blockY);
                    break;
                default:
                    Bc7Decoder.DecodeBlock(bytes, pixels, stride, blockX, blockY);
                    break;
            }
        }

        return Result.Ok(new DdsImage(plan.Width, plan.Height, plan.Format, pixels));
    }

    private static uint ReadUInt32At(ReadOnlySpan<byte> bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static uint FourCc(string tag) =>
        (uint)(tag[0] | (tag[1] << 8) | (tag[2] << 16) | (tag[3] << 24));

    // Renders an unrecognized tag for the refusal message. Bytes outside
    // printable ASCII become '?' so a corrupt word cannot inject control
    // characters into a diagnostic.
    private static string FourCcName(uint fourCc)
    {
        Span<char> characters = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            int value = (int)((fourCc >> (8 * i)) & 0xFF);
            characters[i] = value is >= 0x20 and < 0x7F ? (char)value : '?';
        }

        return new string(characters);
    }

    /// <summary>
    /// How a format lays its level-zero payload out: a unit of
    /// <see cref="UnitTexels"/> square costs <see cref="UnitBytes"/>.
    /// </summary>
    /// <remarks>
    /// One shape for both kinds, because a 4×4 block and a single texel differ
    /// only in those two numbers. A separate uncompressed branch through the
    /// bounds and overflow checks would be a second place for them to disagree.
    /// </remarks>
    private readonly record struct FormatChoice(
        DdsFormat Format, int UnitTexels, int UnitBytes, int PayloadOffset, bool SwapRedBlue)
    {
        public static FormatChoice Blocks(DdsFormat format, int blockBytes, int payloadOffset) =>
            new(format, UnitTexels: 4, blockBytes, payloadOffset, SwapRedBlue: false);

        public static FormatChoice Pixels(bool swapRedBlue) =>
            new(DdsFormat.Uncompressed32, UnitTexels: 1, UnitBytes: 4, HeaderEnd, swapRedBlue);
    }

    private readonly record struct Layout(
        int Width,
        int Height,
        DdsFormat Format,
        int MipMapCount,
        int PayloadOffset,
        int PayloadLength,
        bool SwapRedBlue);
}
