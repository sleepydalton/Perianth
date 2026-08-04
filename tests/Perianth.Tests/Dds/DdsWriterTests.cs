using System;
using System.Collections.Generic;
using System.Linq;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Dds;

/// <summary>
/// Checks writing an uncompressed DDS, mostly by reading it back.
/// </summary>
/// <remarks>
/// The reader is the oracle here, and it is a real one: it was validated
/// against the Python reference over the whole corpus before this was written,
/// so "the writer and reader agree" is not two wrongs cancelling. What the
/// round trip proves is exactness — an authored texture must come back byte for
/// byte, because anything less is silent quality loss on someone's own artwork.
/// </remarks>
public sealed class DdsWriterTests
{
    private static byte[] Ramp(int width, int height, int seed = 0)
    {
        byte[] rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i++)
        {
            // Deliberately not a flat fill: a channel swap or a stride error
            // survives a uniform image and does not survive this.
            rgba[i] = (byte)((i * 7) + seed);
        }

        return rgba;
    }

    private static DdsLevel Level(int width, int height, int seed = 0) =>
        new(width, height, Ramp(width, height, seed));

    [Fact]
    public void A_written_texture_reads_back_byte_for_byte()
    {
        DdsLevel level = Level(7, 3);

        Result<byte[]> written = DdsWriter.Write(level);
        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : null);

        Result<DdsImage> read = DdsReader.Read(written.Value);
        Assert.False(read.IsRefused, read.IsRefused ? read.Refusal.Message : null);

        Assert.Equal(7, read.Value.Width);
        Assert.Equal(3, read.Value.Height);
        Assert.Equal(DdsFormat.Uncompressed32, read.Value.Format);
        Assert.Equal(level.Pixels.ToArray(), read.Value.Pixels.ToArray());
    }

    [Fact]
    public void Dimensions_that_are_not_multiples_of_four_round_trip()
    {
        // The whole point of writing uncompressed. An author's own image is
        // whatever size they drew it, and block alignment is a rule this format
        // does not have.
        Result<byte[]> written = DdsWriter.Write(Level(3, 5));

        Assert.Equal(3, DdsReader.Read(written.Value).Value.Width);
    }

    [Fact]
    public void The_bytes_are_written_in_bgra_order()
    {
        // Not merely "it round-trips": that would pass if the writer and reader
        // agreed on a wrong order. This asserts what is actually in the file,
        // which is what the engine reads.
        DdsLevel level = new(1, 1, new byte[] { 0x11, 0x22, 0x33, 0x44 });

        byte[] bytes = DdsWriter.Write(level).Value;

        Assert.Equal([0x33, 0x22, 0x11, 0x44], bytes[128..132]);
    }

    [Fact]
    public void A_single_level_declares_no_mip_chain()
    {
        byte[] bytes = DdsWriter.Write(Level(4, 4)).Value;

        Assert.Equal(1u, BitConverter.ToUInt32(bytes, 28));
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 8) & 0x20000);
        Assert.Equal(0u, BitConverter.ToUInt32(bytes, 108) & 0x400000);
    }

    [Fact]
    public void A_mip_chain_declares_its_count_and_its_caps()
    {
        List<DdsLevel> levels = [Level(4, 4), Level(2, 2), Level(1, 1)];

        byte[] bytes = DdsWriter.Write(levels).Value;

        Assert.Equal(3u, BitConverter.ToUInt32(bytes, 28));
        Assert.Equal(0x20000u, BitConverter.ToUInt32(bytes, 8) & 0x20000);
        Assert.Equal(0x400000u, BitConverter.ToUInt32(bytes, 108) & 0x400000);

        // Level zero still reads, and the extra levels are in the file rather
        // than merely promised by the header.
        Assert.Equal(4, DdsReader.Read(bytes).Value.Width);
        Assert.Equal(128 + (64 + 16 + 4), bytes.Length);
    }

    [Fact]
    public void A_chain_that_does_not_halve_is_refused()
    {
        // A DDS says only how many levels there are; every reader derives the
        // sizes by halving. Writing these dimensions would produce a file whose
        // bytes mean something other than what the caller passed.
        Result<byte[]> written = DdsWriter.Write([Level(8, 8), Level(4, 8)]);

        Assert.True(written.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, written.Refusal.Kind);
        Assert.Contains("halves", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_odd_dimension_halves_downward_as_the_shipped_files_do()
    {
        // 7 -> 3 -> 1. Measured against every texture in the archives: 46,890
        // of 47,321 declare exactly this chain length for their dimensions.
        Result<byte[]> written = DdsWriter.Write([Level(7, 7), Level(3, 3), Level(1, 1)]);

        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : null);
    }

    [Fact]
    public void A_level_whose_pixels_do_not_match_its_size_is_refused()
    {
        Result<byte[]> written = DdsWriter.Write(new DdsLevel(4, 4, new byte[8]));

        Assert.True(written.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, written.Refusal.Kind);
    }

    [Fact]
    public void An_empty_level_list_is_refused()
    {
        Assert.True(DdsWriter.Write([]).IsRefused);
    }

    [Fact]
    public void A_zero_dimension_is_refused()
    {
        Assert.True(DdsWriter.Write(new DdsLevel(0, 4, Array.Empty<byte>())).IsRefused);
    }

    [Fact]
    public void Writing_the_same_levels_twice_gives_the_same_bytes()
    {
        // Determinism is the product. A mod's patch is worthless if rebuilding
        // the texture from the same source produces different bytes.
        List<DdsLevel> levels = [Level(8, 8), Level(4, 4), Level(2, 2), Level(1, 1)];

        Assert.Equal(DdsWriter.Write(levels).Value, DdsWriter.Write(levels).Value);
    }

    [Fact]
    public void Every_level_of_a_chain_survives_the_round_trip()
    {
        // The reader only decodes level zero, so each level is checked by
        // writing it as its own file too. Without this the tail of a chain
        // could be misplaced by a stride and nothing would notice.
        List<DdsLevel> levels = [Level(8, 8, 0), Level(4, 4, 5), Level(2, 2, 9), Level(1, 1, 3)];

        byte[] chain = DdsWriter.Write(levels).Value;
        int at = 128;

        foreach (DdsLevel level in levels)
        {
            byte[] alone = DdsWriter.Write(level).Value;
            Assert.Equal(alone[128..], chain[at..(at + level.Pixels.Length)]);
            Assert.Equal(level.Pixels.ToArray(), DdsReader.Read(alone).Value.Pixels.ToArray());
            at += level.Pixels.Length;
        }

        Assert.Equal(chain.Length, at);
    }
}
