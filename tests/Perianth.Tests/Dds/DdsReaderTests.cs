using System;
using System.Buffers.Binary;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Dds;

public sealed class DdsReaderTests
{
    [Fact]
    public void A_dxt1_texture_reports_its_header_without_decoding()
    {
        DdsHeader header = ReadHeaderOk(new DdsFileBuilder { Width = 8, Height = 12, MipMapCount = 4 });

        Assert.Equal(8, header.Width);
        Assert.Equal(12, header.Height);
        Assert.Equal(DdsFormat.Bc1, header.Format);

        // Declared, and deliberately unused: only level zero is ever decoded.
        Assert.Equal(4, header.MipMapCount);
        Assert.Equal(2 * 3 * 8, header.PayloadLength);
    }

    [Theory]
    [InlineData("DXT5", DdsFormat.Bc3, 16)]
    [InlineData("DXT1", DdsFormat.Bc1, 8)]
    public void The_legacy_four_cc_names_the_format(string fourCc, DdsFormat expected, int blockBytes)
    {
        DdsHeader header = ReadHeaderOk(new DdsFileBuilder { FourCc = fourCc, BlockBytes = blockBytes });
        Assert.Equal(expected, header.Format);
    }

    [Fact]
    public void A_dx10_header_naming_bc7_unorm_is_recognised()
    {
        DdsHeader header = ReadHeaderOk(new DdsFileBuilder
        {
            FourCc = "DX10",
            Dx10 = new DdsFileBuilder.Dx10Extension(),
            BlockBytes = 16,
        });

        Assert.Equal(DdsFormat.Bc7, header.Format);
    }

