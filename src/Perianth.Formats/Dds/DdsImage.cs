using System;

namespace Perianth.Formats.Dds;

/// <summary>
/// One decoded texture: straight-alpha, row-major RGBA8, mip level zero.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately offers no way to resize, resample or scale itself.
/// The bake is a byte-exact operation — tile replication, one-tile bakes,
/// gutters and nearest-neighbour enlargement are exact because interpolating
/// them would be <em>wrong</em>, not because the reference happened to avoid
/// it. Making a resampler unreachable from the type the bake path holds is
/// cheaper than a rule saying not to call one, and it is cheapest now, while
/// there is one producer and no consumers.
/// </para>
/// <para>
/// Alpha is straight, never premultiplied. Only mip level zero is decoded;
/// headers in this corpus declare seven to ten levels and the exporter reads
/// none of them.
/// </para>
/// </remarks>
public sealed class DdsImage
{
    private readonly byte[] _pixels;

    internal DdsImage(int width, int height, DdsFormat format, byte[] pixels)
    {
        Width = width;
        Height = height;
        Format = format;
        _pixels = pixels;
    }

    /// <summary>Width in texels.</summary>
    public int Width { get; }

    /// <summary>Height in texels.</summary>
    public int Height { get; }

    /// <summary>The compressed format these pixels were decoded from.</summary>
    public DdsFormat Format { get; }

    /// <summary>
    /// The decoded texels, four bytes each in R, G, B, A order, row-major from
    /// the top-left. Exposed as a read-only span so the buffer cannot be
    /// mutated behind the image's back.
    /// </summary>
    public ReadOnlySpan<byte> Pixels => _pixels;
}
