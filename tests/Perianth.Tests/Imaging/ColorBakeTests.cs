using System;
using Perianth.Core.Imaging;
using Xunit;

namespace Perianth.Tests.Imaging;

public sealed class ColorBakeTests
{
    [Fact]
    public void A_pure_gain_scales_each_channel_and_rounds_half_to_even()
    {
        // 100 * 1.005 = 100.5, which rounds to 100 (even), not 101. This is the
        // banker's rounding the reference's round() performs.
        RgbaImage image = Solid(100, 101, 100, 200);
        RgbaImage baked = ColorBake.Apply(image, new ColorAdjustment(new Rgb(1.005, 1, 1), new Rgb(0, 0, 0)));

        Assert.Equal(100, baked.Pixels[0]);   // 100.5 -> 100
        Assert.Equal(101, baked.Pixels[1]);   // unchanged
    }

    [Fact]
    public void An_offset_adds_on_the_zero_to_255_scale()
    {
        // offset 0.5 adds 127.5; 10 + 127.5 = 137.5 -> 138 (even).
        RgbaImage image = Solid(10, 10, 10, 255);
        RgbaImage baked = ColorBake.Apply(image, new ColorAdjustment(new Rgb(1, 1, 1), new Rgb(0.5, 0, 0)));

        Assert.Equal(138, baked.Pixels[0]);
        Assert.Equal(10, baked.Pixels[1]);
    }

    [Fact]
    public void The_result_clamps_rather_than_wrapping()
    {
        RgbaImage image = Solid(200, 0, 0, 255);

        // 200 * 2 = 400 -> 255; a negative offset drives green below zero -> 0.
        RgbaImage baked = ColorBake.Apply(image, new ColorAdjustment(new Rgb(2, 1, 1), new Rgb(0, -0.5, 0)));

        Assert.Equal(255, baked.Pixels[0]);
        Assert.Equal(0, baked.Pixels[1]);
    }

    [Fact]
    public void Alpha_is_never_touched()
    {
        RgbaImage image = Solid(10, 20, 30, 123);
        RgbaImage baked = ColorBake.Apply(image, new ColorAdjustment(new Rgb(2, 2, 2), new Rgb(0.1, 0.1, 0.1)));

        Assert.Equal(123, baked.Pixels[3]);
    }

    [Fact]
    public void Clips_is_true_only_when_an_extreme_leaves_the_range()
    {
        RgbaImage image = Solid(200, 50, 50, 255);

        Assert.True(ColorBake.Clips(image, new ColorAdjustment(new Rgb(2, 1, 1), new Rgb(0, 0, 0))));
        Assert.False(ColorBake.Clips(image, new ColorAdjustment(new Rgb(1, 1, 1), new Rgb(0.1, 0, 0))));
        Assert.True(ColorBake.Clips(image, new ColorAdjustment(new Rgb(1, 1, 1), new Rgb(0, -0.5, 0))));
    }

    [Fact]
    public void The_identity_adjustment_leaves_every_byte_unchanged()
    {
        RgbaImage image = Gradient(8, 8);
        RgbaImage baked = ColorBake.Apply(image, ColorAdjustment.Identity);

        Assert.Equal(image.Pixels.ToArray(), baked.Pixels.ToArray());
        Assert.False(ColorBake.Clips(image, ColorAdjustment.Identity));
    }

    private static RgbaImage Solid(byte r, byte g, byte b, byte a)
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new RgbaImage(4, 4, pixels);
    }

    private static RgbaImage Gradient(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 7);
        }

        return new RgbaImage(width, height, pixels);
    }
}
