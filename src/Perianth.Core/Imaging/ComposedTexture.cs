namespace Perianth.Core.Imaging;

/// <summary>
/// One combined image together with how it must be sampled and addressed.
/// </summary>
/// <remarks>
/// <para>
/// The engine samples DiffuseColor repeated for RGB and TransparentColor
/// clamped for alpha. One glTF image under one sampler can only reproduce that
/// pair by choosing a wrap state and, where no wrap state serves, baking each
/// channel's own rule into the pixels. The wrap and the bake's coordinate
/// rewrite are therefore properties of the <em>result</em>, not of the paths: the
/// same two textures compose differently for two parts whose UV0 differs.
/// </para>
/// <para>
/// A bake past the size cap is not a refusal. It costs exactly the one part that
/// needs it, so it travels as an <see cref="Oversized"/> outcome with the tile
/// and image dimensions the caller reports, rather than throwing.
/// </para>
/// </remarks>
public readonly record struct ComposedTexture
{
    private ComposedTexture(
        RgbaImage? image,
        bool clamp,
        Uv0Remap? remap,
        string identity,
        bool oversized,
        int tilesU,
        int tilesV,
        int bakedWidth,
        int bakedHeight)
    {
        Image = image;
        Clamp = clamp;
        Remap = remap;
        Identity = identity;
        Oversized = oversized;
        TilesU = tilesU;
        TilesV = tilesV;
        BakedWidth = bakedWidth;
        BakedHeight = bakedHeight;
    }

    /// <summary>The combined image, or null when the bake was <see cref="Oversized"/>.</summary>
    public RgbaImage? Image { get; }

    /// <summary>Whether the sampler must clamp rather than repeat.</summary>
    public bool Clamp { get; }

    /// <summary>The UV0 rewrite a bake produced, or null when none.</summary>
    public Uv0Remap? Remap { get; }

    /// <summary>
    /// What distinguishes this image beyond its source paths and wrap: empty for
    /// an ordinary composition, and the repeat, snapped bounds and source
    /// dimensions for a bake, so two bakes of one pair that differ in any of
    /// those cannot share an image.
    /// </summary>
    public string Identity { get; }

    /// <summary>Whether the required bake exceeds the size cap and this part must be omitted.</summary>
    public bool Oversized { get; }

    /// <summary>Diffuse tiles across, for the oversized-omission report.</summary>
    public int TilesU { get; }

    /// <summary>Diffuse tiles down, for the oversized-omission report.</summary>
    public int TilesV { get; }

    /// <summary>The image width the bake would have produced, gutter included.</summary>
    public int BakedWidth { get; }

    /// <summary>The image height the bake would have produced, gutter included.</summary>
    public int BakedHeight { get; }

    /// <summary>Whether this outcome carries a usable image.</summary>
    public bool HasImage => Image is not null;

    /// <summary>An ordinary or near-boundary composition, sampled repeat or clamp.</summary>
    public static ComposedTexture Combined(RgbaImage image, bool clamp) =>
        new(image, clamp, null, string.Empty, false, 0, 0, 0, 0);

    /// <summary>A tile-baked composition, with its coordinate rewrite and identity.</summary>
    public static ComposedTexture Baked(RgbaImage image, Uv0Remap remap, string identity) =>
        new(image, false, remap, identity, false, 0, 0, 0, 0);

    /// <summary>A bake that would exceed the size cap, so its part is omitted.</summary>
    public static ComposedTexture OversizedBake(int tilesU, int tilesV, int bakedWidth, int bakedHeight) =>
        new(null, false, null, string.Empty, true, tilesU, tilesV, bakedWidth, bakedHeight);
}
