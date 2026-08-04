using System;

namespace Perianth.Core.Imaging;

/// <summary>
/// Bilinearly resizes an image, the one deliberate approximation in the imaging path.
/// </summary>
/// <remarks>
/// <para>
/// This is its own type on purpose. <see cref="RgbaImage"/> offers no resize,
/// because the bake replicates and gutters bytes exactly and interpolating
/// those would be wrong. Resampling is needed only to reconcile a diffuse and
/// an alpha texture of different sizes for one combined image — a case the
/// specification names as a deliberate approximation — so it lives apart, where
/// it cannot be reached from the bake by accident.
/// </para>
/// <para>
/// The kernel is an ordinary bilinear with a pixel-centre coordinate mapping.
/// It is not required to match the reference's kernel byte-for-byte: structural
/// equivalence frees the port from that, and the validation tolerates the small
/// differences a resample introduces. What it must be is deterministic, which
/// it is — the output depends only on the input pixels and the target size.
/// </para>
/// </remarks>
public static class TextureResample
{
    /// <summary>
    /// Returns <paramref name="image"/> resized to <paramref name="width"/> by
    /// <paramref name="height"/>, or the same image when it already matches.
    /// </summary>
    public static RgbaImage ToSize(RgbaImage image, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (image.Width == width && image.Height == height)
        {
            return image;
        }

        ReadOnlySpan<byte> source = image.Pixels;
        int sourceStride = image.Stride;
        byte[] output = new byte[width * height * 4];

        double scaleX = (double)image.Width / width;
        double scaleY = (double)image.Height / height;

        for (int y = 0; y < height; y++)
        {
            // Pixel centres: the output centre maps to a source coordinate, and
            // the two neighbouring rows are weighted by the fractional distance.
            double sy = ((y + 0.5) * scaleY) - 0.5;
            int y0 = (int)Math.Floor(sy);
            double fy = sy - y0;
            int y0c = Math.Clamp(y0, 0, image.Height - 1);
            int y1c = Math.Clamp(y0 + 1, 0, image.Height - 1);

            for (int x = 0; x < width; x++)
            {
                double sx = ((x + 0.5) * scaleX) - 0.5;
                int x0 = (int)Math.Floor(sx);
                double fx = sx - x0;
                int x0c = Math.Clamp(x0, 0, image.Width - 1);
                int x1c = Math.Clamp(x0 + 1, 0, image.Width - 1);

                int topLeft = (y0c * sourceStride) + (x0c * 4);
                int topRight = (y0c * sourceStride) + (x1c * 4);
                int bottomLeft = (y1c * sourceStride) + (x0c * 4);
                int bottomRight = (y1c * sourceStride) + (x1c * 4);
                int destination = ((y * width) + x) * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    double top = (source[topLeft + channel] * (1 - fx)) + (source[topRight + channel] * fx);
                    double bottom = (source[bottomLeft + channel] * (1 - fx)) + (source[bottomRight + channel] * fx);
                    double value = (top * (1 - fy)) + (bottom * fy);
                    output[destination + channel] = (byte)Math.Clamp((int)Math.Round(value, MidpointRounding.ToEven), 0, 255);
                }
            }
        }

        return new RgbaImage(width, height, output);
    }
}
