namespace Perianth.Formats.Dds;

/// <summary>
/// The pixel formats this build decodes.
/// </summary>
/// <remarks>
/// <para>
/// Three block-compressed, and one uncompressed. Across the 47,321 DDS files in
/// the archives the formats present are DXT1 (57.1%), BC7_UNORM (41.4%), DXT5
/// (1.5%), nine uncompressed files at 32, 24, 16 and 8 bits per pixel, and a
/// single DXT3. Every other pixel format refuses by name rather than being
/// decoded on the chance that it appears.
/// </para>
/// <para>
/// <see cref="Uncompressed32"/> was added deliberately, reversing an earlier
/// decision to refuse every uncompressed texture; see <see cref="DdsReader"/>
/// for why and what it is bounded to. The 24, 16 and 8 bit files still refuse:
/// one of each exists, all are engine textures, and nothing reads them.
/// </para>
/// </remarks>
public enum DdsFormat
{
    /// <summary>Legacy FourCC <c>DXT1</c>: four-bit-per-texel colour, one-bit punch-through alpha.</summary>
    Bc1,

    /// <summary>Legacy FourCC <c>DXT5</c>: BC1 colour with an interpolated eight-bit alpha block.</summary>
    Bc3,

    /// <summary>DX10 <c>DXGI_FORMAT_BC7_UNORM</c> (98).</summary>
    Bc7,

    /// <summary>Uncompressed 32bpp with an alpha channel, in either BGRA or RGBA order.</summary>
    Uncompressed32,
}
