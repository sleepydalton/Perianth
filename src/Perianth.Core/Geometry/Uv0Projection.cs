using System;
using System.Collections.Immutable;
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

    /// <summary>
    /// Packs a UV0 pair back into the word <see cref="Unified"/> reads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inverse of the split above, and the operation that lets a part which
    /// carries its own texture coordinates be given new ones. Without it an
    /// imported mesh could only keep the coordinates of whatever it replaced,
    /// which is why redrawing such a part used to refuse.
    /// </para>
    /// <para>
    /// Two things it will not do. A scale of zero encodes nothing but zero, so a
    /// non-zero coordinate against it refuses rather than being written as a
    /// value the game will read back as zero. And the low end clamps at −32767
    /// rather than −32768: reading floors at −1, so the extra step would come
    /// back as −1 anyway and would not survive a round trip.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The narrowest scale that can hold every coordinate given, or a refusal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a part converted from projected to carried needs, because a carried
    /// coordinate is a signed fraction of a scale rather than a number in its
    /// own right. The table holds three: zero, which stores nothing but zero;
    /// one, which covers the ordinary <c>0..1</c> a 3D program produces; and
    /// about eight, which covers a layout tiled several times over.
    /// </para>
    /// <para>
    /// Narrowest, because the stored value is a signed 16-bit fraction of the
    /// scale — so the smaller the scale, the finer the steps. Choosing eight
    /// where one would do throws away three bits of precision on every
    /// coordinate.
    /// </para>
    /// </remarks>
    public static Result<int> ScaleFor(ImmutableArray<Vector2D> uv)
    {
        double widest = 0;

        foreach (Vector2D each in uv)
        {
            if (!double.IsFinite(each.X) || !double.IsFinite(each.Y))
            {
                return Refusal.Malformed("A texture coordinate is not a finite number.");
            }

            widest = Math.Max(widest, Math.Max(Math.Abs(each.X), Math.Abs(each.Y)));
        }

        // Chosen by value rather than by index: the table is not in order.
        int best = -1;
        for (int index = 0; index < UnifiedScales.Length; index++)
        {
            if (UnifiedScales[index] < widest)
            {
                continue;
            }

            if (best < 0 || UnifiedScales[index] < UnifiedScales[best])
            {
                best = index;
            }
        }

        return best >= 0
            ? Result.Ok(best)
            : Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A texture coordinate reaches {widest:0.###} and the widest this format stores is {UnifiedScales[^1]:0.###}. Scale the layout down in your 3D program, or keep the part projecting."));
    }

    public static Result<uint> Pack(Vector2D uv, int scaleIndex)
    {
        if (scaleIndex < 0 || scaleIndex >= UnifiedScales.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A constant selects UV0 scale index {scaleIndex}, which has no defined scale."));
        }

        double scale = UnifiedScales[scaleIndex];
        Result<short> u = Raw(uv.X, scale, "U");
        if (!u.TryGetValue(out short packedU, out Refusal? uRefusal))
        {
            return uRefusal;
        }

        Result<short> v = Raw(uv.Y, scale, "V");
        return v.TryGetValue(out short packedV, out Refusal? vRefusal)
            ? Result.Ok((uint)(ushort)packedU | ((uint)(ushort)packedV << 16))
            : vRefusal;
    }

    private static Result<short> Raw(double component, double scale, string which)
    {
        if (!double.IsFinite(component))
        {
            return Refusal.Malformed($"A {which} texture coordinate is not a finite number.");
        }

        if (scale == 0)
        {
            return component == 0
                ? Result.Ok((short)0)
                : Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A {which} texture coordinate of {component} cannot be stored: this part's UV0 scale is zero, so the only value it can hold is zero."));
        }

        double scaled = component / scale;
        if (scaled is < -1.0 or > 1.0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A {which} texture coordinate of {component} is outside the range this part can store, which is -{scale} to {scale}."));
        }

        return Result.Ok((short)Math.Round(scaled * SignedRange, MidpointRounding.AwayFromZero));
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
