using System;
using System.Globalization;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Imaging;

/// <summary>
/// Resolves a repeated DiffuseColor and a clamped TransparentColor into one
/// image over the region a primitive actually uses, byte-exactly for colour.
/// </summary>
/// <remarks>
/// <para>
/// The engine samples DiffuseColor at <c>uv * myUVRepeat</c> repeated and
/// TransparentColor at the raw <c>uv</c> clamped. Those two rules cannot share a
/// sampler, but they can share an <em>image</em>: the colour is a whole number
/// of diffuse tiles copied byte for byte, the alpha is the engine's clamped
/// field resolved onto that grid, and one texel of gutter around the interior
/// carries each channel's own continuation — the colour wraps to the opposite
/// tile, the alpha holds its edge. The rewritten UV0 insets inside that border,
/// so the primitive never samples the baked image's own edge and the result is
/// independent of the sampler it is given.
/// </para>
/// <para>
/// Colour never passes through a resampler here: tiling and any enlargement are
/// byte replication, structurally distinct from <see cref="TextureResample"/>,
/// which the alpha field alone uses — the one documented approximation.
/// </para>
/// </remarks>
public static class TextureBake
{
    // A baked image is bounded by feasibility, not by trust in the corpus. The
    // largest bake observed archive-wide is 1.64 MP; this is three orders of
    // magnitude above that, so it stops only genuinely pathological input.
    private const long MaxBakedPixels = 64L * 1024 * 1024;

