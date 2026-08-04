using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Core.Imaging;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;

namespace Perianth.Core.Materials;

/// <summary>
/// Turns decoded editordata plus a content source into reconstructed surfaces.
/// </summary>
/// <remarks>
/// <para>
/// This is the reconstruction layer the grammar deliberately left work for. It
/// selects material record 0, judges the shader family, requires the channels
/// that family needs, applies the runtime defaults when no custom record is
/// present, and resolves each texture through whichever source holds it.
/// </para>
/// <para>
/// It reconstructs the ordinary and transparent families, merges an emissive
/// companion onto its base, and resolves a transparent pair the sampler cannot
/// serve either by a near-boundary clamp substitution or by baking the repeat
/// and clamped alpha into one image over the region the part uses. A bake past
/// the size cap omits its one part rather than refusing the export.
/// </para>
/// </remarks>
public static class MaterialAssembler
{
    private const string OrdinaryShader = "CamelDefaultShader";
    private const string TransparentShader = "CamelDefaultShader_Trans";
    private const string EmissiveShader = "CamelDefaultShader_Emissive";

    private const string DiffuseChannel = "DiffuseColor";
    private const string TransparentChannel = "TransparentColor";
    private const string EmissiveChannel = "EmissiveColor";

    // An emissive companion's material name is its base's plus this suffix.
    private const string EmissiveSuffix = "__E";

    /// <summary>
    /// Builds the material set for <paramref name="model"/>.
    /// </summary>
    public static Result<MaterialSet> Assemble(
        GeometryModel model,
        EditordataFile editordata,
        ContentSources content)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(editordata);
        ArgumentNullException.ThrowIfNull(content);

