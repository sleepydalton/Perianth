using System;
using System.Collections.Generic;
using System.Linq;
using Perianth.Core.Imaging;
using Xunit;

namespace Perianth.Tests.Imaging;

/// <summary>
/// Checks the mip chain's shape and its exactness.
/// </summary>
/// <remarks>
/// There is no external oracle for these: nothing records what the game's own
/// mip generator produced, and matching it is not the goal — the levels only
/// have to be a reasonable reduction the engine can sample. So the tests assert
/// properties that would be false if the filter were wrong, rather than
/// comparing against recorded bytes.
/// </remarks>
public sealed class MipChainTests
{
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

    private static RgbaImage Ramp(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 13);
        }

        return new RgbaImage(width, height, pixels);
    }

    [Fact]
    public void The_chain_halves_to_one_by_one()
    {
        IReadOnlyList<RgbaImage> levels = MipChain.Build(Ramp(8, 8));

        Assert.Equal(
            [(8, 8), (4, 4), (2, 2), (1, 1)],
            levels.Select(level => (level.Width, level.Height)));
    }

    [Fact]
    public void A_non_square_chain_carries_the_longer_side_down_alone()
    {
        // 8x2 -> 4x1 -> 2x1 -> 1x1. The short side reaches 1 first and stays
        // there while the long side keeps halving, which is what the count
        // measured over the archives depends on.
        IReadOnlyList<RgbaImage> levels = MipChain.Build(Ramp(8, 2));

        Assert.Equal(
            [(8, 2), (4, 1), (2, 1), (1, 1)],
            levels.Select(level => (level.Width, level.Height)));
    }

    [Fact]
    public void An_odd_dimension_rounds_down()
    {
        // 7 -> 3 -> 1, matching the shipped chain lengths.
        IReadOnlyList<RgbaImage> levels = MipChain.Build(Ramp(7, 7));

        Assert.Equal([(7, 7), (3, 3), (1, 1)], levels.Select(level => (level.Width, level.Height)));
    }

    [Fact]
    public void A_one_by_one_image_is_its_own_whole_chain()
    {
        Assert.Single(MipChain.Build(Ramp(1, 1)));
    }

    [Fact]
    public void The_level_count_matches_what_the_writer_will_accept()
    {
        // The two must agree or a full-chain file can never be written. The
        // writer checks halving independently, so this is the two halves
        // meeting rather than one asserting the other.
        foreach ((int width, int height) in new[] { (472, 500), (256, 256), (7, 3), (1, 64) })
        {
            IReadOnlyList<RgbaImage> levels = MipChain.Build(Ramp(width, height));

            int expected = 1;
            int w = width, h = height;
            while (w > 1 || h > 1)
            {
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
                expected++;
            }

            Assert.Equal(expected, levels.Count);
        }
    }

    [Fact]
    public void A_uniform_image_stays_exactly_uniform_all_the_way_down()
    {
        // The exactness check a box filter passes and a sloppy one does not:
        // averaging four equal values must return that value, at every level
        // and in every channel, with no rounding drift.
        IReadOnlyList<RgbaImage> levels = MipChain.Build(Solid(16, 16, 200, 100, 50, 25));

        foreach (RgbaImage level in levels)
        {
            byte[] pixels = level.Pixels.ToArray();
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.Equal([200, 100, 50, 25], pixels[i..(i + 4)]);
            }
        }
    }

    [Fact]
    public void A_level_is_the_average_of_the_four_texels_it_replaces()
    {
        // Two texels of 0 and two of 255 average to 128, not 127: rounding is
        // half away from zero, so repeated halving of a flat image cannot drift
        // downward.
        byte[] pixels =
        [
            0, 0, 0, 0,        255, 255, 255, 255,
            255, 255, 255, 255, 0, 0, 0, 0,
        ];

        RgbaImage level = MipChain.Build(new RgbaImage(2, 2, pixels))[1];

        Assert.Equal([128, 128, 128, 128], level.Pixels.ToArray());
    }

    [Fact]
    public void Each_channel_is_averaged_independently()
    {
        // A channel confusion survives a greyscale fixture and does not survive
        // this one.
        byte[] pixels =
        [
            0, 40, 80, 120,   0, 40, 80, 120,
            0, 40, 80, 120,   4, 44, 84, 124,
        ];

        RgbaImage level = MipChain.Build(new RgbaImage(2, 2, pixels))[1];

        Assert.Equal([1, 41, 81, 121], level.Pixels.ToArray());
    }

    [Fact]
    public void Building_the_same_image_twice_gives_the_same_levels()
    {
        RgbaImage image = Ramp(9, 5);

        IReadOnlyList<RgbaImage> first = MipChain.Build(image);
        IReadOnlyList<RgbaImage> second = MipChain.Build(image);

        Assert.Equal(
            first.Select(level => level.Pixels.ToArray()),
            second.Select(level => level.Pixels.ToArray()));
    }

    [Fact]
    public void The_source_image_is_returned_untouched_as_level_zero()
    {
        RgbaImage image = Ramp(4, 4);

        Assert.Same(image, MipChain.Build(image)[0]);
    }
}
