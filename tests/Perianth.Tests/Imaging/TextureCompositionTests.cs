using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Imaging;

public sealed class TextureCompositionTests
{
    [Fact]
    public void A_constant_alpha_is_installed_and_ignores_the_alpha_image_size()
    {
        RgbaImage diffuse = Filled(4, 4, 10, 20, 30, 255);

        // A different-sized alpha source is fine when the alpha is constant.
        RgbaImage alpha = Filled(2, 2, 0, 0, 0, 128);

        ComposedTexture result = Compose(diffuse, alpha);

        Assert.False(result.Clamp);
        Assert.Null(result.Remap);
        RgbaImage composed = Assert.IsType<RgbaImage>(result.Image);
        Assert.Equal(4, composed.Width);

        // RGB is the diffuse's; alpha is the constant.
        Assert.Equal([10, 20, 30, 128], composed.Pixels[..4].ToArray());
    }

    [Fact]
    public void A_varying_alpha_replaces_the_diffuse_alpha_channel()
    {
        RgbaImage diffuse = Filled(3, 3, 200, 100, 50, 255);

        // A uniform border with a distinct centre: the band varies, yet the
        // opposing edges agree so the composition is allowed. The centre is
        // texel (1,1), byte offset (1*3 + 1) * 4 + 3.
        byte[] alphaPixels = new byte[3 * 3 * 4];
        for (int i = 3; i < alphaPixels.Length; i += 4)
        {
            alphaPixels[i] = 10;
        }

        int centreAlpha = (((1 * 3) + 1) * 4) + 3;
        alphaPixels[centreAlpha] = 200;

        ComposedTexture result = Compose(diffuse, new RgbaImage(3, 3, alphaPixels));

        Assert.False(result.Clamp);
        Assert.Null(result.Remap);
        RgbaImage composed = Assert.IsType<RgbaImage>(result.Image);

        // Colour is the diffuse's throughout; alpha is the source band.
        Assert.Equal([200, 100, 50, 10], composed.Pixels[..4].ToArray());
        Assert.Equal(200, composed.Pixels[centreAlpha]);
    }

    [Fact]
    public void A_varying_alpha_of_a_different_size_is_reconciled_by_resampling()
    {
        // A uniform-border 4x4 alpha with a distinct centre, agreeing edges, so
        // it composes; the 8x8 diffuse forces a resample to the per-axis max.
        RgbaImage diffuse = Filled(8, 8, 30, 40, 50, 255);

        byte[] alphaPixels = new byte[4 * 4 * 4];
        for (int i = 3; i < alphaPixels.Length; i += 4)
        {
            alphaPixels[i] = 20;
        }

        alphaPixels[(((1 * 4) + 1) * 4) + 3] = 200;

        ComposedTexture result = Compose(diffuse, new RgbaImage(4, 4, alphaPixels));

        Assert.False(result.Clamp);
        Assert.Null(result.Remap);
        RgbaImage composed = Assert.IsType<RgbaImage>(result.Image);

        // Reconciled to the per-axis maximum, and the colour survives.
        Assert.Equal(8, composed.Width);
        Assert.Equal(8, composed.Height);
        Assert.Equal([30, 40, 50], composed.Pixels[..3].ToArray());
    }

    [Fact]
    public void A_varying_alpha_whose_opposing_edges_disagree_is_baked_not_refused()
    {
        RgbaImage diffuse = Filled(2, 2, 0, 0, 0, 255);

        // Left column alpha 0, right column alpha 255: the L/R edges differ, so a
        // plain composite would sample the alpha wrongly at the wrap. The bake
        // resolves it into one image and rewrites the coordinates.
        byte[] pixels =
        [
            0, 0, 0, 0,   0, 0, 0, 255,
            0, 0, 0, 0,   0, 0, 0, 255,
        ];

        ComposedTexture result = Compose(diffuse, new RgbaImage(2, 2, pixels));

        Assert.True(result.HasImage);
        Assert.NotNull(result.Remap);
        Assert.False(result.Clamp);
    }

    [Fact]
    public void A_non_identity_repeat_with_varying_alpha_always_bakes()
    {
        RgbaImage diffuse = Filled(4, 4, 10, 20, 30, 255);

        // Varying but edge-agreeing alpha: without the repeat it would compose
        // plainly. The repeat splits the two channels' coordinates, so it bakes.
        byte[] alphaPixels = new byte[4 * 4 * 4];
        for (int i = 3; i < alphaPixels.Length; i += 4)
        {
            alphaPixels[i] = 30;
        }

        alphaPixels[(((1 * 4) + 1) * 4) + 3] = 200;

        ComposedTexture result = TextureComposition.Compose(
            diffuse, new RgbaImage(4, 4, alphaPixels), uv0InRange: true, 2.0, 2.0, Uv0Extent.Unit).Value;

        Assert.True(result.HasImage);
        Assert.NotNull(result.Remap);
    }

    [Fact]
    public void A_non_positive_repeat_with_varying_alpha_refuses()
    {
        RgbaImage diffuse = Filled(4, 4, 10, 20, 30, 255);

        byte[] alphaPixels = new byte[4 * 4 * 4];
        alphaPixels[(((1 * 4) + 1) * 4) + 3] = 200;

        Result<ComposedTexture> result = TextureComposition.Compose(
            diffuse, new RgbaImage(4, 4, alphaPixels), uv0InRange: true, 0.0, 1.0, Uv0Extent.Unit);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public void Opposite_edges_agree_only_when_both_axes_match()
    {
        // Uniform alpha: every edge equal.
        Assert.True(TextureComposition.OppositeEdgesAgree(Filled(3, 3, 0, 0, 0, 40)));

        // Top row 0, bottom row 90: the T/B edges differ.
        byte[] pixels = new byte[3 * 3 * 4];
        for (int x = 0; x < 3; x++)
        {
            pixels[((2 * 3) + x) * 4 + 3] = 90;
        }

        Assert.False(TextureComposition.OppositeEdgesAgree(new RgbaImage(3, 3, pixels)));
    }

    private static ComposedTexture Compose(RgbaImage diffuse, RgbaImage transparent) =>
        TextureComposition.Compose(diffuse, transparent, uv0InRange: true, 1.0, 1.0, Uv0Extent.Unit).Value;

    private static RgbaImage Filled(int width, int height, byte r, byte g, byte b, byte a)
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
}
