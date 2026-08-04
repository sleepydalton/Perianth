using System;

namespace Perianth.Core.Imaging;

/// <summary>
/// One owned, straight-alpha, row-major RGBA8 image.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately offers no resize, resample or scale operation. The
/// bake is a byte-exact process — tile replication, one-tile bakes, gutters and
/// nearest-neighbour enlargement are exact because interpolating them would be
/// <em>wrong</em>, not because the reference happened to avoid it. Making a
/// resampler unreachable from the type the bake path holds is cheaper than a
/// rule saying not to call one.
/// </para>
/// <para>
/// Where a resample is genuinely required — reconciling a diffuse and an alpha
/// texture of different sizes, which the specification calls out as a
/// deliberate approximation — it is a named operation in its own type that
/// produces a new image, so the two cases cannot be confused at a call site.
/// </para>
/// </remarks>
public sealed class RgbaImage
{
    private readonly byte[] _pixels;

    /// <summary>Wraps a buffer of exactly <c>width * height * 4</c> bytes.</summary>
    public RgbaImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != (long)width * height * 4)
        {
            throw new ArgumentException(
                "The buffer does not hold exactly width * height RGBA8 texels.", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in texels.</summary>
    public int Width { get; }

    /// <summary>Height in texels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;

    /// <summary>
    /// The texels, four bytes each in R, G, B, A order, row-major from the
    /// top-left.
    /// </summary>
    public ReadOnlySpan<byte> Pixels => _pixels;
}
