namespace Perianth.Core.Imaging;

/// <summary>
/// An affine rewrite of a primitive's UV0, produced by a tile bake.
/// </summary>
/// <remarks>
/// A bake evaluates <c>myUVRepeat</c> into the pixels over the region the part
/// uses, so the primitive's own coordinates must be rewritten to address the
/// baked image and the repeat dropped. Applying the repeat again through a
/// <c>KHR_texture_transform</c> would scale coordinates the bake already
/// consumed. The new coordinate is <c>(u * ScaleU + OffsetU, v * ScaleV +
/// OffsetV)</c>.
/// </remarks>
public readonly record struct Uv0Remap(double ScaleU, double ScaleV, double OffsetU, double OffsetV);
