using System;
using System.Globalization;
using System.Numerics;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Geometry;

/// <summary>
/// The two ways a vertex gets its UV0.
/// </summary>
internal static class Uv0Projection
{
    /// <summary>
    /// The unified scale table. Index 3 has no entry and refuses where it is
    /// selected.
    /// </summary>
    private static ReadOnlySpan<double> UnifiedScales => [0, 7.999755859375, 1];

    private const double SignedRange = 32767.0;

    /// <summary>
    /// Projects UV0 from a position onto the constant's surface.
    /// </summary>
    /// <remarks>
    /// Every term is binary64 per section 7.4. The serialized floats widen on
    /// the way in and nothing narrows again until a GLB payload is packed, which
    /// happens in another project entirely.
    /// </remarks>
    public static Vector2D Surface(Vector3D position, in SurfaceTerms terms)
    {
        double px = Dot(position, terms.InverseLocal.Column(0)) * terms.InverseUnitScale * terms.PositionXScale;
        double py = Dot(position, terms.InverseLocal.Column(1)) * terms.InverseUnitScale;

        double dx = px - terms.Origin.X;
        double dy = py - terms.Origin.Y;

        double u = ((dx * terms.SurfaceU.X) + (dy * terms.SurfaceU.Y)) * terms.SurfaceU.W;
        double v = 1 - (((dx * terms.SurfaceV.X) + (dy * terms.SurfaceV.Y)) * terms.SurfaceV.W);

        return new Vector2D(u, v);
    }

    /// <summary>
    /// Splits a packed unified UV0 word into its two components.
    /// </summary>
    /// <remarks>
    /// The low half is U and the high half is V, each a signed 16-bit value
    /// divided by 32767 and floored at -1 before the scale applies. The floor
    /// matters: -32768 divided by 32767 is slightly below -1, and without the
    /// clamp the widest stored value would overshoot its own scale.
    /// </remarks>
    public static Result<Vector2D> Unified(uint packed, int scaleIndex)
    {
        if (scaleIndex < 0 || scaleIndex >= UnifiedScales.Length)
        {
            // A selector the format can encode and the table has no entry for.
            // The bytes are coherent, so this is unsupported rather than
            // malformed.
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A constant selects UV0 scale index {scaleIndex}, which has no defined scale."));
        }

        double scale = UnifiedScales[scaleIndex];
        return Result.Ok(new Vector2D(
            Component((short)(packed & 0xFFFF), scale),
            Component((short)(packed >> 16), scale)));
    }

    private static double Component(short raw, double scale) =>
        Math.Max(-1.0, raw / SignedRange) * scale;

    private static double Dot(Vector3D position, Vector4 column) =>
        (position.X * column.X) +
        (position.Y * column.Y) +
        (position.Z * column.Z) +
        column.W;

    /// <summary>The constant fields surface projection needs, widened to binary64 inputs.</summary>
    internal readonly record struct SurfaceTerms(
        Vector4 Origin,
        Vector4 SurfaceU,
        Vector4 SurfaceV,
        SerializedMatrix InverseLocal,
        double PositionXScale,
        double InverseUnitScale)
    {
        public static SurfaceTerms From(in Mode2Constant constant) => new(
            constant.SurfaceOrigin, constant.SurfaceU, constant.SurfaceV,
            constant.InverseLocal, constant.PositionXScale, constant.InverseUnitScale);

        public static SurfaceTerms From(in Mode3Constant constant) => new(
            constant.SurfaceOrigin, constant.SurfaceU, constant.SurfaceV,
            constant.InverseLocal, constant.PositionXScale, constant.InverseUnitScale);
    }
}
