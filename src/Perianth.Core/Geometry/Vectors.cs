using System;

namespace Perianth.Core.Geometry;

/// <summary>
/// A pair of binary64 coordinates.
/// </summary>
/// <remarks>
/// Specification section 7.4 requires geometry, UV and transform arithmetic to
/// be done in binary64, with conversion back to binary32 only where GLB payloads
/// are packed. <c>System.Numerics.Vector2</c> is binary32 and would quietly
/// round every intermediate, so the core carries its own.
/// </remarks>
public readonly record struct Vector2D(double X, double Y);

/// <summary>
/// A triple of binary64 coordinates.
/// </summary>
/// <remarks>See <see cref="Vector2D"/> for why this is not the numerics type.</remarks>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    /// <summary>The Euclidean length.</summary>
    public double Length => Math.Sqrt((X * X) + (Y * Y) + (Z * Z));

    /// <summary>Whether every component is finite.</summary>
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);

    /// <summary>Componentwise sum.</summary>
    public static Vector3D Add(Vector3D left, Vector3D right) => left + right;

    /// <summary>Componentwise difference.</summary>
    public static Vector3D Subtract(Vector3D left, Vector3D right) => left - right;

    /// <summary>The cross product, whose length is twice the triangle's area.</summary>
    public static Vector3D Cross(Vector3D left, Vector3D right) => new(
        (left.Y * right.Z) - (left.Z * right.Y),
        (left.Z * right.X) - (left.X * right.Z),
        (left.X * right.Y) - (left.Y * right.X));

    /// <summary>Componentwise sum.</summary>
    public static Vector3D operator +(Vector3D left, Vector3D right) =>
        new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

    /// <summary>Componentwise difference.</summary>
    public static Vector3D operator -(Vector3D left, Vector3D right) =>
        new(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

    /// <summary>Scales every component by <paramref name="scale"/>.</summary>
    public Vector3D Scaled(double scale) => new(X * scale, Y * scale, Z * scale);
}
