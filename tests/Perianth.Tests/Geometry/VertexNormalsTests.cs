using System;
using System.Collections.Immutable;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Geometry;

public sealed class VertexNormalsTests
{
    [Fact]
    public void A_single_triangle_gives_every_vertex_the_face_normal()
    {
        ImmutableArray<Vector3D> positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

        ImmutableArray<Vector3D> normals = Compute(positions, [0, 1, 2]);

        Assert.All(normals, normal =>
        {
            Assert.Equal(0, normal.X, 1e-12);
            Assert.Equal(0, normal.Y, 1e-12);
            Assert.Equal(1, normal.Z, 1e-12);
        });
    }

    [Fact]
    public void Contributions_are_weighted_by_area_rather_than_averaged()
    {
        // Two triangles meeting at vertex 0, one lying in +Z and a much larger
        // one tilted into +Y. Averaging unit normals would put the shared normal
        // half way between them; weighting by area pulls it toward the larger.
        ImmutableArray<Vector3D> positions =
        [
            new(0, 0, 0),
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 10),
            new(10, 0, 10),
        ];

        ImmutableArray<Vector3D> normals = Compute(positions, [0, 1, 2, 0, 3, 4]);

        Vector3D shared = normals[0];
        double small = Math.Abs(shared.Z);
        double large = Math.Abs(shared.Y);

        Assert.True(large > small, "the larger triangle must dominate the shared normal");

        // An equal-weight average of the two unit normals would have made the
        // two components equal in magnitude.
        Assert.NotEqual(large, small, 1e-6);
    }

    [Fact]
    public void A_vertex_no_triangle_references_gets_the_non_rendering_filler()
    {
        ImmutableArray<Vector3D> positions =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
            new(50, 50, 50),
        ];

        ImmutableArray<Vector3D> normals = Compute(positions, [0, 1, 2]);

        Assert.Equal(new Vector3D(0, 1, 0), normals[3]);
    }

    [Fact]
    public void A_vertex_touched_only_by_a_zero_area_triangle_gets_the_filler()
    {
        // Three collinear points have no area and so no direction to give.
        ImmutableArray<Vector3D> positions = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)];

        ImmutableArray<Vector3D> normals = Compute(positions, [0, 1, 2]);

        Assert.All(normals, normal => Assert.Equal(new Vector3D(0, 1, 0), normal));
    }

    [Fact]
    public void A_zero_area_triangle_contributes_nothing_to_a_vertex_that_has_area_elsewhere()
    {
        ImmutableArray<Vector3D> positions =
        [
            new(0, 0, 0), new(1, 0, 0), new(0, 1, 0),
            new(2, 0, 0),
        ];

        // Triangle two is collinear along X and must not disturb vertex 0.
        ImmutableArray<Vector3D> withDegenerate = Compute(positions, [0, 1, 2, 0, 1, 3]);
        ImmutableArray<Vector3D> withoutDegenerate = Compute(positions, [0, 1, 2]);

        Assert.Equal(withoutDegenerate[0], withDegenerate[0]);
    }

    [Fact]
    public void Normals_that_cancel_exactly_are_refused_rather_than_filled_in()
    {
        // Both triangles have real area, and their cross products are exact
        // opposites. Handing back the filler here would hide a contradiction
        // behind a value that looks deliberate.
        ImmutableArray<Vector3D> positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

        Result<ImmutableArray<Vector3D>> result =
            VertexNormals.Compute(positions, [0, 1, 2, 0, 2, 1], ordinal: 4);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Contains("cancelled out", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("part 4", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_that_is_not_finite_refuses_rather_than_propagating()
    {
        ImmutableArray<Vector3D> positions = [new(0, 0, 0), new(double.NaN, 0, 0), new(0, 1, 0)];

        Result<ImmutableArray<Vector3D>> result =
            VertexNormals.Compute(positions, [0, 1, 2], ordinal: 0);

        Assert.True(result.IsRefused);
        Assert.Contains("not finite", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_normal_is_a_unit_vector()
    {
        ImmutableArray<Vector3D> positions =
        [
            new(0, 0, 0), new(3, 0, 0), new(0, 4, 0), new(0, 0, 5),
        ];

        ImmutableArray<Vector3D> normals = Compute(positions, [0, 1, 2, 0, 2, 3, 0, 3, 1]);

        Assert.All(normals, normal => Assert.Equal(1.0, normal.Length, 1e-12));
    }

    private static ImmutableArray<Vector3D> Compute(
        ImmutableArray<Vector3D> positions, ImmutableArray<int> indices)
    {
        Result<ImmutableArray<Vector3D>> result = VertexNormals.Compute(positions, indices, ordinal: 0);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }
}
