using System;
using System.Collections.Generic;
using System.Text.Json;
using Perianth.Formats;
using Xunit;

namespace Perianth.Tests;

/// <summary>
/// Checks that the transcribed vectors are internally coherent.
/// </summary>
/// <remarks>
/// These are not consumers of the vectors: almost nothing that reads them exists
/// yet. They exist because the risk this fixture carries today is a transcription
/// error, not drift. An oracle nobody checks can be wrong from the moment it is
/// written, and every one of these assertions is a relationship the specification
/// states or implies, so a slip in copying breaks one of them.
/// </remarks>
public sealed class SpecVectorsTests
{
    private static readonly string[] ExpectedGroups =
    [
        "dtra8",
        "dsca8",
        "drot3",
        "bvm_compact",
        "uv0",
        "packed_z_cross_word",
        "compressed_anim_channel",
        "hierarchy_composition",
        "visibility_sentinels",
        "bilinear_resize",
        "one_texel_gutter",
        "gain_offset",
    ];

    [Fact]
    public void Every_group_the_specification_names_is_present_and_no_others_are()
    {
        List<string> actual = [];
        foreach (JsonProperty group in SpecVectors.Groups.EnumerateObject())
        {
            actual.Add(group.Name);
        }

        Assert.Equal(ExpectedGroups.Length, actual.Count);
        foreach (string expected in ExpectedGroups)
        {
            Assert.Contains(expected, actual, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void Every_DROT3_quaternion_has_the_smallest_three_shape()
    {
        JsonElement group = SpecVectors.Group("drot3");
        double stored = group.GetProperty("stored_component").GetDouble();
        double companion = group.GetProperty("companion_component").GetDouble();

        // The companion is whatever makes the quaternion a unit vector. If either
        // number were mistyped this identity would be the first thing to break.
        Assert.Equal(Math.Sqrt(1 - (stored * stored)), companion, SpecVectors.Tolerance);

        foreach (JsonElement rotation in group.GetProperty("cases").EnumerateArray())
        {
            double[] q = SpecVectors.Doubles(rotation, "quaternion");
            Assert.Equal(4, q.Length);

            double norm = Math.Sqrt((q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]));
            Assert.Equal(1.0, norm, SpecVectors.Tolerance);

            int atStored = 0, atCompanion = 0, atZero = 0;
            foreach (double component in q)
            {
                if (Math.Abs(Math.Abs(component) - stored) <= SpecVectors.Tolerance)
                {
                    atStored++;
                }
                else if (Math.Abs(Math.Abs(component) - companion) <= SpecVectors.Tolerance)
                {
                    atCompanion++;
                }
                else if (component == 0)
                {
                    atZero++;
                }
            }

            Assert.Equal(1, atStored);
            Assert.Equal(1, atCompanion);
            Assert.Equal(2, atZero);
        }
    }

    [Fact]
    public void Every_DROT3_case_is_three_bytes_carrying_its_own_code_and_high_word()
    {
        JsonElement group = SpecVectors.Group("drot3");
        int expectedHigh = group.GetProperty("high_word").GetInt32();
        List<int> codes = [];

        foreach (JsonElement rotation in group.GetProperty("cases").EnumerateArray())
        {
            byte[] bytes = SpecVectors.Hex(rotation, "bytes");
            Assert.Equal(3, bytes.Length);

            int code = rotation.GetProperty("code").GetInt32();
            codes.Add(code);

            // The specification's own words for these bytes: the code sits in the
            // top three bits, the low five are zero, and the first two bytes are
            // the signed high word every case shares.
            Assert.Equal(code, bytes[2] >> 5);
            Assert.Equal(0, bytes[2] & 0x1F);
            Assert.Equal(expectedHigh, bytes[0] | (bytes[1] << 8));
        }

        Assert.Equal([1, 2, 3, 4, 5, 6], codes);
    }

    [Fact]
    public void Code_seven_is_recorded_as_refusing_and_has_no_golden_result()
    {
        JsonElement group = SpecVectors.Group("drot3");

        List<int> refusing = [];
        foreach (JsonElement code in group.GetProperty("refusing_codes").EnumerateArray())
        {
            refusing.Add(code.GetInt32());
        }

        Assert.Equal([7], refusing);

        foreach (JsonElement rotation in group.GetProperty("cases").EnumerateArray())
        {
            Assert.DoesNotContain(rotation.GetProperty("code").GetInt32(), refusing);
        }
    }

    [Fact]
    public void Every_BVM_case_agrees_with_the_shared_follower_table()
    {
        // Ties the fixture directly to Sentinels: the selector in the first byte
        // picks a follower count, that count fixes the encoded width, and the
        // width fixes how many bytes the case must have. Three independent
        // statements in the fixture that all have to line up.
        foreach (JsonElement compact in SpecVectors.Group("bvm_compact").GetProperty("cases").EnumerateArray())
        {
            byte[] bytes = SpecVectors.Hex(compact, "bytes");
            int width = compact.GetProperty("encoded_width").GetInt32();

            int selector = bytes[0] >> 6;
            int extra = Sentinels.BvmCompactExtraByteCounts[selector];

            Assert.Equal(1 + extra, bytes.Length);
            Assert.Equal(Sentinels.BvmCompactPayloadBits + (8 * extra), width);
        }
    }

    [Fact]
    public void Every_BVM_signed_value_is_the_twos_complement_of_its_unsigned_value()
    {
        foreach (JsonElement compact in SpecVectors.Group("bvm_compact").GetProperty("cases").EnumerateArray())
        {
            ulong unsigned = compact.GetProperty("unsigned").GetUInt64();
            long signed = compact.GetProperty("signed").GetInt64();
            int width = compact.GetProperty("encoded_width").GetInt32();

            bool negative = (unsigned & (1UL << (width - 1))) != 0;
            long expected = negative ? (long)unsigned - (1L << width) : (long)unsigned;

            Assert.Equal(expected, signed);
        }
    }

    [Fact]
    public void Every_hex_field_is_as_wide_as_its_format_declares()
    {
        Assert.Equal(8, SpecVectors.Hex(Single("dtra8"), "bytes").Length);
        Assert.Equal(8, SpecVectors.Hex(Single("dsca8"), "bytes").Length);

        foreach (JsonElement uv in SpecVectors.Group("uv0").GetProperty("cases").EnumerateArray())
        {
            Assert.Equal(4, SpecVectors.Hex(uv, "packed").Length);
        }

        foreach (JsonElement word in SpecVectors.Group("packed_z_cross_word").GetProperty("words").EnumerateArray())
        {
            Assert.Equal(4, Convert.FromHexString(word.GetString()!).Length);
        }
    }

    [Fact]
    public void The_packed_Z_reads_stay_inside_the_words_they_are_given()
    {
        JsonElement group = SpecVectors.Group("packed_z_cross_word");
        int bits = group.GetProperty("words").GetArrayLength() * 32;

        bool straddles = false;
        foreach (JsonElement read in group.GetProperty("cases").EnumerateArray())
        {
            int offset = read.GetProperty("bit_offset").GetInt32();
            int count = read.GetProperty("bit_count").GetInt32();

            Assert.InRange(offset + count, 0, bits);
            straddles |= offset / 32 != (offset + count - 1) / 32;
        }

        // The whole point of the group is the crossing; a case set that never
        // crossed a word boundary would be testing nothing.
        Assert.True(straddles);
    }

    [Fact]
    public void Every_pixel_grid_is_rectangular_and_matches_its_declared_size()
    {
        JsonElement group = SpecVectors.Group("bilinear_resize");
        JsonElement source = group.GetProperty("source");
        AssertGrid(SpecVectors.Grid(source, "rows"),
            source.GetProperty("width").GetInt32(),
            source.GetProperty("height").GetInt32());

        foreach (JsonElement resized in group.GetProperty("cases").EnumerateArray())
        {
            AssertGrid(SpecVectors.Grid(resized, "rows"),
                resized.GetProperty("width").GetInt32(),
                resized.GetProperty("height").GetInt32());
        }
    }

    [Fact]
    public void The_resized_corners_keep_the_source_corners_exactly()
    {
        // Bilinear resampling reproduces the corner texels of the source, so a
        // transcribed grid whose corners drifted would be wrong whatever else
        // was right.
        JsonElement group = SpecVectors.Group("bilinear_resize");
        int[][] source = SpecVectors.Grid(group.GetProperty("source"), "rows");

        foreach (JsonElement resized in group.GetProperty("cases").EnumerateArray())
        {
            int[][] grid = SpecVectors.Grid(resized, "rows");
            Assert.Equal(source[0][0], grid[0][0]);
            Assert.Equal(source[0][^1], grid[0][^1]);
            Assert.Equal(source[^1][0], grid[^1][0]);
            Assert.Equal(source[^1][^1], grid[^1][^1]);
        }
    }

    [Fact]
    public void The_gutter_layouts_use_only_the_interior_they_come_from()
    {
        JsonElement group = SpecVectors.Group("one_texel_gutter");

        string[][] interior = SpecVectors.LabelGrid(group, "interior_rgb");
        string[][] colour = SpecVectors.LabelGrid(group, "result_rgb");
        int[][] alphaIn = SpecVectors.Grid(group, "interior_alpha");
        int[][] alpha = SpecVectors.Grid(group, "result_alpha");

        AssertLabelGrid(colour, 4, 4);
        AssertGrid(alpha, 4, 4);

        List<string> labels = [];
        foreach (string[] row in interior)
        {
            labels.AddRange(row);
        }

        foreach (string[] row in colour)
        {
            foreach (string label in row)
            {
                Assert.Contains(label, labels, StringComparer.Ordinal);
            }
        }

        List<int> values = [];
        foreach (int[] row in alphaIn)
        {
            values.AddRange(row);
        }

        foreach (int[] row in alpha)
        {
            foreach (int value in row)
            {
                Assert.Contains(value, values);
            }
        }

        // The two layouts must differ. They are the reason this vector exists:
        // colour repeats across the gutter while alpha clamps into it, and a
        // transcription that made them agree would erase the distinction.
        Assert.NotEqual(Flatten(colour), Flatten(Relabel(alpha, interior, alphaIn)));
    }

    [Fact]
    public void The_gain_and_offset_case_clips_exactly_where_it_says_it_does()
    {
        foreach (JsonElement colour in SpecVectors.Group("gain_offset").GetProperty("cases").EnumerateArray())
        {
            double[] before = SpecVectors.Doubles(colour, "before_rounding");
            double[] output = SpecVectors.Doubles(colour, "output_rgba");
            double[] input = SpecVectors.Doubles(colour, "input_rgba");

            Assert.Equal(3, before.Length);
            Assert.Equal(4, output.Length);

            bool clips = false;
            for (int channel = 0; channel < 3; channel++)
            {
                double raw = before[channel];
                clips |= raw is < 0 or > 255;

                // Where a channel was clipped the output is the bound it crossed;
                // where it was not, the output is the rounded value.
                if (raw < 0)
                {
                    Assert.Equal(0, output[channel]);
                }
                else if (raw > 255)
                {
                    Assert.Equal(255, output[channel]);
                }
                else
                {
                    Assert.Equal(Math.Round(raw, MidpointRounding.ToEven), output[channel]);
                }
            }

            Assert.Equal(clips, colour.GetProperty("reports_clipping").GetBoolean());

            // Alpha never participates.
            Assert.Equal(input[3], output[3]);
        }
    }

    [Fact]
    public void The_gain_and_offset_ties_round_half_to_even()
    {
        foreach (JsonElement tie in SpecVectors.Group("gain_offset").GetProperty("tie_cases").EnumerateArray())
        {
            double raw = tie.GetProperty("input").GetDouble() * tie.GetProperty("gain").GetDouble();

            // Each tie case has to actually be a tie, or it proves nothing.
            Assert.Equal(0.5, Math.Abs(raw - Math.Truncate(raw)));
            Assert.Equal(Math.Round(raw, MidpointRounding.ToEven), tie.GetProperty("output").GetDouble());
        }
    }

    [Fact]
    public void The_composed_hierarchy_scales_componentwise_and_keeps_the_parent_rotation()
    {
        JsonElement group = SpecVectors.Group("hierarchy_composition");
        double[] parentScale = SpecVectors.Doubles(group.GetProperty("parent"), "scale");
        double[] childScale = SpecVectors.Doubles(group.GetProperty("child_local"), "scale");
        double[] worldScale = SpecVectors.Doubles(group.GetProperty("child_world"), "scale");

        for (int axis = 0; axis < 3; axis++)
        {
            Assert.Equal(parentScale[axis] * childScale[axis], worldScale[axis], SpecVectors.Tolerance);
        }

        // The child's local rotation is identity, so the world rotation is the
        // parent's unchanged.
        Assert.Equal([0, 0, 0, 1], SpecVectors.Doubles(group.GetProperty("child_local"), "rotation"));
        Assert.Equal(
            SpecVectors.Doubles(group.GetProperty("parent"), "rotation"),
            SpecVectors.Doubles(group.GetProperty("child_world"), "rotation"));

        foreach (string node in new[] { "parent", "child_local", "child_world" })
        {
            double[] q = SpecVectors.Doubles(group.GetProperty(node), "rotation");
            double norm = Math.Sqrt((q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]));
            Assert.Equal(1.0, norm, SpecVectors.Tolerance);
        }
    }

    [Fact]
    public void Every_visibility_scenario_uses_only_the_declared_selector_sentinels()
    {
        foreach (JsonElement scenario in SpecVectors.Group("visibility_sentinels").GetProperty("scenarios").EnumerateArray())
        {
            foreach (string side in new[] { "setup", "clip" })
            {
                JsonElement node = scenario.GetProperty(side);
                if (node.ValueKind is JsonValueKind.Null)
                {
                    continue;
                }

                foreach (JsonProperty selector in node.EnumerateObject())
                {
                    ushort value = selector.Value.GetUInt16();
                    Assert.True(
                        value == Sentinels.AnimSelectorHiddenOrIdentity ||
                        value == Sentinels.AnimSelectorActiveOrIdentity);
                }
            }
        }
    }

    [Fact]
    public void The_compressed_channel_keys_cover_every_animated_channel()
    {
        JsonElement group = SpecVectors.Group("compressed_anim_channel");
        int channels = group.GetProperty("animated_channels").GetInt32();

        // CAKS carries one entry per channel per sample boundary; CHAK names the
        // channels that change. Neither may point past the value payload.
        int values = group.GetProperty("values").GetArrayLength();
        foreach (JsonElement key in group.GetProperty("caks").EnumerateArray())
        {
            Assert.InRange(key.GetInt32(), 0, values - 1);
        }

        foreach (JsonElement changed in group.GetProperty("chak").EnumerateArray())
        {
            Assert.InRange(changed.GetInt32(), 0, values);
        }

        List<int> resolved = [];
        foreach (JsonElement resolution in group.GetProperty("resolutions").EnumerateArray())
        {
            int channel = resolution.GetProperty("channel").GetInt32();
            Assert.InRange(channel, 0, channels - 1);
            if (!resolved.Contains(channel))
            {
                resolved.Add(channel);
            }
        }

        Assert.Equal(channels, resolved.Count);
    }

    private static JsonElement Single(string group)
    {
        JsonElement cases = SpecVectors.Group(group).GetProperty("cases");
        Assert.Equal(1, cases.GetArrayLength());
        return cases[0];
    }

    private static void AssertGrid(int[][] grid, int width, int height)
    {
        Assert.Equal(height, grid.Length);
        foreach (int[] row in grid)
        {
            Assert.Equal(width, row.Length);
        }
    }

    private static void AssertLabelGrid(string[][] grid, int width, int height)
    {
        Assert.Equal(height, grid.Length);
        foreach (string[] row in grid)
        {
            Assert.Equal(width, row.Length);
        }
    }

    private static string Flatten(string[][] grid) => string.Join(",", Array.ConvertAll(grid, r => string.Join("", r)));

    private static string[][] Relabel(int[][] alpha, string[][] interior, int[][] interiorAlpha)
    {
        Dictionary<int, string> map = [];
        for (int row = 0; row < interiorAlpha.Length; row++)
        {
            for (int column = 0; column < interiorAlpha[row].Length; column++)
            {
                map[interiorAlpha[row][column]] = interior[row][column];
            }
        }

        return Array.ConvertAll(alpha, r => Array.ConvertAll(r, v => map[v]));
    }
}
