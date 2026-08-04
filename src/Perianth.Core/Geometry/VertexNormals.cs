using System.Collections.Immutable;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Geometry;

/// <summary>
/// Area-weighted vertex normals.
/// </summary>
/// <remarks>
/// The normal of a vertex is the normalized sum of the <em>unnormalized</em>
/// cross products of the triangles touching it, which weights each triangle by
/// twice its area. Normalizing per triangle first would weight them equally and
/// is a different answer.
/// </remarks>
internal static class VertexNormals
{
    /// <summary>
    /// Below this, a cross product is treated as having no area at all and
    /// contributes nothing.
    /// </summary>
    private const double ZeroAreaEpsilon = 1.0e-12;

    /// <summary>
    /// What a vertex gets when nothing gave it a direction.
    /// </summary>
    /// <remarks>
    /// Unreferenced vertices, and vertices touched only by zero-area triangles,
    /// are not drawn, so this is filler rather than a computed value.
    /// </remarks>
    private static readonly Vector3D NonRenderingFiller = new(0, 1, 0);

    public static Result<ImmutableArray<Vector3D>> Compute(
        ImmutableArray<Vector3D> positions,
        ImmutableArray<int> indices,
        int ordinal)
    {
        Vector3D[] accumulated = new Vector3D[positions.Length];
        bool[] contributed = new bool[positions.Length];

        for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
        {
            int a = indices[triangle];
            int b = indices[triangle + 1];
            int c = indices[triangle + 2];

            Vector3D cross = Vector3D.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            if (!cross.IsFinite)
            {
                return Malformed(ordinal, "produces a cross product that is not finite");
            }

            if (cross.Length <= ZeroAreaEpsilon)
            {
                // Zero area carries no direction, so it contributes nothing and
                // does not count as having touched its vertices either.
                continue;
            }

            foreach (int vertex in stackalloc[] { a, b, c })
            {
                accumulated[vertex] += cross;
                contributed[vertex] = true;
                if (!accumulated[vertex].IsFinite)
                {
                    return Malformed(ordinal, "accumulates a vertex normal that overflows");
                }
            }
        }

        ImmutableArray<Vector3D>.Builder normals =
            ImmutableArray.CreateBuilder<Vector3D>(positions.Length);

        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            if (!contributed[vertex])
            {
                normals.Add(NonRenderingFiller);
                continue;
            }

            double length = accumulated[vertex].Length;
            if (length <= ZeroAreaEpsilon)
            {
                // The vertex took part in triangles that each had area, and they
                // cancelled. That is not something a filler should paper over.
                return Malformed(ordinal, "has a vertex whose contributing normals cancelled out");
            }

            normals.Add(accumulated[vertex].Scaled(1.0 / length));
        }

        return Result.Ok(normals.MoveToImmutable());
    }

    private static Refusal Malformed(int ordinal, string problem) =>
        Refusal.Malformed(string.Create(
            CultureInfo.InvariantCulture, $"Model part {ordinal} {problem}."));
}
