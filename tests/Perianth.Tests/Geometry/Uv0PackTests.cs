using System;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Geometry;

/// <summary>
/// Packing texture coordinates back into the word the game reads.
/// </summary>
/// <remarks>
/// The operation that lets a part carrying its own UV0 be given new ones, which
/// is the branch an imported mesh most wants to be on: the other 86% compute
/// UV0 from position as a planar projection, and that smears the sides and back
/// of anything three-dimensional.
/// </remarks>
public sealed class Uv0PackTests
{
    [Theory]
    [InlineData(2, 0.0, 0.0)]
    [InlineData(2, 1.0, 1.0)]
    [InlineData(2, 0.25, 0.75)]
    [InlineData(2, -1.0, 0.5)]
    [InlineData(1, 4.0, -2.0)]
    public void A_packed_coordinate_reads_back_as_the_one_written(int scaleIndex, double u, double v)
    {
        // The round trip is the whole claim. Sixteen signed bits per component
        // is about five decimal places against a scale of one, so the tolerance
        // is the format's rather than a guess.
        Result<uint> packed = Uv0Projection.Pack(new Vector2D(u, v), scaleIndex);
        Assert.True(packed.IsSuccess, packed.IsRefused ? packed.Refusal.Message : "");

        Result<Vector2D> read = Uv0Projection.Unified(packed.Value, scaleIndex);
        Assert.True(read.IsSuccess);
        Assert.Equal(u, read.Value.X, 4);
        Assert.Equal(v, read.Value.Y, 4);
    }

    [Fact]
    public void A_coordinate_past_the_scale_refuses_rather_than_wrapping()
    {
        // Sixteen bits wrap silently, and a wrapped coordinate paints the part
        // with a different corner of its texture -- a file that loads and looks
        // wrong, which is the outcome this project refuses instead.
        Result<uint> packed = Uv0Projection.Pack(new Vector2D(1.5, 0), scaleIndex: 2);

        Assert.True(packed.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, packed.Refusal.Kind);
        Assert.Contains("outside the range", packed.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_scale_holds_zero_and_says_so_about_anything_else()
    {
        Assert.True(Uv0Projection.Pack(new Vector2D(0, 0), scaleIndex: 0).IsSuccess);

        Result<uint> packed = Uv0Projection.Pack(new Vector2D(0.5, 0), scaleIndex: 0);

        Assert.True(packed.IsRefused);
        Assert.Contains("scale is zero", packed.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unpackable_scale_index_refuses_on_the_way_in_as_on_the_way_out()
    {
        Assert.True(Uv0Projection.Pack(new Vector2D(0, 0), scaleIndex: 3).IsRefused);
        Assert.True(Uv0Projection.Unified(0, scaleIndex: 3).IsRefused);
    }

    [Fact]
    public void A_coordinate_that_is_not_finite_refuses()
    {
        Result<uint> packed = Uv0Projection.Pack(new Vector2D(double.NaN, 0), scaleIndex: 2);

        Assert.True(packed.IsRefused);
        Assert.Equal(RefusalKind.Malformed, packed.Refusal.Kind);
    }
}
