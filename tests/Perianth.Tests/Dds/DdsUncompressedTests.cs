using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Dds;

/// <summary>
/// Checks reading an uncompressed 32bpp DDS, and the boundary around it.
/// </summary>
/// <remarks>
/// The conformance oracle holds exactly one uncompressed file, and it is in
/// RGBA order — so the BGRA order, which is the one the shipped engine textures
/// and this tool's own writer use, has no oracle coverage at all. These
/// synthetic files are what stands behind it.
/// </remarks>
public sealed class DdsUncompressedTests
{
    private const uint AlphaPixels = 0x1;

    private static DdsFileBuilder Uncompressed(byte[] payload) => new()
    {
        Width = 2,
        Height = 1,
        PixelFlags = AlphaPixels,
        BitCount = 32,
        GreenMask = 0x0000FF00,
        AlphaMask = 0xFF000000,
        Payload = payload,
    };

    private static DdsFileBuilder Bgra(byte[] payload)
    {
        DdsFileBuilder builder = Uncompressed(payload);
        builder.RedMask = 0x00FF0000;
        builder.BlueMask = 0x000000FF;
        return builder;
    }

    private static DdsFileBuilder Rgba(byte[] payload)
    {
        DdsFileBuilder builder = Uncompressed(payload);
        builder.RedMask = 0x000000FF;
        builder.BlueMask = 0x00FF0000;
        return builder;
    }

    [Fact]
    public void A_bgra_file_is_reordered_to_rgba()
    {
        // Two texels: one opaque red, one half-transparent blue, written in the
        // byte order the shipped engine textures use.
        byte[] payload = [0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x80];

        Result<DdsImage> read = DdsReader.Read(Bgra(payload).Build());

        Assert.False(read.IsRefused, read.IsRefused ? read.Refusal.Message : null);
        Assert.Equal(
            [0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x80],
            read.Value.Pixels.ToArray());
    }

    [Fact]
    public void An_rgba_file_is_copied_unchanged()
    {
        byte[] payload = [0xFF, 0x00, 0x00, 0xFF, 0x00, 0x00, 0xFF, 0x80];

        Result<DdsImage> read = DdsReader.Read(Rgba(payload).Build());

        Assert.Equal(payload, read.Value.Pixels.ToArray());
    }

    [Fact]
    public void The_format_is_reported_as_uncompressed()
    {
        Result<DdsHeader> header = DdsReader.ReadHeader(Bgra(new byte[8]).Build());

        Assert.Equal(DdsFormat.Uncompressed32, header.Value.Format);
    }

    [Fact]
    public void Dimensions_need_not_be_multiples_of_four()
    {
        // The block-alignment rule belongs to block-compressed formats. Asking
        // it of every texture would refuse most of what this tool can write:
        // one measured shipped texture is 472x500, and an author's own PNG is
        // whatever size they drew.
        DdsFileBuilder builder = Bgra(new byte[3 * 5 * 4]);
        builder.Width = 3;
        builder.Height = 5;

        Result<DdsImage> read = DdsReader.Read(builder.Build());

        Assert.False(read.IsRefused, read.IsRefused ? read.Refusal.Message : null);
        Assert.Equal(3, read.Value.Width);
    }

    [Fact]
    public void A_truncated_payload_is_malformed()
    {
        DdsFileBuilder builder = Bgra(new byte[4]);
        builder.Width = 4;
        builder.Height = 4;

        Result<DdsImage> read = DdsReader.Read(builder.Build());

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Malformed, read.Refusal.Kind);
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(16u)]
    [InlineData(24u)]
    public void Narrower_depths_still_refuse(uint bitCount)
    {
        // One file of each exists in the archives, all engine textures, and
        // nothing reads them. The boundary is deliberate; this is what holds it.
        DdsFileBuilder builder = Bgra(new byte[8]);
        builder.BitCount = bitCount;

        Result<DdsImage> read = DdsReader.Read(builder.Build());

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
        Assert.Contains(bitCount.ToString(System.Globalization.CultureInfo.InvariantCulture), read.Refusal.Message);
    }

    [Fact]
    public void A_32bpp_file_with_no_alpha_channel_refuses()
    {
        DdsFileBuilder builder = Bgra(new byte[8]);
        builder.PixelFlags = 0;

        Result<DdsImage> read = DdsReader.Read(builder.Build());

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
    }

    [Fact]
    public void An_unfamiliar_byte_order_refuses_rather_than_guessing()
    {
        // ARGB in file order. Coherent, nothing in the archives uses it, and
        // decoding it on the chance it was meant would silently swap channels.
        DdsFileBuilder builder = Uncompressed(new byte[8]);
        builder.RedMask = 0x0000FF00;
        builder.GreenMask = 0x00FF0000;
        builder.BlueMask = 0xFF000000;
        builder.AlphaMask = 0x000000FF;

        Result<DdsImage> read = DdsReader.Read(builder.Build());

        Assert.True(read.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, read.Refusal.Kind);
        Assert.Contains("byte order", read.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_compressed_file_is_unaffected()
    {
        // The masks are read only when the FourCC flag is absent; a DXT1 file
        // carrying stray mask bytes must still be read as DXT1.
        DdsFileBuilder builder = new() { RedMask = 0x000000FF, AlphaMask = 0xFF000000 };

        Result<DdsHeader> header = DdsReader.ReadHeader(builder.Build());

        Assert.Equal(DdsFormat.Bc1, header.Value.Format);
    }
}
