using System.Linq;
using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Imaging;

public sealed class TextureBakeTests
{
    [Fact]
    public void The_one_texel_gutter_repeats_colour_and_clamps_alpha()
    {
        // The specification's gutter vector: a 2x2 interior RGB [A,B;C,D] and
        // alpha [1,2;3,4] must produce a 4x4 whose colour wraps to the opposite
        // tile and whose alpha holds its own edge.
        (byte, byte, byte) a = (10, 11, 12);
        (byte, byte, byte) b = (20, 21, 22);
        (byte, byte, byte) c = (30, 31, 32);
        (byte, byte, byte) d = (40, 41, 42);

        RgbaImage diffuse = FromRgb(2, 2, [a, b, c, d]);
        RgbaImage transparent = FromAlpha(2, 2, [1, 2, 3, 4]);

        RgbaImage baked = TextureBake.Bake(diffuse, transparent, Uv0Extent.Unit, 1.0, 1.0).Value.Image!;

        Assert.Equal(4, baked.Width);
        Assert.Equal(4, baked.Height);

        (byte, byte, byte)[][] expectedRgb =
        [
            [b, a, b, a],
            [b, a, b, a],
            [d, c, d, c],
            [d, c, d, c],
        ];

        byte[][] expectedAlpha =
        [
            [1, 1, 2, 2],
            [1, 1, 2, 2],
            [3, 3, 4, 4],
            [3, 3, 4, 4],
        ];

        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int p = (y * 4 + x) * 4;
                Assert.Equal(expectedRgb[y][x], (baked.Pixels[p], baked.Pixels[p + 1], baked.Pixels[p + 2]));
                Assert.Equal(expectedAlpha[y][x], baked.Pixels[p + 3]);
            }
        }
    }

    [Fact]
    public void An_enlarged_colour_is_replicated_not_interpolated()
    {
        // A finer alpha forces the colour to enlarge. Nearest replication only
        // ever copies a source byte, so a two-value diffuse can never gain a
        // blended third value; bilinear would introduce one.
        RgbaImage diffuse = FromRgb(2, 2,
        [
            (10, 0, 0), (250, 0, 0),
            (10, 0, 0), (250, 0, 0),
        ]);
        RgbaImage transparent = FromAlpha(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i * 15)).ToArray());

        RgbaImage baked = TextureBake.Bake(diffuse, transparent, Uv0Extent.Unit, 1.0, 1.0).Value.Image!;

        for (int i = 0; i < baked.Width * baked.Height; i++)
        {
            byte red = baked.Pixels[i * 4];
            Assert.True(red is 10 or 250, $"colour byte {red} is neither source value, so it was interpolated");
        }
    }

    [Fact]
    public void The_remap_insets_the_coordinates_inside_the_gutter()
    {
        // One tile with a one-texel gutter each side: the unit range maps to the
        // interior, so 0 and 1 land at 1/(w+2) and (w+1)/(w+2), never the edge.
        RgbaImage diffuse = FromRgb(2, 2, [(1, 1, 1), (2, 2, 2), (3, 3, 3), (4, 4, 4)]);
        RgbaImage transparent = FromAlpha(2, 2, [1, 2, 3, 4]);

        Uv0Remap remap = TextureBake.Bake(diffuse, transparent, Uv0Extent.Unit, 1.0, 1.0).Value.Remap!.Value;

        double atZero = (0.0 * remap.ScaleU) + remap.OffsetU;
        double atOne = (1.0 * remap.ScaleU) + remap.OffsetU;
        Assert.Equal(1.0 / 4.0, atZero, 9);
        Assert.Equal(3.0 / 4.0, atOne, 9);
    }

    [Fact]
    public void A_bake_past_the_cap_is_oversized_rather_than_refused()
    {
        RgbaImage diffuse = FromRgb(8192, 8, Enumerable.Repeat<(byte, byte, byte)>((1, 2, 3), 8192 * 8).ToArray());
        RgbaImage transparent = FromAlpha(4, 4, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());

        // Ten thousand tiles across one row: the interior alone exceeds the cap.
        Result<ComposedTexture> result = TextureBake.Bake(
            diffuse, transparent, new Uv0Extent(0.0, 1.0, 0.0, 0.0), 10000.0, 1.0);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Oversized);
        Assert.Equal(10000, result.Value.TilesU);
        Assert.Null(result.Value.Image);
    }

    [Fact]
    public void A_non_positive_repeat_refuses()
    {
        RgbaImage diffuse = FromRgb(2, 2, [(1, 1, 1), (2, 2, 2), (3, 3, 3), (4, 4, 4)]);
        RgbaImage transparent = FromAlpha(2, 2, [1, 2, 3, 4]);

        Assert.True(TextureBake.Bake(diffuse, transparent, Uv0Extent.Unit, -1.0, 1.0).IsRefused);
    }

    [Fact]
    public void A_sub_half_texel_overshoot_with_agreeing_edges_clamps()
    {
        // Uniform colour, so its crossed edges agree; a varying alpha that leaves
        // the unit range in U by under half a texel is served by clamping.
        RgbaImage diffuse = Uniform(4, 4, 60, 70, 80);
        RgbaImage transparent = FromAlpha(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i % 4 == 0 ? 200 : 40)).ToArray());

        ComposedTexture result = TextureComposition.Compose(
            diffuse, transparent, uv0InRange: false, 1.0, 1.0, new Uv0Extent(0.0, 1.05, 0.0, 1.0)).Value;

        Assert.True(result.Clamp);
        Assert.True(result.HasImage);
        Assert.Null(result.Remap);
    }

    [Fact]
    public void A_repeated_span_past_half_a_texel_bakes_rather_than_clamps()
    {
        RgbaImage diffuse = Uniform(4, 4, 60, 70, 80);
        RgbaImage transparent = FromAlpha(4, 4, Enumerable.Range(0, 16).Select(i => (byte)(i % 4 == 0 ? 200 : 40)).ToArray());

        ComposedTexture result = TextureComposition.Compose(
            diffuse, transparent, uv0InRange: false, 1.0, 1.0, new Uv0Extent(0.0, 1.5, 0.0, 1.0)).Value;

        Assert.False(result.Clamp);
        Assert.NotNull(result.Remap);
    }

    private static RgbaImage FromRgb(int width, int height, (byte R, byte G, byte B)[] rgb)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < rgb.Length; i++)
        {
            pixels[i * 4] = rgb[i].R;
            pixels[i * 4 + 1] = rgb[i].G;
            pixels[i * 4 + 2] = rgb[i].B;
            pixels[i * 4 + 3] = 255;
        }

        return new RgbaImage(width, height, pixels);
    }

    private static RgbaImage FromAlpha(int width, int height, byte[] alpha)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            pixels[i * 4 + 3] = alpha[i];
        }

        return new RgbaImage(width, height, pixels);
    }

    private static RgbaImage Uniform(int width, int height, byte r, byte g, byte b)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        return new RgbaImage(width, height, pixels);
    }
}
