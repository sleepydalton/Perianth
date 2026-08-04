using System;
using Perianth.Core.Imaging;
using Xunit;

namespace Perianth.Tests.Imaging;

public sealed class TextureResampleTests
{
    [Fact]
    public void A_matching_size_returns_the_same_instance()
    {
        RgbaImage image = Gradient(6, 4);
        Assert.Same(image, TextureResample.ToSize(image, 6, 4));
    }

    [Fact]
    public void A_uniform_image_stays_uniform_at_any_size()
    {
        // Bilinear of a constant field is that constant everywhere, so this
        // catches a weighting that does not sum to one.
        RgbaImage image = Solid(3, 3, 40, 80, 120, 200);
        RgbaImage bigger = TextureResample.ToSize(image, 7, 5);

        Assert.Equal(7, bigger.Width);
        Assert.Equal(5, bigger.Height);
        for (int i = 0; i < bigger.Pixels.Length; i += 4)
        {
            Assert.Equal([40, 80, 120, 200], bigger.Pixels.Slice(i, 4).ToArray());
        }
    }

    [Fact]
    public void The_corners_hold_the_source_corners()
    {
        // The pixel-centre mapping clamps at the edges, so an enlarged image's
        // corner samples the source corner exactly.
        byte[] pixels =
        [
            10, 0, 0, 255,   20, 0, 0, 255,
            30, 0, 0, 255,   40, 0, 0, 255,
        ];

        RgbaImage bigger = TextureResample.ToSize(new RgbaImage(2, 2, pixels), 4, 4);

        Assert.Equal(10, bigger.Pixels[0]);
        int topRight = (3 * 4);
        Assert.Equal(20, bigger.Pixels[topRight]);
        int bottomLeft = (3 * bigger.Stride);
        Assert.Equal(30, bigger.Pixels[bottomLeft]);
    }

    [Fact]
    public void The_result_is_deterministic()
    {
        RgbaImage image = Gradient(5, 3);
        Assert.Equal(
            TextureResample.ToSize(image, 11, 7).Pixels.ToArray(),
            TextureResample.ToSize(image, 11, 7).Pixels.ToArray());
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
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 3);
        }

        return new RgbaImage(width, height, pixels);
    }
}
