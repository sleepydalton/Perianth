using System.Globalization;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Geometry;

public sealed class Uv0ProjectionTests
{
    [Fact]
    public void The_specification_vectors_for_unified_UV0_hold()
    {
        // The second of the fixture's groups to stop being transcribed numbers.
        foreach (JsonElement vector in SpecVectors.Group("uv0").GetProperty("cases").EnumerateArray())
        {
            uint packed = uint.Parse(
                vector.GetProperty("packed").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            Result<Vector2D> result = Uv0Projection.Unified(packed, vector.GetProperty("scale_index").GetInt32());
            Assert.True(result.IsSuccess);

            double[] expected = SpecVectors.Doubles(vector, "uv");
            Assert.Equal(expected[0], result.Value.X, SpecVectors.Tolerance);
            Assert.Equal(expected[1], result.Value.Y, SpecVectors.Tolerance);

            if (vector.TryGetProperty("split", out JsonElement split))
            {
                Assert.Equal(split[0].GetInt32(), (short)(packed & 0xFFFF));
                Assert.Equal(split[1].GetInt32(), (short)(packed >> 16));
            }
        }
    }

    [Fact]
    public void The_widest_negative_value_is_floored_at_minus_one_before_scaling()
    {
        // -32768 over 32767 is slightly below -1, so without the floor the
        // widest storable value would overshoot its own scale. This is the
        // second specification vector's whole point, stated on its own.
        Result<Vector2D> result = Uv0Projection.Unified(0x8000_8000, 1);

        Assert.Equal(-7.999755859375, result.Value.X, 1e-12);
        Assert.Equal(-7.999755859375, result.Value.Y, 1e-12);
    }

    [Fact]
    public void The_low_half_is_U_and_the_high_half_is_V()
    {
        Result<Vector2D> result = Uv0Projection.Unified(0x0000_4000, 2);

        Assert.Equal(0.500015259254738, result.Value.X, 1e-12);
        Assert.Equal(0, result.Value.Y);
    }

    [Fact]
    public void A_scale_index_with_no_defined_scale_is_unsupported()
    {
        // The format can encode index 3 and the table has three entries. The
        // bytes are coherent, so this is not a malformed file.
        Result<Vector2D> result = Uv0Projection.Unified(0, 3);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("scale index 3", result.Refusal.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Scale_index_zero_collapses_every_coordinate()
    {
        Result<Vector2D> result = Uv0Projection.Unified(0x4000_4000, 0);

        Assert.Equal(0, result.Value.X);
        Assert.Equal(0, result.Value.Y);
    }
}
