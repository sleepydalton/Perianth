using System;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Imaging;

/// <summary>
/// Combines a DiffuseColor image with a TransparentColor alpha into one image,
/// choosing the wrap state, clamp substitution, or tile bake the pair requires.
/// </summary>
/// <remarks>
/// <para>
/// The engine samples DiffuseColor for RGB with repeat and TransparentColor for
/// alpha with clamp, as two draws with two wrap states. One RGBA image under one
/// sampler can only stand in for that pair where the two wrap states cannot be
/// told apart. This is the whole decision ladder: its order is observable and
/// significant, so it is preserved exactly. A constant alpha short-circuits it; a
/// non-identity repeat bakes; an out-of-range UV0 clamps if it can and bakes if
/// it cannot; an in-range pair whose alpha edges disagree bakes; everything else
/// resamples to a common size and composes.
/// </para>
/// <para>
/// Binding one image to both channels is <em>not</em> a special case and takes
/// the same ladder as any other pair: the composition is a no-op but the wrap
/// divergence is not, so it must still be proven away.
/// </para>
/// </remarks>
public static class TextureComposition
{
    /// <summary>
    /// Composes <paramref name="diffuse"/>'s RGB with <paramref name="transparent"/>'s
    /// alpha for a part whose UV0 has the given range and extent.
    /// </summary>
    public static Result<ComposedTexture> Compose(
        RgbaImage diffuse,
        RgbaImage transparent,
        bool uv0InRange,
        double repeatU,
        double repeatV,
        Uv0Extent extent)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(transparent);

        (byte low, byte high) = AlphaExtrema(transparent);

        if (low == high)
        {
            // A constant alpha samples identically under every wrap state and at
            // every resolution, so neither guard below can apply to it.
            return Result.Ok(ComposedTexture.Combined(WithConstantAlpha(diffuse, low), clamp: false));
        }

        if (repeatU != 1.0 || repeatV != 1.0)
        {
            // DiffuseColor samples at uv * myUVRepeat while TransparentColor
            // samples the raw UV, so no single image and unmodified UV set carries
            // both. Baking resolves the two coordinate systems into the pixels.
            // This precedes the shared-path case below, which reconciles neither.
            return TextureBake.Bake(diffuse, transparent, extent, repeatU, repeatV);
        }

        if (!uv0InRange)
        {
            return ComposeNearBoundary(diffuse, transparent, extent);
        }

        if (!OppositeEdgesAgree(transparent))
        {
            // The alpha's opposing edges differ, so no single wrap state serves
            // both channels of a plain composite. The one-tile bake resolves it:
            // the interior is this same composition and the gutter carries each
            // channel's own continuation, which is the divergence just detected.
            return TextureBake.Bake(diffuse, transparent, extent, 1.0, 1.0);
        }

        (RgbaImage resampledDiffuse, RgbaImage resampledTransparent) = ResamplePair(diffuse, transparent);

        // The shipped alpha is the resampled one, so the wrap-compatibility
        // assertion belongs to it rather than to the source it came from.
        if (!OppositeEdgesAgree(resampledTransparent))
        {
            return TextureBake.Bake(diffuse, transparent, extent, 1.0, 1.0);
        }