    /// <summary>
    /// Bakes <paramref name="diffuse"/> and <paramref name="transparent"/> over
    /// <paramref name="extent"/> at <paramref name="repeatU"/> by
    /// <paramref name="repeatV"/>.
    /// </summary>
    /// <remarks>
    /// A non-positive or non-finite repeat refuses: a zero repeat collapses the
    /// colour's coordinate and a negative one mirrors it, so the positive tile
    /// grid the bake is built on does not describe what the engine samples. A
    /// bake past the size cap returns an <see cref="ComposedTexture.Oversized"/>
    /// outcome rather than refusing, so it costs one part and not the export.
    /// </remarks>
    public static Result<ComposedTexture> Bake(
        RgbaImage diffuse,
        RgbaImage transparent,
        Uv0Extent extent,
        double repeatU,
        double repeatV)
    {
        ArgumentNullException.ThrowIfNull(diffuse);
        ArgumentNullException.ThrowIfNull(transparent);

        if (!(repeatU > 0.0 && double.IsFinite(repeatU) && repeatV > 0.0 && double.IsFinite(repeatV)))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"a varying TransparentColor alpha accompanies a DiffuseColor myUVRepeat of {repeatU}x{repeatV}, which is not a positive tiling"));
        }

        int dw = diffuse.Width;
        int dh = diffuse.Height;

        (int firstU, int lastU) = TileBounds(extent.UMin * repeatU, extent.UMax * repeatU, dw);
        (int firstV, int lastV) = TileBounds(extent.VMin * repeatV, extent.VMax * repeatV, dh);
        int tilesU = lastU - firstU;
        int tilesV = lastV - firstV;

        int width = tilesU * dw;
        int height = tilesV * dh;

        // The alpha spans this much of its own coordinate, and must not be
        // decimated to fit the colour's grid. Any enlargement is a whole
        // multiple so the tiled colour stays an exact copy.
        double spanU = tilesU / repeatU;
        double spanV = tilesV / repeatV;
        int scaleU = Math.Max(1, (int)Math.Ceiling(transparent.Width * spanU / width));
        int scaleV = Math.Max(1, (int)Math.Ceiling(transparent.Height * spanV / height));
        width *= scaleU;
        height *= scaleV;

        if ((long)(width + 2) * (height + 2) > MaxBakedPixels)
        {
            return Result.Ok(ComposedTexture.OversizedBake(tilesU, tilesV, width + 2, height + 2));
        }

        byte[] colour = Tiled(diffuse, tilesU, tilesV);
        if (scaleU != 1 || scaleV != 1)
        {
            colour = NearestEnlarge(colour, tilesU * dw, tilesV * dh, scaleU, scaleV);
        }

        byte[] field = ClampedAlphaField(
            transparent,
            width,
            height,
            spanU,
            spanV,
            firstU / repeatU,
            firstV / repeatV);

        // Install the clamped alpha over the tiled colour.
        for (int i = 0; i < width * height; i++)
        {
            colour[(i * 4) + 3] = field[i];
        }

        RgbaImage baked = Gutter(colour, field, width, height);

        // new_u = (1 + (u * repeat - first) / tiles * width) / (width + 2)
        double remapScaleU = repeatU * width / (tilesU * (width + 2));
        double remapScaleV = repeatV * height / (tilesV * (height + 2));
        double remapOffsetU = (1.0 - (firstU * (double)width / tilesU)) / (width + 2);
        double remapOffsetV = (1.0 - (firstV * (double)height / tilesV)) / (height + 2);

        string identity = string.Create(
            CultureInfo.InvariantCulture,
            $"baked;{repeatU},{repeatV};{firstU},{lastU},{firstV},{lastV};{dw}x{dh};{transparent.Width}x{transparent.Height}");

        return Result.Ok(ComposedTexture.Baked(
            baked,
            new Uv0Remap(remapScaleU, remapScaleV, remapOffsetU, remapOffsetV),
            identity));
    }

    /// <summary>
    /// The integer tile span covering <c>[lo, hi]</c>, each bound snapped to a
    /// whole tile when it lies within half a texel of one.
    /// </summary>
    /// <remarks>
    /// Snapping matters more than it looks: unsnapped, a coordinate of -1e-6 puts
    /// the floor at -1 and inflates a one-tile bake into three.
    /// </remarks>
    private static (int First, int Last) TileBounds(double lo, double hi, int texels)
    {
        double limit = 0.5 / Math.Max(texels, 1);
        lo = SnapWithin(lo, limit);
        hi = SnapWithin(hi, limit);
        int first = (int)Math.Floor(lo);
        int last = (int)Math.Ceiling(hi);
        return (first, Math.Max(last, first + 1));
    }

    private static double SnapWithin(double value, double limit)
    {
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        return Math.Abs(value - rounded) < limit ? rounded : value;
    }

    /// <summary>Repeat an image across a whole number of tiles, by byte copy.</summary>
    private static byte[] Tiled(RgbaImage image, int tilesU, int tilesV)
    {
        int w = image.Width;
        int h = image.Height;
        ReadOnlySpan<byte> src = image.Pixels;

        if (tilesU == 1 && tilesV == 1)
        {
            return src.ToArray();
        }

        int outWidth = w * tilesU;
        byte[] output = new byte[outWidth * (h * tilesV) * 4];
        for (int column = 0; column < tilesU; column++)
        {
            for (int row = 0; row < tilesV; row++)
            {
                int originX = column * w;
                int originY = row * h;
                for (int y = 0; y < h; y++)
                {
                    int srcRow = y * w * 4;
                    int dstRow = ((originY + y) * outWidth + originX) * 4;
                    src.Slice(srcRow, w * 4).CopyTo(output.AsSpan(dstRow, w * 4));
                }
            }
        }

        return output;
    }

    /// <summary>Replicate each colour texel into a whole block, by byte copy.</summary>
    private static byte[] NearestEnlarge(byte[] source, int width, int height, int scaleU, int scaleV)
    {
        int outWidth = width * scaleU;
        int outHeight = height * scaleV;
        byte[] output = new byte[outWidth * outHeight * 4];

        for (int y = 0; y < outHeight; y++)
        {
            int srcY = y / scaleV;
            for (int x = 0; x < outWidth; x++)
            {
                int srcX = x / scaleU;
                int srcIndex = (srcY * width + srcX) * 4;
                int dstIndex = (y * outWidth + x) * 4;
                output[dstIndex] = source[srcIndex];
                output[dstIndex + 1] = source[srcIndex + 1];
                output[dstIndex + 2] = source[srcIndex + 2];
                output[dstIndex + 3] = source[srcIndex + 3];
            }
        }

        return output;
    }

    /// <summary>
    /// The alpha as the engine clamps it, resolved onto the baked grid.
    /// </summary>
    /// <remarks>
    /// The region inside the unit range carries the resampled alpha; everything
    /// outside holds the corresponding edge, which is what clamping means. Edge
    /// extension is whole-strip replication so the cost stays proportional to the
    /// border, not the area.
    /// </remarks>
    private static byte[] ClampedAlphaField(
        RgbaImage transparent,
        int width,
        int height,
        double spanU,
        double spanV,
        double originU,
        double originV)
    {
        (int left, int right) = Band(originU, spanU, width);
        (int top, int bottom) = Band(originV, spanV, height);

        int insideWidth = right - left;
        int insideHeight = bottom - top;
        byte[] inside = ResizedAlpha(transparent, insideWidth, insideHeight);

        byte[] field = new byte[width * height];

        // The resampled interior.
        for (int j = 0; j < insideHeight; j++)
        {
            for (int i = 0; i < insideWidth; i++)
            {
                field[(top + j) * width + (left + i)] = inside[j * insideWidth + i];
            }
        }

        // Left and right columns hold the interior's own edge.
        for (int j = 0; j < insideHeight; j++)
        {
            byte leftEdge = inside[j * insideWidth];
            byte rightEdge = inside[j * insideWidth + (insideWidth - 1)];
            int row = (top + j) * width;
            for (int x = 0; x < left; x++)
            {
                field[row + x] = leftEdge;
            }

            for (int x = right; x < width; x++)
            {
                field[row + x] = rightEdge;
            }
        }

        // Top and bottom strips extend the now-complete first and last interior
        // rows, corners included.
        for (int y = 0; y < top; y++)
        {
            for (int x = 0; x < width; x++)
            {
                field[y * width + x] = field[top * width + x];
            }
        }

        for (int y = bottom; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                field[y * width + x] = field[(bottom - 1) * width + x];
            }
        }

        return field;
    }

    /// <summary>The half-open pixel range whose centres fall inside the unit range.</summary>
    private static (int First, int Last) Band(double start, double extent, int pixels)
    {
        int first = Math.Max(0, (int)Math.Ceiling((0.0 - start) / extent * pixels - 0.5));
        int last = Math.Min(pixels, (int)Math.Floor((1.0 - start) / extent * pixels + 0.5));
        return (first, Math.Max(last, first + 1));
    }

    /// <summary>
    /// The transparent image's alpha band, bilinearly resized to the interior
    /// size and returned as one byte per texel.
    /// </summary>
    private static byte[] ResizedAlpha(RgbaImage transparent, int width, int height)
    {
        // Broadcast the alpha into all channels so the shared bilinear kernel
        // resamples it identically to a single-channel resize, then read one back.
        ReadOnlySpan<byte> pixels = transparent.Pixels;
        byte[] broadcast = new byte[transparent.Width * transparent.Height * 4];
        for (int i = 0; i < transparent.Width * transparent.Height; i++)
        {
            byte a = pixels[(i * 4) + 3];
            broadcast[i * 4] = a;
            broadcast[(i * 4) + 1] = a;
            broadcast[(i * 4) + 2] = a;
            broadcast[(i * 4) + 3] = a;
        }

        RgbaImage resized = TextureResample.ToSize(new RgbaImage(transparent.Width, transparent.Height, broadcast), width, height);
        ReadOnlySpan<byte> resizedPixels = resized.Pixels;
        byte[] field = new byte[width * height];
        for (int i = 0; i < width * height; i++)
        {
            field[i] = resizedPixels[(i * 4) + 3];
        }

        return field;
    }

    /// <summary>
    /// Surround the interior with one texel of gutter, each channel continued by
    /// its own rule: colour wraps to the opposite tile, alpha holds its edge.
    /// </summary>
    private static RgbaImage Gutter(byte[] colour, byte[] field, int width, int height)
    {
        int outWidth = width + 2;
        int outHeight = height + 2;
        byte[] baked = new byte[outWidth * outHeight * 4];

        // The interior, inset by one texel.
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * 4;
            int dstRow = ((y + 1) * outWidth + 1) * 4;
            Array.Copy(colour, srcRow, baked, dstRow, width * 4);
        }

        // Left and right colour columns wrap: the opposite interior edge.
        for (int y = 0; y < height; y++)
        {
            int leftSrc = (y * width + (width - 1)) * 4;
            int rightSrc = (y * width) * 4;
            int leftDst = ((y + 1) * outWidth + 0) * 4;
            int rightDst = ((y + 1) * outWidth + (width + 1)) * 4;
            Array.Copy(colour, leftSrc, baked, leftDst, 4);
            Array.Copy(colour, rightSrc, baked, rightDst, 4);
        }

        // Top and bottom rows extend the first and last constructed rows.
        Array.Copy(baked, (1 * outWidth) * 4, baked, 0, outWidth * 4);
        Array.Copy(baked, (height * outWidth) * 4, baked, ((height + 1) * outWidth) * 4, outWidth * 4);

        // The colour wrapped across the gutter but the alpha must hold its edge,
        // so the alpha channel of the border is written back from the field.
        for (int y = 0; y < height; y++)
        {
            baked[((y + 1) * outWidth + 0) * 4 + 3] = field[y * width];
            baked[((y + 1) * outWidth + (width + 1)) * 4 + 3] = field[y * width + (width - 1)];
        }

        for (int x = 0; x < outWidth; x++)
        {
            baked[(0 * outWidth + x) * 4 + 3] = baked[(1 * outWidth + x) * 4 + 3];
            baked[((height + 1) * outWidth + x) * 4 + 3] = baked[(height * outWidth + x) * 4 + 3];
        }

        return new RgbaImage(outWidth, outHeight, baked);
    }
}
