using System;

namespace Perianth.Core.Imaging;

/// <summary>A per-channel multiplicative gain and additive offset.</summary>
/// <remarks>
/// The recovered shader computes <c>diffuse.rgb * gain + offset</c> before the
/// albedo tint multiplies the result. Where the offset is non-default the whole
/// term is baked into the emitted image, because glTF has no base-colour offset
/// to carry an additive constant; a pure gain instead folds into
/// <c>baseColorFactor</c> and never reaches this type.
/// </remarks>
/// <param name="Gain">The multiplier applied to each of R, G and B.</param>
/// <param name="Offset">The constant added to each, on a 0..1 scale.</param>
public readonly record struct ColorAdjustment(Rgb Gain, Rgb Offset)
{
    /// <summary>The adjustment that changes nothing.</summary>
    public static ColorAdjustment Identity => new(new Rgb(1, 1, 1), new Rgb(0, 0, 0));

    /// <summary>Whether the offset is default, so the gain folds into the factor instead.</summary>
    public bool OffsetIsDefault => Offset is { R: 0, G: 0, B: 0 };
}

/// <summary>Three colour components, in the order R, G, B.</summary>
public readonly record struct Rgb(double R, double G, double B);

/// <summary>
/// Bakes a colour gain and offset into an image's RGB, byte-exactly.
/// </summary>
/// <remarks>
/// The arithmetic is on the stored 8-bit bytes, matching the engine, and it
/// clamps where the engine's shader does not — a gain or offset driving a
/// channel past full white is real behaviour an 8-bit image cannot carry, so it
/// is reported through <see cref="Clips"/> rather than silently wrapped. Only
/// RGB is touched: the shader discards the fourth component of the term, so the
/// alpha the image carries must survive untouched.
/// </remarks>
public static class ColorBake
{
    /// <summary>Returns a copy of <paramref name="image"/> with the adjustment baked in.</summary>
    public static RgbaImage Apply(RgbaImage image, ColorAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(image);

        byte[] pixels = image.Pixels.ToArray();
        Span<double> gain = [adjustment.Gain.R, adjustment.Gain.G, adjustment.Gain.B];
        Span<double> shift = [adjustment.Offset.R * 255.0, adjustment.Offset.G * 255.0, adjustment.Offset.B * 255.0];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                // Round half to even, matching the reference's round(), then
                // clamp: the round comes first so the clamp acts on the final
                // integer rather than on an intermediate.
                double adjusted = Math.Round((pixels[i + channel] * gain[channel]) + shift[channel], MidpointRounding.ToEven);
                pixels[i + channel] = (byte)Math.Clamp((int)adjusted, 0, 255);
            }
        }

        return new RgbaImage(image.Width, image.Height, pixels);
    }

    /// <summary>
    /// Whether baking <paramref name="adjustment"/> would clamp any channel.
    /// </summary>
    /// <remarks>
    /// Only the per-channel extrema are tested, matching the reference: the
    /// shape of the check is what decides whether the clip warning fires, so it
    /// reproduces that shape rather than scanning every pixel.
    /// </remarks>
    public static bool Clips(RgbaImage image, ColorAdjustment adjustment)
    {
        ArgumentNullException.ThrowIfNull(image);

        Span<double> gain = [adjustment.Gain.R, adjustment.Gain.G, adjustment.Gain.B];
        Span<double> shift = [adjustment.Offset.R * 255.0, adjustment.Offset.G * 255.0, adjustment.Offset.B * 255.0];

        Span<byte> low = [255, 255, 255];
        Span<byte> high = [0, 0, 0];
        ReadOnlySpan<byte> pixels = image.Pixels;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                byte value = pixels[i + channel];
                if (value < low[channel])
                {
                    low[channel] = value;
                }

                if (value > high[channel])
                {
                    high[channel] = value;
                }
            }
        }

        for (int channel = 0; channel < 3; channel++)
        {
            double atLow = (low[channel] * gain[channel]) + shift[channel];
            double atHigh = (high[channel] * gain[channel]) + shift[channel];
            if (atLow < 0.0 || atLow > 255.0 || atHigh < 0.0 || atHigh > 255.0)
            {
                return true;
            }
        }

        return false;
    }
}
