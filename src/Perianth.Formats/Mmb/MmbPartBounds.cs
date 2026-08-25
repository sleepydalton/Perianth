using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Numerics;

namespace Perianth.Formats.Mmb;

/// <summary>
/// The twelve floats at the head of a part envelope, computed from the geometry
/// the part draws.
/// </summary>
/// <remarks>
/// <para>
/// The block <see cref="MmbModelPart.Values"/> keeps. It was carried verbatim
/// for a long time because nothing here read it and a round trip did not need
/// it to mean anything. It means this, measured over 433,944 parts (Roadmap
/// §10.65):
/// </para>
/// <list type="table">
/// <item><term>0 to 2</term><description>the bounding box minimum — 100%</description></item>
/// <item><term>3 to 5</term><description>the bounding box maximum — 100%</description></item>
/// <item><term>6</term><description>the furthest vertex from the model origin — 100%</description></item>
/// <item><term>7 to 9</term><description>the bounding box centre — 100%</description></item>
/// <item><term>10</term><description>the furthest vertex from that centre — a sphere</description></item>
/// <item><term>11</term><description>the same measured in the XZ plane only — a cylinder about the up axis</description></item>
/// </list>
/// <para>
/// <b>They are radii of the vertices, not of the box.</b> A shape that does not
/// reach its own box corner has a smaller radius than the box diagonal, which is
/// why the box-derived readings matched only a quarter of the corpus and the
/// vertex-derived ones matched all of it.
/// </para>
/// <para>
/// So the block is <em>derived</em>, and a part whose geometry changed must have
/// it recomputed — a stale bounding box is a part the game may cull while it is
/// on screen, which nothing in an offline render would show. It is also what
/// lets a part be <em>added</em> without a template: a new part computes its own
/// block rather than inheriting one that describes something else.
/// </para>
/// <para>
/// Word 11 is the one rule that is not exact: 472 parts in 433,944 carry
/// something else there. The rule is used anyway because it is right on
/// everything else and the field is a bound; what those 472 do instead is
/// unresolved and recorded rather than guessed at.
/// </para>
/// </remarks>
public static class MmbPartBounds
{
    /// <summary>How many floats the block holds.</summary>
    public const int Length = 12;

    /// <summary>
    /// Computes the block for a part drawing <paramref name="vertices"/>.
    /// </summary>
    /// <remarks>
    /// The vertices are the part's own pool slots — each distinct planar point
    /// at the depth its packed index selects — not its corners. A repeated
    /// position contributes nothing a single one does not.
    /// </remarks>
    public static ImmutableArray<float> Compute(IReadOnlyList<Vector3> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        if (vertices.Count == 0)
        {
            return ImmutableArray.CreateRange(new float[Length]);
        }

        Vector3 low = vertices[0];
        Vector3 high = vertices[0];
        foreach (Vector3 vertex in vertices)
        {
            low = Vector3.Min(low, vertex);
            high = Vector3.Max(high, vertex);
        }

        Vector3 centre = (low + high) * 0.5f;

        float fromOrigin = 0f;
        float sphere = 0f;
        float cylinder = 0f;
        foreach (Vector3 vertex in vertices)
        {
            Vector3 offset = vertex - centre;
            fromOrigin = Math.Max(fromOrigin, Magnitude(vertex.X, vertex.Y, vertex.Z));
            sphere = Math.Max(sphere, Magnitude(offset.X, offset.Y, offset.Z));
            cylinder = Math.Max(cylinder, Magnitude(offset.X, 0f, offset.Z));
        }

        return
        [
            low.X, low.Y, low.Z,
            high.X, high.Y, high.Z,
            fromOrigin,
            centre.X, centre.Y, centre.Z,
            sphere,
            cylinder,
        ];
    }

    /// <summary>
    /// A vector length, accumulated one component at a time in single precision.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than left to <see cref="Vector3.Length"/> because the
    /// result has to be the one a C++ program using floats produced, and that is
    /// a property of the order and the width of the arithmetic rather than of the
    /// formula. Adding a zero component is exact, so a two-dimensional radius is
    /// this with the unwanted axis passed as zero.
    /// </remarks>
    private static float Magnitude(float x, float y, float z)
    {
        float squared = 0f;
        squared += x * x;
        squared += y * y;
        squared += z * z;
        return MathF.Sqrt(squared);
    }
}
