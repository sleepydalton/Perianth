namespace Perianth.Core.Imaging;

/// <summary>
/// The axis-aligned bounds of one part's UV0, in the primitive's own coordinates.
/// </summary>
/// <remarks>
/// The transparent ladder needs to know how far a part's texture coordinates
/// reach: whether they leave the unit square, by how much, and on which axis.
/// That decides between a plain composite, a clamped substitution, and a tile
/// bake. The thresholds live in <see cref="TextureComposition"/> and
/// <see cref="TextureBake"/>; this type only reports the geometry.
/// </remarks>
public readonly record struct Uv0Extent(double UMin, double UMax, double VMin, double VMax)
{
    /// <summary>The whole unit square, the extent an unknown region must be treated as.</summary>
    public static Uv0Extent Unit => new(0.0, 1.0, 0.0, 1.0);

    /// <summary>Whether the coordinates leave the unit range in U.</summary>
    public bool CrossesU => UMin < 0.0 || UMax > 1.0;

    /// <summary>Whether the coordinates leave the unit range in V.</summary>
    public bool CrossesV => VMin < 0.0 || VMax > 1.0;

    /// <summary>How far the coordinates overshoot the unit range in U, never negative.</summary>
    public double OvershootU => System.Math.Max(System.Math.Max(-UMin, UMax - 1.0), 0.0);

    /// <summary>How far the coordinates overshoot the unit range in V, never negative.</summary>
    public double OvershootV => System.Math.Max(System.Math.Max(-VMin, VMax - 1.0), 0.0);
}
