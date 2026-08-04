using System.Collections.Immutable;
using Perianth.Core.Imaging;

namespace Perianth.Core.Materials;

/// <summary>
/// How a texture's coordinates behave outside the unit square.
/// </summary>
/// <remarks>
/// A property of the engine's sampler, not a glTF enum. The writer maps these
/// onto glTF numbers; nothing here knows what those numbers are.
/// </remarks>
public enum TextureWrap
{
    /// <summary>Coordinates tile.</summary>
    Repeat,

    /// <summary>Coordinates hold the edge texel.</summary>
    ClampToEdge,
}

/// <summary>
/// One encoded image and the identity that decided it could be shared.
/// </summary>
/// <param name="Name">
/// What the image is, in source terms: a texture's virtual path, or a
/// description of how several were combined. The reference uses this as the
/// glTF image name, and the harness compares it, so it is content rather than
/// decoration.
/// </param>
/// <param name="Png">The encoded bytes.</param>
public sealed record TextureImage(string Name, ImmutableArray<byte> Png);

/// <summary>
/// A reconstructed surface, in source terms.
/// </summary>
/// <remarks>
/// Deliberately free of glTF vocabulary. There is no <c>alphaMode</c> string,
/// no extension name and no encoded-image field standing in for a decoded one:
/// the Python material record held glTF terms and encoded PNG bytes, and that
/// is exactly what would force a future importer to decode a second time.
/// </remarks>
/// <param name="Name">The material name the editordata carried.</param>
/// <param name="ImageIndex">Which image this surface samples, or null when it has none.</param>
/// <param name="BaseColorFactor">
/// The constant the shader multiplies the sampled colour by: RGB is the albedo
/// tint combined with the observed colour gain, and W is the constant alpha.
/// </param>
/// <param name="IsTransparent">
/// Whether the source family blends. Derived from the shader family, not from
/// whether any alpha byte happens to be less than 255.
/// </param>
/// <param name="Wrap">How the sampler treats coordinates outside the unit square.</param>
/// <param name="Scale">
/// The engine's <c>myUVRepeat</c>: the diffuse channel is sampled at
/// <c>uv * repeat</c>. Source vocabulary, not a glTF term — the writer decides
/// what a non-identity value becomes. Identity when the material has no repeat.
/// </param>
/// <param name="EmissiveImageIndex">
/// The image index of a merged emissive companion's texture, or null when this
/// surface has no companion. Indexes the same image list as <paramref name="ImageIndex"/>.
/// </param>
/// <param name="EmissiveFactor">
/// The companion's <c>slot_60.rgb</c>, the runtime-proven emissive factor. Null
/// unless a companion was merged.
/// </param>
public sealed record SurfaceMaterial(
    string Name,
    int? ImageIndex,
    ColorRgba BaseColorFactor,
    bool IsTransparent,
    TextureWrap Wrap,
    TextureScale Scale,
    int? EmissiveImageIndex = null,
    Rgb? EmissiveFactor = null);

/// <summary>
/// A tile bake's UV0 rewrite for one source part.
/// </summary>
/// <remarks>
/// A bake consumes <c>myUVRepeat</c> into the pixels, so the part's own
/// coordinates must be rewritten to address the baked region. Keyed by source
/// ordinal because the caller applies it to the full geometry, whose parts are
/// in source order, before dropping any.
/// </remarks>
public readonly record struct BakedUv0(int SourceOrdinal, Uv0Remap Remap);

/// <summary>
/// A part omitted because its required bake exceeds the size cap.
/// </summary>
/// <param name="SourceOrdinal">The source ordinal whose geometry is absent.</param>
/// <param name="Detail">
/// What the bake would have been, for the omission report: the paths and the
/// tile and image dimensions it needed.
/// </param>
public readonly record struct OversizedOmission(int SourceOrdinal, string Detail);

/// <summary>Four colour components, unclamped, in the order R, G, B, A.</summary>
public readonly record struct ColorRgba(double R, double G, double B, double A)
{
    /// <summary>Opaque white, the value when nothing modifies the surface.</summary>
    public static ColorRgba White => new(1, 1, 1, 1);
}

