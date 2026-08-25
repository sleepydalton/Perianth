using System;
using System.Collections.Immutable;
using System.Numerics;
using Perianth.Formats.Mmb;
using Xunit;

namespace Perianth.Tests.Mmb;

/// <summary>
/// The twelve floats at the head of a part envelope.
/// </summary>
/// <remarks>
/// Derived rather than carried, which is what makes them testable at all: the
/// corpus identified every one against the part's own geometry (Roadmap §10.65),
/// so a wrong rule here is a wrong number rather than a matter of taste.
/// </remarks>
public sealed class MmbPartBoundsTests
{
    /// <summary>
    /// A diamond, whose points are the edge midpoints of its own box.
    /// </summary>
    /// <remarks>
    /// The shape the corpus distinguished the readings with. A square reaches
    /// its box corners, so a radius measured to the vertices and one measured
    /// to the corner agree and nothing can tell them apart — which is how the
    /// first draft of these tests let that mutation live. A diamond does not
    /// reach them, and the two answers differ by a factor of the square root of
    /// two.
    /// </remarks>
    private static readonly Vector3[] Diamond =
    [
        new(10, 0, 7), new(20, 10, 7), new(10, 20, 7), new(0, 10, 7),
    ];

    [Fact]
    public void The_box_is_the_extent_of_the_vertices()
    {
        ImmutableArray<float> block = MmbPartBounds.Compute(Diamond);

        Assert.Equal([0f, 0f, 7f], block[0..3]);
        Assert.Equal([20f, 20f, 7f], block[3..6]);
        Assert.Equal([10f, 10f, 7f], block[7..10]);
    }

    [Fact]
    public void The_radii_are_measured_to_the_vertices_and_not_to_the_box()
    {
        ImmutableArray<float> block = MmbPartBounds.Compute(Diamond);

        // Every point is ten from the centre. The box corner is 14.14, which is
        // what a box-derived reading would give and what the corpus rejected.
        Assert.Equal(10f, block[10], 4);
        Assert.Equal(10f, block[11], 4);

        // Furthest from the model origin is either of the two far points.
        Assert.Equal(MathF.Sqrt(400f + 100f + 49f), block[6], 4);
    }

    [Fact]
    public void The_second_radius_is_a_cylinder_about_the_up_axis()
    {
        // Word 11 is the radius in the XZ plane, not along X. On a flat part the
        // depth term is zero and the two are the same number, which is why 95%
        // of the corpus cannot tell them apart and why this fixture has depth.
        //
        // Four points spanning 6 in X and 8 in Z, centred at the origin: the X
        // radius alone would be 3, and the radius in the plane is 5.
        ImmutableArray<float> block = MmbPartBounds.Compute(
        [
            new(-3, 100, -4), new(3, 100, -4), new(3, 100, 4), new(-3, 100, 4),
        ]);

        Assert.Equal(5f, block[11], 4);
        Assert.Equal(5f, block[10], 4);
        Assert.Equal(3f, (block[3] - block[0]) / 2f, 4);
    }

    [Fact]
    public void A_part_drawing_nothing_has_a_block_of_zeroes()
    {
        // Not a case the corpus has -- every part draws something -- but the
        // computation must have an answer rather than an exception, because it
        // runs before anything has checked the part is worth writing.
        ImmutableArray<float> block = MmbPartBounds.Compute([]);

        Assert.Equal(MmbPartBounds.Length, block.Length);
        Assert.All(block, value => Assert.Equal(0f, value));
    }

    [Fact]
    public void A_single_point_has_no_extent_and_a_radius_of_its_own_distance()
    {
        ImmutableArray<float> block = MmbPartBounds.Compute([new Vector3(3, 4, 0)]);

        Assert.Equal([3f, 4f, 0f], block[0..3]);
        Assert.Equal([3f, 4f, 0f], block[3..6]);
        Assert.Equal(5f, block[6], 4);
        Assert.Equal(0f, block[10], 4);
        Assert.Equal(0f, block[11], 4);
    }
}