    [Fact]
    public void Bad_magic_is_malformed()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { Magic = "DDX " });
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void A_header_size_other_than_124_is_malformed()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { HeaderSize = 120 });
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void A_pixel_format_block_of_the_wrong_size_is_malformed()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { PixelFormatSize = 24 });
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void A_truncated_payload_is_malformed_and_says_what_was_missing()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder
        {
            Width = 8,
            Height = 8,
            Payload = new byte[16],
        });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("32 bytes of level-zero data", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_uncompressed_texture_is_unsupported_and_names_its_bit_depth()
    {
        // The corpus holds exactly one of these, a colour-grading LUT no
        // editordata references. It refuses by name rather than growing a
        // pixel-mask path for a single unreachable file.
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { PixelFlags = 0x40, BitCount = 32 });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("32 bits per pixel", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_four_cc_is_named_in_the_refusal()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { FourCc = "DXT3", BlockBytes = 16 });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("DXT3", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dxgi_format_other_than_bc7_unorm_is_named_in_the_refusal()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder
        {
            FourCc = "DX10",
            Dx10 = new DdsFileBuilder.Dx10Extension(DxgiFormat: 77),
            BlockBytes = 16,
        });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("77", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cubemap_refuses_rather_than_handing_back_one_face()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { Caps2 = 0x200 });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("cubemap", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_volume_texture_refuses()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { Caps2 = 0x200000 });
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
    }

    [Fact]
    public void Undefined_upper_bits_in_caps2_are_not_a_cubemap()
    {
        // Four corpus files set 0xFE000000 here and are ordinary 2D textures.
        // Testing "caps2 is non-zero" instead of the two real flags would
        // refuse every one of them.
        DdsHeader header = ReadHeaderOk(new DdsFileBuilder { Caps2 = 0xFE000000 });
        Assert.Equal(DdsFormat.Bc1, header.Format);
    }

    [Fact]
    public void A_texture_array_refuses()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder
        {
            FourCc = "DX10",
            Dx10 = new DdsFileBuilder.Dx10Extension(ArraySize: 6),
            BlockBytes = 16,
        });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("6 slices", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dimensions_that_are_not_multiples_of_four_are_unsupported()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder
        {
            Width = 6,
            Height = 4,
            Payload = new byte[64],
        });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("multiples of four", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_dimensions_are_malformed()
    {
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder { Width = 0, Payload = [] });
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void Dimensions_too_large_to_buffer_are_a_resource_refusal()
    {
        // No size ceiling is imposed, so this must be caught by the arithmetic
        // rather than by a limit: 65532 squared texels is over 17 GB of RGBA8.
        Refusal refusal = ReadHeaderRefused(new DdsFileBuilder
        {
            Width = 65532,
            Height = 65532,
            Payload = [],
        });

        Assert.Equal(RefusalKind.Resource, refusal.Kind);
    }

    [Fact]
    public void Bc1_expands_endpoints_by_replication_and_interpolates_in_eight_bit_space()
    {
        // Measured against the pinned reference decoder. White-to-black gives
        // 170 and 85; interpolating in 5:6:5 space first would give 165 for
        // the red and blue channels of the first interpolant.
        byte[] pixels = DecodeOneBlock(colour0: 0xFFFF, colour1: 0x0000, indices: 0b11100100);

        Assert.Equal([255, 255, 255, 255], Texel(pixels, 0));
        Assert.Equal([0, 0, 0, 255], Texel(pixels, 1));
        Assert.Equal([170, 170, 170, 255], Texel(pixels, 2));
        Assert.Equal([85, 85, 85, 255], Texel(pixels, 3));
    }

    [Fact]
    public void Bc1_truncates_its_interpolants_rather_than_rounding()
    {
        // Endpoints (140,142,74) and (132,138,66). Truncating gives 137,140,71
        // for the two-thirds point; rounding to nearest gives 141 for green.
        byte[] pixels = DecodeOneBlock(colour0: 0x8C69, colour1: 0x8448, indices: 0b11100100);

        Assert.Equal([140, 142, 74, 255], Texel(pixels, 0));
        Assert.Equal([137, 140, 71, 255], Texel(pixels, 2));
    }

    [Fact]
    public void Bc1_punch_through_makes_the_fourth_index_fully_transparent()
    {
        // colour0 <= colour1 selects the three-colour mode: one half-way
        // colour, then a texel that is transparent in all four components
        // rather than an opaque black.
        byte[] pixels = DecodeOneBlock(colour0: 0x0821, colour1: 0x1062, indices: 0b11100100);

        Assert.Equal([8, 4, 8, 255], Texel(pixels, 0));
        Assert.Equal([16, 12, 16, 255], Texel(pixels, 1));
        Assert.Equal([12, 8, 12, 255], Texel(pixels, 2));
        Assert.Equal([0, 0, 0, 0], Texel(pixels, 3));
    }

    [Fact]
    public void Bc1_indices_are_two_bits_per_texel_least_significant_first()
    {
        // Every texel index 1, so the whole block is the second endpoint.
        byte[] pixels = DecodeOneBlock(colour0: 0xFFFF, colour1: 0x0000, indices: 0x5555_5555);

        for (int texel = 0; texel < 16; texel++)
        {
            Assert.Equal([0, 0, 0, 255], Texel(pixels, texel));
        }
    }

    [Fact]
    public void Blocks_are_laid_out_left_to_right_then_top_to_bottom()
    {
        // Two blocks across, one down. The first is all white, the second all
        // black, so a decoder that walked them in the other order, or that got
        // the stride wrong, puts the black block on the left.
        byte[] payload = new byte[16];
        WriteBlock(payload.AsSpan(0), 0xFFFF, 0xFFFF, 0);
        WriteBlock(payload.AsSpan(8), 0x0000, 0x0000, 0);

        DdsImage image = ReadOk(new DdsFileBuilder { Width = 8, Height = 4, Payload = payload });

        Assert.Equal(255, image.Pixels[0]);
        Assert.Equal(0, image.Pixels[4 * 4]);

        // Second row of the left block is still white, which is the stride.
        Assert.Equal(255, image.Pixels[8 * 4]);
    }

    [Fact]
    public void Bc3_spreads_six_alpha_interpolants_when_the_first_endpoint_is_larger()
    {
        // Measured: truncating gives 218 and 182 for the first two
        // interpolants of 255/0, where rounding would give 219 and 182.
        byte[] pixels = DecodeBc3Block(alpha0: 255, alpha1: 0);

        Assert.Equal([255, 0, 218, 182, 145, 109, 72, 36], AlphaRun(pixels));
    }

    [Fact]
    public void Bc3_uses_four_interpolants_and_hard_ends_when_the_first_endpoint_is_smaller()
    {
        byte[] pixels = DecodeBc3Block(alpha0: 40, alpha1: 200);

        // The last two slots are hard 0 and 255, not interpolated.
        Assert.Equal([40, 200, 72, 104, 136, 168, 0, 255], AlphaRun(pixels));
    }

    [Fact]
    public void Bc3_truncates_its_alpha_interpolants_rather_than_rounding()
    {
        // Endpoints 7 and 9 in the four-interpolant mode. Truncating gives
        // 7,7,8,8; rounding to nearest gives 7,8,8,9.
        byte[] pixels = DecodeBc3Block(alpha0: 7, alpha1: 9);

        Assert.Equal([7, 9, 7, 7, 8, 8, 0, 255], AlphaRun(pixels));
    }

    [Fact]
    public void Bc3_colour_never_takes_the_punch_through_branch()
    {
        // The same reversed endpoints that make a BC1 block transparent. In
        // BC3 alpha lives in its own block, so this is still the four-colour
        // mode: index 2 is the one-third point (10,6,10), not the half-way
        // point (12,8,12), and index 3 stays opaque.
        byte[] pixels = DecodeBc3Block(alpha0: 255, alpha1: 255, colour0: 0x0821, colour1: 0x1062);

        Assert.Equal([8, 4, 8, 255], Texel(pixels, 0));
        Assert.Equal([16, 12, 16, 255], Texel(pixels, 1));
        Assert.Equal([10, 6, 10, 255], Texel(pixels, 2));
        Assert.Equal([13, 9, 13, 255], Texel(pixels, 3));
    }

    [Fact]
    public void Bc3_alpha_indices_are_three_bits_per_texel_across_six_bytes()
    {
        // Index 7 everywhere. In the four-interpolant mode that is the hard
        // 255, so any texel whose three-bit index was assembled wrongly comes
        // back at some other alpha.
        byte[] payload = new byte[16];
        payload[0] = 40;
        payload[1] = 200;
        payload.AsSpan(2, 6).Fill(0xFF);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8), 0xFFFF);

        DdsImage image = ReadOk(new DdsFileBuilder { FourCc = "DXT5", Payload = payload });

        for (int texel = 0; texel < 16; texel++)
        {
            Assert.Equal(255, image.Pixels[(texel * 4) + 3]);
        }
    }

    [Fact]
    public void Bc7_mode_six_interpolates_on_the_four_bit_weight_table()
    {
        // Endpoints black and white, then indices 0 to 15 across the sixteen
        // texels, so each texel's grey *is* its weight scaled to 0-255. This
        // pins every entry of the four-bit table. Entry 13 was transcribed as
        // 56 rather than 55 and, because mode 6 is the only mode with four-bit
        // indices, that one digit failed 1,569 of the 2,747 recorded BC7 files
        // while every other mode stayed correct.
        DdsImage image = ReadOk(new DdsFileBuilder
        {
            FourCc = "DX10",
            Dx10 = new DdsFileBuilder.Dx10Extension(),
            BlockBytes = 16,
            Payload = Bc7ModeSixRamp(),
        });

        byte[] greys = new byte[16];
        for (int texel = 0; texel < 16; texel++)
        {
            int x = texel % 4;
            int y = texel / 4;
            greys[texel] = image.Pixels[(y * 16) + (x * 4)];
        }

        Assert.Equal([0, 16, 36, 52, 68, 84, 104, 120, 135, 151, 171, 187, 203, 219, 239, 255], greys);
    }

    [Fact]
    public void Bc7_a_reserved_mode_decodes_to_opaque_black()
    {
        // Eight zero bits name no mode. The format calls it reserved and leaves
        // the result undefined, which is precisely where implementations drift:
        // the reference yields opaque black, not transparent black.
        DdsImage image = ReadOk(new DdsFileBuilder
        {
            FourCc = "DX10",
            Dx10 = new DdsFileBuilder.Dx10Extension(),
            BlockBytes = 16,
            Payload = new byte[16],
        });

        for (int texel = 0; texel < 16; texel++)
        {
            Assert.Equal([0, 0, 0, 255], Texel(image.Pixels.ToArray(), texel));
        }
    }

    /// <summary>
    /// A mode-6 block with black and white endpoints and indices 0 to 15.
    /// </summary>
    private static byte[] Bc7ModeSixRamp()
    {
        byte[] block = new byte[16];
        int position = 0;

        void Write(int value, int width)
        {
            for (int i = 0; i < width; i++)
            {
                if (((value >> i) & 1) != 0)
                {
                    block[position >> 3] |= (byte)(1 << (position & 7));
                }

                position++;
            }
        }

        // Mode 6: six zero bits then a one.
        Write(1 << 6, 7);

        // R0 R1 G0 G1 B0 B1 A0 A1, seven bits each. Zero and 127, which with
        // the P-bits below become 0 and 255 at eight-bit precision.
        for (int channel = 0; channel < 4; channel++)
        {
            Write(0, 7);
            Write(127, 7);
        }

        Write(0, 1);
        Write(1, 1);

        // The anchor is texel zero and stores one bit fewer.
        Write(0, 3);
        for (int texel = 1; texel < 16; texel++)
        {
            Write(texel, 4);
        }

        Assert.Equal(128, position);
        return block;
    }

    /// <summary>Alpha of the first eight texels, whose indices are 0 to 7.</summary>
    private static byte[] AlphaRun(byte[] pixels)
    {
        byte[] alphas = new byte[8];
        for (int texel = 0; texel < 8; texel++)
        {
            alphas[texel] = pixels[(texel * 4) + 3];
        }

        return alphas;
    }

    private static byte[] DecodeBc3Block(
        byte alpha0,
        byte alpha1,
        ushort colour0 = 0xFFFF,
        ushort colour1 = 0x0000)
    {
        byte[] payload = new byte[16];
        payload[0] = alpha0;
        payload[1] = alpha1;

        // Sixteen three-bit indices cycling 0 to 7, least significant first.
        ulong indices = 0;
        for (int texel = 0; texel < 16; texel++)
        {
            indices |= (ulong)(texel % 8) << (3 * texel);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), (uint)indices);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6), (ushort)(indices >> 32));
        WriteBlock(payload.AsSpan(8), colour0, colour1, 0b11100100);

        return ReadOk(new DdsFileBuilder { FourCc = "DXT5", Payload = payload }).Pixels.ToArray();
    }

    private static byte[] DecodeOneBlock(ushort colour0, ushort colour1, uint indices)
    {
        byte[] payload = new byte[8];
        WriteBlock(payload, colour0, colour1, indices);
        return ReadOk(new DdsFileBuilder { Payload = payload }).Pixels.ToArray();
    }

    private static void WriteBlock(Span<byte> block, ushort colour0, ushort colour1, uint indices)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(block, colour0);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], colour1);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
    }

    private static byte[] Texel(byte[] pixels, int index) => pixels[(index * 4)..((index * 4) + 4)];

    private static DdsImage ReadOk(DdsFileBuilder builder)
    {
        Result<DdsImage> result = DdsReader.Read(builder.Build());
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static Refusal ReadRefused(DdsFileBuilder builder)
    {
        Result<DdsImage> result = DdsReader.Read(builder.Build());
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }

    private static DdsHeader ReadHeaderOk(DdsFileBuilder builder)
    {
        Result<DdsHeader> result = DdsReader.ReadHeader(builder.Build());
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static Refusal ReadHeaderRefused(DdsFileBuilder builder)
    {
        Result<DdsHeader> result = DdsReader.ReadHeader(builder.Build());
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }
}