/// <summary>
/// A per-axis texture sampling scale, the engine's <c>myUVRepeat</c>.
/// </summary>
/// <remarks>
/// Carried unclamped, including zero and negative values: for an opaque or
/// constant-alpha surface the repeat is reproduced verbatim, since it has been
/// shown to affect the sampled colour independently of the coordinates.
/// </remarks>
public readonly record struct TextureScale(double U, double V)
{
    /// <summary>The no-op scale, sampling at the raw coordinates.</summary>
    public static TextureScale Identity => new(1, 1);

    /// <summary>Whether this scale changes nothing and needs no transform.</summary>
    public bool IsIdentity => U == 1 && V == 1;
}

/// <summary>
/// The materials an export resolved, and which part each one dresses.
/// </summary>
/// <param name="Images">Encoded images, shared wherever identity allowed.</param>
/// <param name="Materials">The surfaces, one per part that has one.</param>
/// <param name="MaterialOfPart">
/// For each <em>surviving</em> part in <see cref="SurvivingParts"/> order, the
/// index into <see cref="Materials"/>, or -1 when the part is untextured.
/// </param>
/// <param name="SurvivingParts">
/// The source ordinals that survive into the export, in order. Emissive
/// companion parts are absent: their surface is not drawn, so their geometry is
/// dropped. Empty when no materials were assembled, which the caller reads as
/// "keep every part".
/// </param>
/// <param name="OffsetBakedParts">
/// Source ordinals whose non-default colour offset was baked into the emitted
/// image, sorted. The export reports these, because glTF has no base-colour
/// offset field and the bake is an approximation of an engine constant.
/// </param>
/// <param name="ClippedParts">
/// Source ordinals where baking the gain and offset drove a channel outside
/// 0..255 and it was clamped, sorted. Reported because the shader applies no
/// saturation, so the clamp is a limit of the 8-bit image, not a correction.
/// </param>
/// <param name="MergedCompanions">
/// Source ordinals of emissive companions merged onto a base, sorted. Reported
/// because the merge approximates an additive pass core glTF cannot express.
/// </param>
/// <param name="UnpairedCompanions">
/// Source ordinals of emissive companions that matched no base and were omitted
/// rather than drawn as an occluding coincident surface, sorted.
/// </param>
/// <param name="ClampedParts">
/// Source ordinals whose combined texture is sampled clamped rather than
/// repeated, a substitution admitted only where the diffuse's crossed edges
/// agree, sorted. Reported because it reproduces the engine's clamped alpha.
/// </param>
/// <param name="BakedParts">
/// Source ordinals whose DiffuseColor repeat and clamped TransparentColor alpha
/// were resolved into one baked image over the region they use, sorted.
/// </param>
/// <param name="Uv0Remaps">
/// The UV0 rewrite each baked part needs, keyed by source ordinal. Applied to
/// the geometry before the surviving-parts selection.
/// </param>
/// <param name="OversizedOmissions">
/// Parts omitted because their required bake exceeds the size cap. Each costs
/// one part rather than the export, which is therefore a partial export.
/// </param>
public sealed record MaterialSet(
    ImmutableArray<TextureImage> Images,
    ImmutableArray<SurfaceMaterial> Materials,
    ImmutableArray<int> MaterialOfPart,
    ImmutableArray<int> SurvivingParts,
    ImmutableArray<int> OffsetBakedParts,
    ImmutableArray<int> ClippedParts,
    ImmutableArray<int> MergedCompanions,
    ImmutableArray<int> UnpairedCompanions,
    ImmutableArray<int> ClampedParts,
    ImmutableArray<int> BakedParts,
    ImmutableArray<BakedUv0> Uv0Remaps,
    ImmutableArray<OversizedOmission> OversizedOmissions)
{
    /// <summary>A set that dresses nothing, for an untextured export.</summary>
    public static MaterialSet Empty { get; } = new([], [], [], [], [], [], [], [], [], [], [], []);

    /// <summary>Whether any part carries a material.</summary>
    public bool IsEmpty => Materials.IsDefaultOrEmpty;

    /// <summary>Whether any reconstructed surface blends.</summary>
    public bool HasTransparent
    {
        get
        {
            if (Materials.IsDefaultOrEmpty)
            {
                return false;
            }

            foreach (SurfaceMaterial material in Materials)
            {
                if (material.IsTransparent)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