        // The section ordinal is the association: a part's source ordinal names
        // its editordata section. The model handed here may be a posed subset, so
        // a part addresses its own section by ordinal rather than by position.
        // Nothing matches on a name or searches for a near miss.
        foreach (GeometryPart candidate in model.Parts)
        {
            if (candidate.SourceOrdinal < 0 || candidate.SourceOrdinal >= editordata.Sections.Length)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Model part {candidate.SourceOrdinal} has no editordata section among the {editordata.Sections.Length} declared."));
            }
        }

        EmissivePairing pairing = PairEmissiveCompanions(model, editordata);

        Build build = new();
        List<int> surviving = [];
        List<int> materialOfSurviving = [];

        for (int part = 0; part < model.Parts.Length; part++)
        {
            // An emissive companion's surface is not drawn: it merges onto a
            // base, or it matched none and is omitted. Either way its geometry
            // is dropped, so it never survives into the export.
            if (pairing.Dropped.Contains(part))
            {
                continue;
            }

            int ordinal = model.Parts[part].SourceOrdinal;
            EditordataSection section = editordata.Sections[ordinal];

            if (section.Materials.IsDefaultOrEmpty)
            {
                // A section authored with no material leaves its part untextured.
                surviving.Add(part);
                materialOfSurviving.Add(-1);
                continue;
            }

            EditordataMaterial record = section.Materials[0];

            Result<PartSurface> surface = BuildSurface(
                record, section, model.Parts[part], ordinal, content, build);

            if (!surface.TryGetValue(out PartSurface partSurface, out Refusal? refusal))
            {
                return refusal;
            }

            if (partSurface.Omitted)
            {
                // The one recoverable outcome in this path: reconciling the
                // repeat with the clamped alpha would bake an image past the size
                // cap. That costs this one part, not the export, so its geometry
                // is left out and the rest is unaffected.
                build.OversizedOmissions.Add(new OversizedOmission(ordinal, partSurface.OmitDetail!));
                continue;
            }

            SurfaceMaterial built = partSurface.Material!;

            if (partSurface.Remap is { } remap)
            {
                build.Remaps.Add(new BakedUv0(part, remap));
            }

            if (pairing.Merged.TryGetValue(part, out int companion))
            {
                Result<SurfaceMaterial> withEmissive = AttachEmissive(
                    built, model.Parts[companion].SourceOrdinal, editordata, content, build);
                if (!withEmissive.TryGetValue(out SurfaceMaterial? merged, out Refusal? emissiveRefusal))
                {
                    return emissiveRefusal;
                }

                built = merged;
            }

            surviving.Add(part);
            materialOfSurviving.Add(build.Materials.Count);
            build.Materials.Add(built);
        }

        build.OffsetOrdinals.Sort();
        build.ClipOrdinals.Sort();
        build.ClampOrdinals.Sort();
        build.BakeOrdinals.Sort();
        build.OversizedOmissions.Sort((a, b) => a.SourceOrdinal.CompareTo(b.SourceOrdinal));

        return Result.Ok(new MaterialSet(
            [.. build.Images],
            [.. build.Materials],
            [.. materialOfSurviving],
            [.. surviving],
            [.. build.OffsetOrdinals],
            [.. build.ClipOrdinals],
            [.. pairing.Merged.Values.Select(c => model.Parts[c].SourceOrdinal).Order()],
            [.. pairing.Unpaired.Select(c => model.Parts[c].SourceOrdinal).Order()],
            [.. build.ClampOrdinals],
            [.. build.BakeOrdinals],
            [.. build.Remaps],
            [.. build.OversizedOmissions]));
    }

    /// <summary>A reconstructed surface, its UV0 rewrite, or an omission.</summary>
    private readonly record struct PartSurface(
        SurfaceMaterial? Material,
        Uv0Remap? Remap,
        bool Omitted,
        string? OmitDetail);

    /// <summary>
    /// Pairs each emissive companion part with the base it merges onto.
    /// </summary>
    /// <remarks>
    /// A companion qualifies only when its name minus the <c>__E</c> suffix
    /// names exactly one base material, that base is not already a merge target,
    /// and the two parts have identical positions, indices, UV0 and hierarchy
    /// placement. Anything short of that is unpaired: the merge would move a
    /// surface's lighting onto geometry it does not actually cover. Every
    /// emissive part is dropped from drawing regardless of the outcome.
    /// </remarks>
    private static EmissivePairing PairEmissiveCompanions(GeometryModel model, EditordataFile editordata)
    {
        Dictionary<string, List<int>> basesByName = new(StringComparer.Ordinal);
        for (int part = 0; part < model.Parts.Length; part++)
        {
            EditordataSection section = editordata.Sections[model.Parts[part].SourceOrdinal];
            if (section.Materials.IsDefaultOrEmpty || section.Materials[0].Shader == EmissiveShader)
            {
                continue;
            }

            string name = section.Materials[0].Name;
            if (!basesByName.TryGetValue(name, out List<int>? ordinals))
            {
                ordinals = [];
                basesByName[name] = ordinals;
            }

            ordinals.Add(part);
        }

        Dictionary<int, int> merged = [];
        List<int> unpaired = [];
        HashSet<int> dropped = [];

        for (int part = 0; part < model.Parts.Length; part++)
        {
            EditordataSection section = editordata.Sections[model.Parts[part].SourceOrdinal];
            if (section.Materials.IsDefaultOrEmpty || section.Materials[0].Shader != EmissiveShader)
            {
                continue;
            }

            dropped.Add(part);

            string name = section.Materials[0].Name;
            int? baseOrdinal = null;
            if (name.EndsWith(EmissiveSuffix, StringComparison.Ordinal) &&
                basesByName.TryGetValue(name[..^EmissiveSuffix.Length], out List<int>? candidates) &&
                candidates.Count == 1)
            {
                baseOrdinal = candidates[0];
            }

            if (baseOrdinal is int b &&
                !merged.ContainsKey(b) &&
                GeometryIdentical(model.Parts[b], model.Parts[part]))
            {
                merged[b] = part;
            }
            else
            {
                unpaired.Add(part);
            }
        }

        return new EmissivePairing(merged, unpaired, dropped);
    }

    private static bool GeometryIdentical(GeometryPart a, GeometryPart b) =>
        a.Positions.AsSpan().SequenceEqual(b.Positions.AsSpan()) &&
        a.Indices.AsSpan().SequenceEqual(b.Indices.AsSpan()) &&
        a.Uv0.AsSpan().SequenceEqual(b.Uv0.AsSpan()) &&
        string.Equals(Placement(a.SourceLabel), Placement(b.SourceLabel), StringComparison.Ordinal);

    /// <summary>The hierarchy node a label attaches to: everything before its last pipe.</summary>
    private static string Placement(string label)
    {
        int pipe = label.LastIndexOf('|');
        return pipe < 0 ? string.Empty : label[..pipe];
    }

    private static Result<SurfaceMaterial> AttachEmissive(
        SurfaceMaterial baseSurface,
        int companionOrdinal,
        EditordataFile editordata,
        ContentSources content,
        Build build)
    {
        EditordataSection companion = editordata.Sections[companionOrdinal];
        EditordataMaterial record = companion.Materials[0];

        Result<string> emissivePath = ResolvePath(record, EmissiveChannel, companionOrdinal);
        if (!emissivePath.TryGetValue(out string? path, out Refusal? pathRefusal))
        {
            return pathRefusal;
        }

        // An emissive image is the raw EmissiveColor file, keyed apart from any
        // diffuse use of the same path and named so the export says which it is.
        string key = "E:" + path;
        int imageIndex;
        if (build.ImageIndexByKey.TryGetValue(key, out int existing))
        {
            imageIndex = existing;
        }
        else
        {
            Result<RgbaImage> decoded = LoadTexture(path, content);
            if (!decoded.TryGetValue(out RgbaImage? image, out Refusal? decodeRefusal))
            {
                return decodeRefusal;
            }

            string name = string.Create(CultureInfo.InvariantCulture, $"{path} (EmissiveColor)");
            imageIndex = build.Images.Count;
            build.Images.Add(new TextureImage(name, [.. PngEncoder.Encode(image)]));
            build.ImageIndexByKey[key] = imageIndex;
        }

        Rgb factor = companion.CustomRecords.IsDefaultOrEmpty
            ? new Rgb(0, 0, 0)
            : new Rgb(companion.CustomRecords[0].Slot60.X, companion.CustomRecords[0].Slot60.Y, companion.CustomRecords[0].Slot60.Z);

        return Result.Ok(baseSurface with { EmissiveImageIndex = imageIndex, EmissiveFactor = factor });
    }

    private readonly record struct EmissivePairing(
        Dictionary<int, int> Merged,
        List<int> Unpaired,
        HashSet<int> Dropped);

    private static Result<PartSurface> BuildSurface(
        EditordataMaterial record,
        EditordataSection section,
        GeometryPart part,
        int ordinal,
        ContentSources content,
        Build build)
    {
        if (record.Shader is not (OrdinaryShader or TransparentShader))
        {
            if (record.Shader == EmissiveShader)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Section {ordinal} is an emissive companion, whose merge is not yet reconstructed."));
            }

            string family = record.Shader.Length == 0 ? "<empty>" : record.Shader;
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Section {ordinal} uses unsupported shader family {family}."));
        }

        // Runtime defaults apply when the section carries no custom record.
        EditordataCustomRecord? custom = section.CustomRecords.IsDefaultOrEmpty
            ? null
            : section.CustomRecords[0];

        Rgb tint = custom is { Slot10: { } t } ? new Rgb(t.X, t.Y, t.Z) : new Rgb(1, 1, 1);
        Rgb gain = custom is { Slot30: { } g } ? new Rgb(g.X, g.Y, g.Z) : new Rgb(1, 1, 1);
        Rgb offset = custom is { Slot40: { } o } ? new Rgb(o.X, o.Y, o.Z) : new Rgb(0, 0, 0);
        double alphaFactor = custom?.Slot20.W ?? 1.0;
        ColorAdjustment adjustment = new(gain, offset);

        // myUVRepeat is present from custom-record version 2; a section with no
        // custom record samples at the identity scale.
        TextureScale scale = custom is { UvRepeat: { } repeat }
            ? new TextureScale(repeat.X, repeat.Y)
            : TextureScale.Identity;

        bool transparent = record.Shader == TransparentShader;

        Result<ComposedBase> baseResult = transparent
            ? ComposeBase(record, part, ordinal, scale, content, build)
            : OrdinaryBase(record, ordinal, content, build);

        if (!baseResult.TryGetValue(out ComposedBase composed, out Refusal? refusal))
        {
            return refusal;
        }

        if (composed.OversizedDetail is { } detail)
        {
            return Result.Ok(new PartSurface(null, null, Omitted: true, detail));
        }

        // A bake consumes myUVRepeat into the pixels and rewrites UV0, so the
        // material's own repeat is dropped: leaving it would apply the scale a
        // second time through a texture transform. A clamped composition keeps
        // its wrap; every other composition repeats.
        bool baked = composed.Remap is not null;
        TextureScale emittedScale = baked ? TextureScale.Identity : scale;
        TextureWrap wrap = composed.Clamp ? TextureWrap.ClampToEdge : TextureWrap.Repeat;

        if (composed.Clamp)
        {
            build.ClampOrdinals.Add(ordinal);
        }

        if (baked)
        {
            build.BakeOrdinals.Add(ordinal);
        }

        // The two mappings of the recovered shader's
        // `albedo.rgb * (diffuse.rgb * gain + offset)` are mutually exclusive.
        // With a default offset the gain folds into the factor and the image is
        // untouched; with a non-default offset the factor stays at the tint and
        // both are baked into the image. Gain is never applied twice.
        ColorRgba factor = adjustment.OffsetIsDefault
            ? new ColorRgba(tint.R * gain.R, tint.G * gain.G, tint.B * gain.B, alphaFactor)
            : new ColorRgba(tint.R, tint.G, tint.B, alphaFactor);

        int imageIndex = ResolveImage(composed, adjustment, ordinal, build);

        return Result.Ok(new PartSurface(
            new SurfaceMaterial(
                record.Name,
                imageIndex,
                factor,
                transparent,
                wrap,
                emittedScale),
            composed.Remap,
            Omitted: false,
            OmitDetail: null));
    }

    /// <summary>
    /// Applies the colour adjustment, records what it did, and shares the image.
    /// </summary>
    /// <remarks>
    /// The offset and clip ordinals are recorded per part, before the image
    /// dedup, so two parts sharing a texture and offset are both reported even
    /// though they share one image. The adjustment joins the image identity, so
    /// two parts with the same texture but different adjustments cannot share
    /// one image.
    /// </remarks>
    private static int ResolveImage(ComposedBase baseImage, ColorAdjustment adjustment, int ordinal, Build build)
    {
        bool bake = !adjustment.OffsetIsDefault;
        string fullKey = bake ? baseImage.ImageKey + AdjustmentSuffix(adjustment) : baseImage.ImageKey;

        if (bake)
        {
            if (ColorBake.Clips(baseImage.Image!, adjustment))
            {
                build.ClipOrdinals.Add(ordinal);
            }

            build.OffsetOrdinals.Add(ordinal);
        }

        if (build.ImageIndexByKey.TryGetValue(fullKey, out int existing))
        {
            return existing;
        }

        RgbaImage final = bake ? ColorBake.Apply(baseImage.Image!, adjustment) : baseImage.Image!;
        int index = build.Images.Count;
        build.Images.Add(new TextureImage(baseImage.Name, [.. PngEncoder.Encode(final)]));
        build.ImageIndexByKey[fullKey] = index;
        return index;
    }

    private static Result<ComposedBase> OrdinaryBase(
        EditordataMaterial record,
        int ordinal,
        ContentSources content,
        Build build)
    {
        Result<string> diffuse = ResolvePath(record, DiffuseChannel, ordinal);
        if (!diffuse.TryGetValue(out string? path, out Refusal? pathRefusal))
        {
            return pathRefusal;
        }

        // The prefix keeps an ordinary image distinct from a composition that
        // happens to share the diffuse path.
        string key = "O:" + path;
        if (build.BaseByKey.TryGetValue(key, out ComposedBase cached))
        {
            return Result.Ok(cached);
        }

        Result<RgbaImage> decoded = LoadTexture(path, content);
        if (!decoded.TryGetValue(out RgbaImage? image, out Refusal? decodeRefusal))
        {
            return decodeRefusal;
        }

        ComposedBase result = new(image, key, path, Clamp: false, Remap: null, OversizedDetail: null);
        build.BaseByKey[key] = result;
        return Result.Ok(result);
    }

    private static Result<ComposedBase> ComposeBase(
        EditordataMaterial record,
        GeometryPart part,
        int ordinal,
        TextureScale scale,
        ContentSources content,
        Build build)
    {
        Result<string> diffusePath = ResolvePath(record, DiffuseChannel, ordinal);
        if (!diffusePath.TryGetValue(out string? diffuse, out Refusal? diffuseRefusal))
        {
            return diffuseRefusal;
        }

        Result<string> transparentPath = ResolvePath(record, TransparentChannel, ordinal);
        if (!transparentPath.TryGetValue(out string? alpha, out Refusal? alphaRefusal))
        {
            return alphaRefusal;
        }

        bool uv0InRange = Uv0WithinUnitRange(part.Uv0);
        Uv0Extent extent = ExtentOf(part.Uv0);

        // The extent joins the composition-cache key exactly where it can change
        // the result: a near-boundary decision and a bake both depend on how far
        // this part's own UV0 reaches. Where neither applies, an ordinary
        // character still composes a handful of images rather than one per part.
        bool extentMatters = !uv0InRange || !scale.IsIdentity;
        string sourceKey = string.Create(
            CultureInfo.InvariantCulture,
            $"C:{diffuse}\0{alpha}\0{uv0InRange}\0{scale.U},{scale.V}\0{(extentMatters ? extent.ToString() : "-")}");
        if (build.BaseByKey.TryGetValue(sourceKey, out ComposedBase cached))
        {
            return Result.Ok(cached);
        }

        Result<RgbaImage> diffuseImage = LoadTexture(diffuse, content);
        if (!diffuseImage.TryGetValue(out RgbaImage? diffuseRgba, out Refusal? d))
        {
            return d;
        }

        Result<RgbaImage> alphaImage = LoadTexture(alpha, content);
        if (!alphaImage.TryGetValue(out RgbaImage? alphaRgba, out Refusal? a))
        {
            return a;
        }

        Result<ComposedTexture> attempt = TextureComposition.Compose(
            diffuseRgba, alphaRgba, uv0InRange, scale.U, scale.V, extent);
        if (!attempt.TryGetValue(out ComposedTexture composition, out Refusal? composeRefusal))
        {
            return composeRefusal;
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"{diffuse} + {alpha}.a");

        if (composition.Oversized)
        {
            // Not cached: like the reference's BakeTooLarge, it is a per-part
            // omission rather than a shared result. The detail mirrors what the
            // bake would have been, for the omission report.
            string detail = string.Create(
                CultureInfo.InvariantCulture,
                $"combining DiffuseColor {diffuse} and TransparentColor {alpha} would bake {composition.TilesU}x{composition.TilesV} tiles into a {composition.BakedWidth}x{composition.BakedHeight} image");
            return Result.Ok(new ComposedBase(null, sourceKey, name, Clamp: false, Remap: null, detail));
        }

        // The final image identity carries the wrap and the bake bounds, so a
        // clamped and a repeated use of one pair, or two bakes over different
        // regions, do not collide on one image.
        string imageKey = string.Create(
            CultureInfo.InvariantCulture,
            $"C:{diffuse}\0{alpha}\0W{(composition.Clamp ? "C" : "R")}\0I{composition.Identity}");

        ComposedBase result = new(
            composition.Image,
            imageKey,
            name,
            composition.Clamp,
            composition.Remap,
            OversizedDetail: null);
        build.BaseByKey[sourceKey] = result;
        return Result.Ok(result);
    }

    /// <summary>The axis-aligned UV0 bounds, or the unit square when there is no UV0.</summary>
    private static Uv0Extent ExtentOf(System.Collections.Immutable.ImmutableArray<Vector2D> uv0)
    {
        if (uv0.IsDefaultOrEmpty)
        {
            return Uv0Extent.Unit;
        }

        double uMin = uv0[0].X, uMax = uv0[0].X, vMin = uv0[0].Y, vMax = uv0[0].Y;
        foreach (Vector2D coordinate in uv0)
        {
            uMin = Math.Min(uMin, coordinate.X);
            uMax = Math.Max(uMax, coordinate.X);
            vMin = Math.Min(vMin, coordinate.Y);
            vMax = Math.Max(vMax, coordinate.Y);
        }

        return new Uv0Extent(uMin, uMax, vMin, vMax);
    }

    /// <summary>A gain-and-offset key fragment that distinguishes baked images.</summary>
    private static string AdjustmentSuffix(ColorAdjustment adjustment) => string.Create(
        CultureInfo.InvariantCulture,
        $":G{adjustment.Gain.R},{adjustment.Gain.G},{adjustment.Gain.B};O{adjustment.Offset.R},{adjustment.Offset.G},{adjustment.Offset.B}");

    /// <summary>A composed base image with its sampler and coordinate consequences.</summary>
    private readonly record struct ComposedBase(
        RgbaImage? Image,
        string ImageKey,
        string Name,
        bool Clamp,
        Uv0Remap? Remap,
        string? OversizedDetail);

    private sealed class Build
    {
        public List<TextureImage> Images { get; } = [];

        public List<SurfaceMaterial> Materials { get; } = [];

        public List<int> OffsetOrdinals { get; } = [];

        public List<int> ClipOrdinals { get; } = [];

        public List<int> ClampOrdinals { get; } = [];

        public List<int> BakeOrdinals { get; } = [];

        public List<BakedUv0> Remaps { get; } = [];

        public List<OversizedOmission> OversizedOmissions { get; } = [];

        public Dictionary<string, int> ImageIndexByKey { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ComposedBase> BaseByKey { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether every UV0 coordinate lies inside the normalized range.
    /// </summary>
    /// <remarks>
    /// The tolerance matches the reference: a coordinate is inside when it lies
    /// within <c>[-1e-6, 1.000001]</c>. Outside it the engine's repeat and clamp
    /// wrap states select different texels, so one combined image cannot stand
    /// in for both channels.
    /// </remarks>
    private static bool Uv0WithinUnitRange(System.Collections.Immutable.ImmutableArray<Vector2D> uv0)
    {
        foreach (Vector2D coordinate in uv0)
        {
            if (coordinate.X is < -1.0e-6 or > 1.000001 ||
                coordinate.Y is < -1.0e-6 or > 1.000001)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the single non-empty texture a channel binds and normalizes it.
    /// </summary>
    private static Result<string> ResolvePath(EditordataMaterial record, string channel, int ordinal)
    {
        string? found = null;
        int count = 0;

        foreach (EditordataChannel bound in record.Channels)
        {
            if (!string.Equals(bound.Channel, channel, StringComparison.Ordinal) ||
                bound.TexturePath.Length == 0)
            {
                continue;
            }

            found = bound.TexturePath;
            count++;
        }

        if (count != 1 || found is null)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Section {ordinal} material record 0 does not contain exactly one {channel} texture."));
        }

        return TexturePath.Normalize(found, channel);
    }

    private static Result<RgbaImage> LoadTexture(string path, ContentSources content)
    {
        Result<byte[]?> read = content.Read(path);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            // A texture the editordata names but no source holds. Missing
            // texture bytes refuse; this is not the absence the precedence
            // rule tolerates, because every source has now been asked.
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"The texture {path} was not found in any supplied content source."),
                DiagnosticIds.ResourceMissing);
        }

        Result<DdsImage> decoded = DdsReader.Read(bytes);
        if (!decoded.TryGetValue(out DdsImage? image, out Refusal? decodeRefusal))
        {
            return decodeRefusal;
        }

        return Result.Ok(new RgbaImage(image.Width, image.Height, image.Pixels.ToArray()));
    }
}