        return Result.Ok(ComposedTexture.Combined(
            WithAlphaChannel(resampledDiffuse, resampledTransparent), clamp: false));
    }

    /// <summary>
    /// Composes a part whose UV0 leaves the unit range by under half a texel by
    /// clamping, or bakes when the overshoot is a genuine repeated span.
    /// </summary>
    private static Result<ComposedTexture> ComposeNearBoundary(
        RgbaImage diffuse,
        RgbaImage transparent,
        Uv0Extent extent)
    {
        if (!WithinHalfTexel(extent, diffuse, transparent))
        {
            // A genuine repeated span. Clamping cannot serve it -- the colour
            // really does tile across the part -- so it goes to the bake.
            return TextureBake.Bake(diffuse, transparent, extent, 1.0, 1.0);
        }

        (RgbaImage resampledDiffuse, RgbaImage resampledTransparent) = ResamplePair(diffuse, transparent);

        // Asserted on the image that actually ships, as the repeat path does: a
        // resample can disturb an agreement the source held.
        if (!CrossedEdgesAgree(resampledDiffuse, extent))
        {
            // Clamping would fix the alpha by altering the colour. The bake owes
            // nothing to either wrap state.
            return TextureBake.Bake(diffuse, transparent, extent, 1.0, 1.0);
        }

        // The engine samples TransparentColor clamped, so emitting the combined
        // image clamped reproduces its alpha exactly. The cost falls on the
        // diffuse, whose crossed edges were just proven identical.
        return Result.Ok(ComposedTexture.Combined(
            WithAlphaChannel(resampledDiffuse, resampledTransparent), clamp: true));
    }

    /// <summary>The lowest and highest alpha byte in an image.</summary>
    public static (byte Low, byte High) AlphaExtrema(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        ReadOnlySpan<byte> pixels = image.Pixels;
        byte low = 255;
        byte high = 0;

        for (int i = 3; i < pixels.Length; i += 4)
        {
            byte a = pixels[i];
            if (a < low)
            {
                low = a;
            }

            if (a > high)
            {
                high = a;
            }
        }

        return (low, high);
    }

    /// <summary>
    /// Whether wrapping this image's alpha reproduces clamping it.
    /// </summary>
    /// <remarks>
    /// A combined image carries one wrap state for all four channels, emitted as
    /// repeat because DiffuseColor wants it. At a boundary coordinate repeat
    /// blends the opposing edge texels while clamp holds the near one, so the
    /// substitution introduces no discrepancy exactly when the opposing edges
    /// are identical.
    /// </remarks>
    public static bool OppositeEdgesAgree(RgbaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        int width = image.Width;
        int height = image.Height;
        ReadOnlySpan<byte> pixels = image.Pixels;

        for (int y = 0; y < height; y++)
        {
            int row = y * image.Stride;
            if (pixels[row + 3] != pixels[row + ((width - 1) * 4) + 3])
            {
                return false;
            }
        }

        int lastRow = (height - 1) * image.Stride;
        for (int x = 0; x < width; x++)
        {
            if (pixels[(x * 4) + 3] != pixels[lastRow + (x * 4) + 3])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether clamping reproduces repeating on the diffuse's crossed axes.
    /// </summary>
    /// <remarks>
    /// Only the axes the extent actually crosses are examined. An extent that
    /// leaves the unit range in U alone never samples across the top and bottom
    /// edges, so their disagreement cannot reach a fragment and must not veto the
    /// substitution. The comparison is on RGB, the channels clamp would alter.
    /// </remarks>
    public static bool CrossedEdgesAgree(RgbaImage diffuse, Uv0Extent extent)
    {
        ArgumentNullException.ThrowIfNull(diffuse);

        int width = diffuse.Width;
        int height = diffuse.Height;
        ReadOnlySpan<byte> pixels = diffuse.Pixels;
        int stride = diffuse.Stride;

        if (extent.CrossesU)
        {
            for (int y = 0; y < height; y++)
            {
                int left = y * stride;
                int right = left + ((width - 1) * 4);
                if (pixels[left] != pixels[right] ||
                    pixels[left + 1] != pixels[right + 1] ||
                    pixels[left + 2] != pixels[right + 2])
                {
                    return false;
                }
            }
        }

        if (extent.CrossesV)
        {
            int lastRow = (height - 1) * stride;
            for (int x = 0; x < width; x++)
            {
                int top = x * 4;
                int bottom = lastRow + (x * 4);
                if (pixels[top] != pixels[bottom] ||
                    pixels[top + 1] != pixels[bottom + 1] ||
                    pixels[top + 2] != pixels[bottom + 2])
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the extent leaves the unit range by under half a texel.
    /// </summary>
    /// <remarks>
    /// The threshold is per axis from each source's own dimensions, and the
    /// strictest governs: a coarse alpha and a fine diffuse do not agree on how
    /// wide a texel is. Below half a texel no sampler resolves the two wrap states
    /// into a distinct texel centre.
    /// </remarks>
    public static bool WithinHalfTexel(Uv0Extent extent, RgbaImage diffuse, RgbaImage transparent)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(transparent);

        double uLimit = Math.Min(0.5 / diffuse.Width, 0.5 / transparent.Width);
        double vLimit = Math.Min(0.5 / diffuse.Height, 0.5 / transparent.Height);
        return extent.OvershootU < uLimit && extent.OvershootV < vLimit;
    }

    private static (RgbaImage Diffuse, RgbaImage Transparent) ResamplePair(RgbaImage diffuse, RgbaImage transparent)
    {
        int width = Math.Max(diffuse.Width, transparent.Width);
        int height = Math.Max(diffuse.Height, transparent.Height);
        return (TextureResample.ToSize(diffuse, width, height), TextureResample.ToSize(transparent, width, height));
    }

    private static RgbaImage WithConstantAlpha(RgbaImage diffuse, byte alpha)
    {
        byte[] pixels = diffuse.Pixels.ToArray();
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = alpha;
        }

        return new RgbaImage(diffuse.Width, diffuse.Height, pixels);
    }

    private static RgbaImage WithAlphaChannel(RgbaImage diffuse, RgbaImage alphaSource)
    {
        byte[] pixels = diffuse.Pixels.ToArray();
        ReadOnlySpan<byte> alpha = alphaSource.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = alpha[i];
        }

        return new RgbaImage(diffuse.Width, diffuse.Height, pixels);
    }
}
