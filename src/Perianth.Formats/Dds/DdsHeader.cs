namespace Perianth.Formats.Dds;

/// <summary>
/// What a DDS header said, without decoding any texels.
/// </summary>
/// <param name="Width">Width in texels.</param>
/// <param name="Height">Height in texels.</param>
/// <param name="Format">The block-compressed format the pixel-format block named.</param>
/// <param name="MipMapCount">
/// Levels the header declares. Recorded because the file says it, not because
/// anything reads past level zero — textures here declare seven to ten.
/// </param>
/// <param name="PayloadLength">Bytes of level-zero compressed data.</param>
public readonly record struct DdsHeader(
    int Width,
    int Height,
    DdsFormat Format,
    int MipMapCount,
    int PayloadLength);
