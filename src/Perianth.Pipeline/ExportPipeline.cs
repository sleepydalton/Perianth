using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Core;
using Perianth.Core.Audio;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Core.Materials;
using Perianth.Core.Pose;
using Perianth.Core.Io;
using Perianth.Formats.Anim;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Formats.Lipsync;
using Perianth.Formats.Mmb;
using Perianth.Gltf;

namespace Perianth.Pipeline;

/// <summary>
/// Reads the inputs, resolves the geometry, writes the GLB, and publishes it.
/// </summary>
/// <remarks>
/// This sits in the command-line tool rather than the core because the core
/// cannot reference the glTF writer — the dependency points the other way, and
/// keeping it that way is what stops glTF terms leaking into the model. When the
/// graphical front end arrives it will need the same sequence, and that is the
/// point to give the core a writer it can call rather than inventing an
/// abstraction now for a second caller that does not exist.
/// </remarks>
public static class ExportPipeline
{
    /// <summary>
    /// The warning an unposed export carries, reproduced exactly.
    /// </summary>
    /// <remarks>
    /// The wording is compared verbatim against the frozen reference, so it is
    /// not a message to improve. It exists because an unposed export is the
    /// model's whole part list: every alternate state the setup would have
    /// chosen between is present at once.
    /// </remarks>
    private const string UnposedWarning =
        "no --setup-anim was given, so this export is the model's complete part list rather than an " +
        "appearance: no hierarchy places the parts, and no visibility selection is applied, so every " +
        "alternate state the setup would have chosen between is present at once. Characters exported " +
        "this way show several poses, facings and prop variants overlaid. The glTF scene is named " +
        "'unposed-all-parts' so the file says so on its own. Parts emitted: ";

    /// <summary>
    /// The disclosure every reconstructed material carries, reproduced exactly.
    /// </summary>
    /// <remarks>
    /// Compared verbatim against the frozen reference, so it is not a message to
    /// improve. It is emitted once whenever any material is reconstructed,
    /// naming what the recovered shader does and the several inputs it does not
    /// reproduce, so a viewer of the export knows the surface is an
    /// approximation rather than the engine's own result.
    /// </remarks>
    private const string MaterialWarning =
        "runtime alpha-test and per-material two-sided overrides are not reconstructed; assembled Camel " +
        "model parts are emitted double-sided to preserve mirrored planar winding; baseColorFactor.rgb " +
        "reproduces the editordata custom record's myConstAlbedoColor.rgb tint (default white when no " +
        "custom record is present) combined with the observed slot_30 colour gain, and the observed " +
        "slot_40 colour offset is baked into the emitted image where it is non-default; the recovered " +
        "shader applies these as albedo * (diffuse * gain + offset) and discards the fourth component of " +
        "that term, so neither slot's .w is used. The additive term also carries a SpecularColor sample, " +
        "black on 99.65% of materials and not reproduced where it is not, and the gain is scaled by the " +
        "NormalMap colour ramp, which is the white placeholder on 98.6% of materials and not reproduced " +
        "where it is not. myConstAlbedoColor.w, myConstAmbientColor, the runtime myTint path, and further " +
        "runtime overrides are not reconstructed";

    /// <summary>
    /// The extra disclosure a transparent material carries, reproduced exactly.
    /// </summary>
    /// <remarks>
    /// Emitted once when any reconstructed surface blends, alongside the general
    /// material warning. It names what the alpha reproduces and the later
    /// serialized and runtime effects it does not.
    /// </remarks>
    private const string TransparentWarning =
        "baseColorFactor.a reproduces the editordata custom record's myConstAlpha.w opacity (default 1 " +
        "when no custom record is present) alongside the two proven texture contributions; " +
        "serialized/runtime overrides and later grid, curve, vanishing, and outline effects are not " +
        "reconstructed";

    /// <summary>
    /// The prefix of the colour-offset disclosure, followed by the section list.
    /// </summary>
    private const string ColourOffsetWarning =
        "the observed slot_40 colour offset is non-default on part(s) below, so it has been baked into " +
        "their emitted image: glTF has no base-colour offset field to carry an additive constant, and a " +
        "pure gain would have folded into baseColorFactor instead. Offset section(s): ";

    /// <summary>
    /// The prefix of the clip disclosure, followed by the section list.
    /// </summary>
    private const string ColourClipWarning =
        "baking the observed colour gain and offset drove a channel outside 0-255 on part(s) below and " +
        "it has been clamped. The recovered shader applies no saturation at this point, so the engine's " +
        "own value is out of range for an 8-bit image rather than the clamp being a correction. Clipped " +
        "section(s): ";

    /// <summary>
    /// The prefix of the clamp disclosure, followed by the section list.
    /// </summary>
    private const string ClampedWarning =
        "part(s) whose UV0 leaves the normalized range by under half a source texel carry their combined " +
        "texture clamped rather than repeated, which reproduces the engine's clamped TransparentColor " +
        "alpha exactly; each was admitted only after its DiffuseColor was proven identical across the " +
        "edges its UV0 crosses, so the colour is unchanged. Clamped section(s): ";

    /// <summary>
    /// The prefix of the bake disclosure, followed by the section list.
    /// </summary>
    private const string BakedWarning =
        "part(s) whose DiffuseColor repeats across coordinates its TransparentColor samples clamped carry " +
        "a baked texture: the repeated colour and the clamped alpha are evaluated into one image over the " +
        "region the part uses, its UV0 is rewritten to address that image, and no texture transform " +
        "accompanies it. The colour is copied tile by tile rather than resampled; the alpha is resampled " +
        "onto the baked grid as any differing-dimension composition is. Baked section(s): ";

    /// <summary>
    /// The prefix of the oversized-omission disclosure, followed by per-section detail.
    /// </summary>
    private const string OversizedWarning =
        "part(s) omitted: reconciling their DiffuseColor repeat with their clamped TransparentColor alpha " +
        "would require a baked image past this exporter's size cap. The rest of the export is unaffected, " +
        "and nothing wrong has been emitted in their place -- the geometry is simply absent. Omitted: ";

    /// <summary>
    /// The prefix of the emissive-merge disclosure, followed by the section list.
    /// </summary>
    private const string EmissiveWarning =
        "Approximate emissive material: Snowdrop ONE+ONE additive framebuffer blending is not " +
        "representable in core glTF. The emissive companion has been merged into its matching base " +
        "material as a self-lit surface, so its interaction with surfaces behind it may differ. Camel " +
        "QuadraticPS curve coverage is not reproduced here or anywhere, deliberately: see README.md. " +
        "Merged companion section(s): ";

    /// <summary>
    /// The prefix of the unpaired-companion disclosure, followed by the section list.
    /// </summary>
    private const string EmissiveUnpairedWarning =
        "emissive companion section(s) could not be matched to a base part with identical geometry and " +
        "were omitted rather than drawn as a separate surface, which would occlude whatever they " +
        "accompany: ";

    /// <summary>
    /// The disclosure a mouth atlas carries when materials were reconstructed,
    /// reproduced exactly.
    /// </summary>
    /// <remarks>
    /// Compared verbatim against the frozen reference. The runtime swaps a
    /// character's mouth material as it speaks; the exporter keeps each facial
    /// part's own source-ordinal editordata material instead, so this names what
    /// the surface does not reproduce.
    /// </remarks>
    private const string FacialMaterialWarning =
        "runtime facial material swapping is not reconstructed; facial parts retain " +
        "their source-ordinal editordata material";

    /// <summary>
    /// The prefix of the unrigged-omission disclosure, followed by the part names.
    /// </summary>
    private const string UnriggedWarning =
        "part(s) omitted: the setup hierarchy declares no node of their name, so they carry geometry but " +
        "no placement and there is nowhere to attach them. Nothing has been emitted in their place. If " +
        "this names most of the model rather than a few extras, the setup file probably does not belong " +
        "to it. Omitted part(s): ";

    public static Result<ExportOutcome> Run(ExportRequest arguments)
    {
        Result<SourceFile> mmbFile = SourceFileReader.Read(arguments.Mmb);
        if (!mmbFile.IsSuccess)
        {
            return mmbFile.Refusal;
        }

        Result<MmbModel> model = MmbReader.Read(mmbFile.Value);
        if (!model.IsSuccess)
        {
            return model.Refusal;
        }

        Result<SourceFile> cameldataFile = SourceFileReader.Read(arguments.Cameldata);
        if (!cameldataFile.IsSuccess)
        {
            return cameldataFile.Refusal;
        }

        Result<CameldataFile> cameldata = CameldataReader.Read(cameldataFile.Value);
        if (!cameldata.IsSuccess)
        {
            return cameldata.Refusal;
        }

        Result<GeometryModel> geometry = GeometryAssembler.Assemble(model.Value, cameldata.Value);
        if (!geometry.IsSuccess)
        {
            return geometry.Refusal;
        }

        // The pose comes before materials: the reference dresses only the parts a
        // setup hierarchy places and its visibility selects, so a hidden part is
        // never reconstructed and never reported. Without a setup the posed model
        // is the whole part list.
        Result<PoseResult> pose = ApplyPose(arguments, geometry.Value);
        if (!pose.TryGetValue(out PoseResult posed, out Refusal? poseRefusal))
        {
            return poseRefusal;
        }

        Result<MaterialSet> materials = AssembleMaterials(arguments, posed.PosedModel, geometry.Value.Parts.Length);
        if (!materials.IsSuccess)
        {
            return materials.Refusal;
        }

        // A tile bake consumes myUVRepeat into the pixels and rewrites the UV0 of
        // the parts that carry it, applied before any part is dropped so the keys
        // line up.
        GeometryModel remapped = ApplyUv0Remaps(posed.PosedModel, materials.Value.Uv0Remaps);

        // Emissive companions merge onto a base and their geometry is dropped, as
        // is any part whose bake exceeded the size cap; the surviving-parts view is
        // what ships. Their attachment nodes stay in the hierarchy without a mesh.
        ImmutableArray<int> kept = arguments.Editordata is null
            ? Identity(posed.PosedModel.Parts.Length)
            : materials.Value.SurvivingParts;
        GeometryModel drawn = remapped.SelectParts(kept);
        SceneGraph? graph = posed.Graph?.RemapMeshes(kept, posed.PosedModel.Parts.Length);

        Result<byte[]> glb = GlbWriter.Write(
            drawn,
            materials.Value,
            new GlbWriteOptions
            {
                IncludePresentationBasis = !arguments.SourceSpace,
                SceneName = graph is null ? GlbNames.UnposedScene : GlbNames.PosedScene,
                SceneGraph = graph,
                Animation = posed.Animation,
            });
        if (!glb.IsSuccess)
        {
            return glb.Refusal;
        }

        // The audio sidecar is resolved and decoded before either file is
        // published, so a failed decode leaves both the GLB and the WAV absent.
        Result<PreparedAudio> audioResult = PrepareAudio(arguments);
        if (!audioResult.TryGetValue(out PreparedAudio prepared, out Refusal? audioRefusal))
        {
            return audioRefusal;
        }

        if (prepared.Report is not null)
        {
            Result<int> wav = AtomicFile.Publish(prepared.Report.Output, prepared.Bytes!);
            if (!wav.IsSuccess)
            {
                return wav.Refusal;
            }
        }

        Result<int> published = AtomicFile.Publish(arguments.Out, glb.Value);
        if (!published.IsSuccess)
        {
            return published.Refusal;
        }

        return Result.Ok(new ExportOutcome(
            Count(drawn),
            Diagnose(drawn, materials.Value, graph is not null, posed.UnriggedParts, arguments.MouthAnim is not null),
            PartialExport: !materials.Value.OversizedOmissions.IsDefaultOrEmpty,
            Audio: prepared.Report));
    }

    /// <summary>The audio report a caller sees, paired with the WAV bytes to publish.</summary>
    private readonly record struct PreparedAudio(AudioReport? Report, byte[]? Bytes);

    /// <summary>
    /// Resolves and decodes the speech WEM into a report and its bytes, or nothing
    /// when no <c>--wem-root</c> was given.
    /// </summary>
    private static Result<PreparedAudio> PrepareAudio(ExportRequest arguments)
    {
        if (arguments.WemRoot is null)
        {
            return Result.Ok(new PreparedAudio(null, null));
        }

        string output = Path.ChangeExtension(arguments.Out, ".wav");

        string?[] inputs =
        [
            arguments.Out, arguments.Mmb, arguments.Cameldata, arguments.Editordata,
            arguments.SetupAnim, arguments.ClipAnim, arguments.MouthAnim, arguments.EyesAnim,
            arguments.PupilsAnim, arguments.EyebrowsAnim, arguments.LipsyncDatabase,
        ];
        foreach (string? input in inputs)
        {
            if (input is not null && SamePath(output, input))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"The audio sidecar path {output} overlaps an input."));
            }
        }

        Result<WemSelection> resolved = WemResolver.Resolve(arguments.WemRoot, arguments.SpeechId!);
        if (!resolved.TryGetValue(out WemSelection wem, out Refusal? resolveRefusal))
        {
            return resolveRefusal;
        }

        if (SamePath(output, wem.Path))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"The audio sidecar path {output} overlaps its WEM input."));
        }

        Result<AudioInfo> decoded = VgmstreamDecoder.Decode(wem, arguments.VgmstreamCli);
        if (!decoded.TryGetValue(out AudioInfo? info, out Refusal? decodeRefusal))
        {
            return decodeRefusal;
        }

        Result<double?> endResult = LipsyncEndSeconds(arguments);
        if (!endResult.TryGetValue(out double? lipsyncEnd, out Refusal? endRefusal))
        {
            return endRefusal;
        }

        AudioReport report = new(
            output, info.SourceName, info.Locale, info.Channels, info.SampleRate, info.SampleCount,
            info.DurationSeconds, lipsyncEnd, lipsyncEnd is double e ? info.DurationSeconds - e : null);
        return Result.Ok(new PreparedAudio(report, info.Wav));
    }

    /// <summary>The lip-sync schedule's final key time in seconds, or none when no schedule drives the mouth.</summary>
    private static Result<double?> LipsyncEndSeconds(ExportRequest arguments)
    {
        if (arguments.LipsyncDatabase is null || arguments.SpeechId is null)
        {
            return Result.Ok<double?>(null);
        }

        Result<ImmutableArray<(int KeyTime, int Selector)>> schedule = ReadLipsyncSchedule(arguments);
        if (!schedule.TryGetValue(out ImmutableArray<(int KeyTime, int Selector)> pairs, out Refusal? refusal))
        {
            return refusal;
        }

        return Result.Ok<double?>(pairs[^1].KeyTime / 24.0);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), System.StringComparison.Ordinal);
        }
        catch (System.Exception ex) when (ex is System.ArgumentException or System.NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The posed model to dress, the hierarchy that places it, any clip animation, and the parts left unrigged.</summary>
    private readonly record struct PoseResult(
        GeometryModel PosedModel,
        SceneGraph? Graph,
        Animation? Animation,
        ImmutableArray<string> UnriggedParts);

    /// <summary>
    /// Places the full geometry under a setup hierarchy — statically, or as a clip
    /// animation when one was given with --animate — or passes it through unposed.
    /// </summary>
    private static Result<PoseResult> ApplyPose(ExportRequest arguments, GeometryModel geometry)
    {
        if (arguments.SetupAnim is null)
        {
            // An unposed multi-part export is legitimate to ask for and warns
            // rather than refuses — except when this model's own setup ANIM is
            // sitting beside the inputs unpassed. That is a forgotten argument,
            // not a choice, so the export refuses and names the file to pass.
            // --allow-unposed says the complete part list was meant.
            if (!arguments.AllowUnposed && geometry.Parts.Length > 1)
            {
                Refusal? forgotten = ForgottenSetup(arguments.Mmb, geometry);
                if (forgotten is not null)
                {
                    return forgotten;
                }
            }

            return Result.Ok(new PoseResult(geometry, null, null, []));
        }

        Result<AnimFile> setupResult = ReadAnim(arguments.SetupAnim, hierarchy: true);
        if (!setupResult.TryGetValue(out AnimFile? setup, out Refusal? setupRefusal))
        {
            return setupRefusal;
        }

        AnimFile? clip = null;
        if (arguments.ClipAnim is not null)
        {
            Result<AnimFile> clipResult = ReadAnim(arguments.ClipAnim, hierarchy: false);
            if (!clipResult.TryGetValue(out clip, out Refusal? clipRefusal))
            {
                return clipRefusal;
            }
        }

        // Facial atlases, when any is requested, overlay the pose and take over the
        // static-versus-animated decision below.
        Result<ImmutableArray<FacialLayer>> facialResult = ReadFacialLayers(arguments);
        if (!facialResult.TryGetValue(out ImmutableArray<FacialLayer> facial, out Refusal? facialRefusal))
        {
            return facialRefusal;
        }

        if (!facial.IsDefaultOrEmpty)
        {
            // --animate composes the facial states over the whole body clip;
            // otherwise a single frame is sampled at --time.
            if (arguments.Animate)
            {
                Result<AnimatedScene> animated = FacialAnimation.Animate(geometry, setup, clip!, facial);
                if (!animated.TryGetValue(out AnimatedScene? scene, out Refusal? animateRefusal))
                {
                    return animateRefusal;
                }

                return Result.Ok(new PoseResult(
                    geometry.SelectParts(scene.Scene.Keep), scene.Scene.Graph, scene.Animation, scene.Scene.UnriggedParts));
            }

            Result<PosedScene> facialPosed = FacialPose.Pose(geometry, setup, clip, arguments.Time, facial);
            if (!facialPosed.TryGetValue(out PosedScene? facialPlaced, out Refusal? facialPoseRefusal))
            {
                return facialPoseRefusal;
            }

            return Result.Ok(new PoseResult(
                geometry.SelectParts(facialPlaced.Keep), facialPlaced.Graph, null, facialPlaced.UnriggedParts));
        }

        // --animate emits the whole clip; otherwise a single frame is sampled at
        // --time, taking the clip where it drives a channel and the setup elsewhere.
        if (arguments.Animate)
        {
            Result<AnimatedScene> animated = ClipAnimation.Animate(geometry, setup, clip!);
            if (!animated.TryGetValue(out AnimatedScene? scene, out Refusal? animateRefusal))
            {
                return animateRefusal;
            }

            return Result.Ok(new PoseResult(
                geometry.SelectParts(scene.Scene.Keep), scene.Scene.Graph, scene.Animation, scene.Scene.UnriggedParts));
        }

        Result<PosedScene> posed = SetupPose.Pose(geometry, setup, clip, arguments.Time);
        if (!posed.TryGetValue(out PosedScene? placed, out Refusal? poseRefusal))
        {
            return poseRefusal;
        }

        return Result.Ok(new PoseResult(geometry.SelectParts(placed.Keep), placed.Graph, null, placed.UnriggedParts));
    }

    /// <summary>
    /// Scans the directory holding the geometry for a setup ANIM that rigs this
    /// model but was not passed, returning the refusal that names it or nothing.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. Only files the game's own naming marks as setups — a
    /// stem containing "setup" — are even opened, so the filename bounds how many
    /// files are read, and candidacy is decided by running the real association
    /// rule against each, not by its name. A model with no setup anywhere is
    /// unaffected, which is why this can refuse where an unconditional rule could
    /// not. An unrelated or unreadable neighbour answers "no" rather than refusing.
    /// </remarks>
    private static Refusal? ForgottenSetup(string mmbPath, GeometryModel geometry)
    {
        string directory = Path.GetDirectoryName(mmbPath) ?? string.Empty;

        string[] found;
        try
        {
            found = Directory.GetFiles(directory.Length == 0 ? "." : directory, "*.anim");
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            return null;
        }

        // Reconstruct each path the way the reference's parent/name join renders
        // it — a bare filename when the geometry has no directory component — so
        // the path named in the refusal matches, then scan in sorted order.
        string[] candidates =
        [
            .. found
                .Select(path => Path.GetFileName(path))
                .Where(name => Path.GetFileNameWithoutExtension(name).Contains("setup", System.StringComparison.OrdinalIgnoreCase))
                .Select(name => directory.Length == 0 ? name : Path.Combine(directory, name))
                .OrderBy(path => path, System.StringComparer.Ordinal),
        ];

        foreach (string candidate in candidates)
        {
            Result<SourceFile> source = SourceFileReader.Read(candidate);
            if (!source.TryGetValue(out SourceFile? file, out _))
            {
                continue;
            }

            if (SetupPose.DescribesModel(geometry, file))
            {
                string name = Path.GetFileName(candidate);
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name} sits beside the inputs and is this model's setup hierarchy, but --setup-anim was not given. Without it the export is the complete part list with every alternate state present at once. Pass --setup-anim {candidate}, or --allow-unposed if the unposed part list is what you want."));
            }
        }

        return null;
    }

    /// <summary>
    /// Reads and builds the requested facial layers, each a parsed atlas and the
    /// fixed sample its one-based state selects. Empty when none was asked for.
    /// </summary>
    private static Result<ImmutableArray<FacialLayer>> ReadFacialLayers(ExportRequest arguments)
    {
        ImmutableArray<FacialLayer>.Builder layers = ImmutableArray.CreateBuilder<FacialLayer>();

        // The mouth atlas is driven by the lip-sync schedule when one is given, and
        // by a fixed state otherwise; the other systems are always fixed. States
        // are one-based and read the zero-based atlas sample (N-1).
        if (arguments.MouthAnim is not null)
        {
            Result<AnimFile> mouthResult = ReadAnim(arguments.MouthAnim, hierarchy: false);
            if (!mouthResult.TryGetValue(out AnimFile? mouth, out Refusal? mouthRefusal))
            {
                return mouthRefusal;
            }

            if (arguments.LipsyncDatabase is not null)
            {
                Result<ImmutableArray<(int, int)>> schedule = ReadLipsyncSchedule(arguments);
                if (!schedule.TryGetValue(out ImmutableArray<(int, int)> pairs, out Refusal? scheduleRefusal))
                {
                    return scheduleRefusal;
                }

                layers.Add(FacialLayer.Lipsync(mouth, pairs));
            }
            else
            {
                layers.Add(FacialLayer.Fixed("mouth", mouth, arguments.MouthState!.Value - 1));
            }
        }

        // The eye atlas is driven by explicit blink events when any is given, and
        // by a fixed state otherwise; with blinks the state is the optional hold
        // between them.
        if (arguments.EyesAnim is not null)
        {
            Result<AnimFile> eyesResult = ReadAnim(arguments.EyesAnim, hierarchy: false);
            if (!eyesResult.TryGetValue(out AnimFile? eyes, out Refusal? eyesRefusal))
            {
                return eyesRefusal;
            }

            if (!arguments.BlinkAt.IsDefaultOrEmpty)
            {
                int? defaultSample = arguments.EyeState is int eyeState ? eyeState - 1 : null;
                Result<FacialLayer> blink = FacialLayer.Blink(eyes, arguments.BlinkAt, defaultSample);
                if (!blink.TryGetValue(out FacialLayer? layer, out Refusal? blinkRefusal))
                {
                    return blinkRefusal;
                }

                layers.Add(layer);
            }
            else
            {
                layers.Add(FacialLayer.Fixed("eyes", eyes, arguments.EyeState!.Value - 1));
            }
        }

        // Only the pupil layer suppresses its translation, and only under mesh-neutral.
        (string Name, string? Path, int? State, bool Suppress)[] systems =
        [
            ("pupils", arguments.PupilsAnim, arguments.PupilState,
                string.Equals(arguments.PupilPosition, "mesh-neutral", System.StringComparison.Ordinal)),
            ("eyebrows", arguments.EyebrowsAnim, arguments.EyebrowState, false),
        ];

        foreach ((string name, string? path, int? state, bool suppress) in systems)
        {
            if (path is null)
            {
                continue;
            }

            Result<AnimFile> atlas = ReadAnim(path, hierarchy: false);
            if (!atlas.TryGetValue(out AnimFile? file, out Refusal? refusal))
            {
                return refusal;
            }

            layers.Add(FacialLayer.Fixed(name, file, state!.Value - 1, suppress));
        }

        return Result.Ok(layers.ToImmutable());
    }

    /// <summary>Reads the lip-sync schedule for the requested speech ID as key/selector pairs.</summary>
    private static Result<ImmutableArray<(int, int)>> ReadLipsyncSchedule(ExportRequest arguments)
    {
        Result<SourceFile> file = SourceFileReader.Read(arguments.LipsyncDatabase!);
        if (!file.TryGetValue(out SourceFile? source, out Refusal? fileRefusal))
        {
            return fileRefusal;
        }

        Result<ImmutableArray<LipsyncPair>> schedule = LipsyncReader.ReadSchedule(source, arguments.SpeechId!);
        if (!schedule.TryGetValue(out ImmutableArray<LipsyncPair> pairs, out Refusal? scheduleRefusal))
        {
            return scheduleRefusal;
        }

        return Result.Ok(ImmutableArray.CreateRange(pairs, p => (p.KeyTime, p.Selector)));
    }

    private static Result<AnimFile> ReadAnim(string path, bool hierarchy)
    {
        Result<SourceFile> file = SourceFileReader.Read(path);
        if (!file.TryGetValue(out SourceFile? source, out Refusal? refusal))
        {
            return refusal;
        }

        return AnimReader.Read(source, hierarchy);
    }

    private static ImmutableArray<int> Identity(int count)
    {
        ImmutableArray<int>.Builder builder = ImmutableArray.CreateBuilder<int>(count);
        for (int i = 0; i < count; i++)
        {
            builder.Add(i);
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Applies each baked part's UV0 rewrite to the full model, keyed by source
    /// ordinal, translating the neutral remap into the affine the model takes.
    /// </summary>
    private static GeometryModel ApplyUv0Remaps(GeometryModel geometry, ImmutableArray<BakedUv0> remaps)
    {
        if (remaps.IsDefaultOrEmpty)
        {
            return geometry;
        }

        Dictionary<int, (double, double, double, double)> byPart = new(remaps.Length);
        foreach (BakedUv0 remap in remaps)
        {
            byPart[remap.SourceOrdinal] = (remap.Remap.ScaleU, remap.Remap.ScaleV, remap.Remap.OffsetU, remap.Remap.OffsetV);
        }

        return geometry.RewriteUv0(byPart);
    }

    /// <summary>
    /// Resolves materials when an editordata was supplied, and nothing otherwise.
    /// </summary>
    /// <remarks>
    /// The content sources are disposed here rather than held: the archives keep
    /// file handles open, and an export has no reason to keep them past the one
    /// pass that reads every texture it needs.
    /// </remarks>
    private static Result<MaterialSet> AssembleMaterials(
        ExportRequest arguments, GeometryModel posedModel, int fullPartCount)
    {
        if (arguments.Editordata is null)
        {
            return Result.Ok(MaterialSet.Empty);
        }

        Result<SourceFile> file = SourceFileReader.Read(arguments.Editordata);
        if (!file.IsSuccess)
        {
            return file.Refusal;
        }

        Result<EditordataFile> editordata = EditordataReader.Read(file.Value);
        if (!editordata.IsSuccess)
        {
            return editordata.Refusal;
        }

        // The editordata dresses the whole model one section per part, so a count
        // that disagrees with the full part list is the wrong file — checked
        // against the full model rather than the posed subset it will address.
        if (editordata.Value.Sections.Length != fullPartCount)
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The editordata declares {editordata.Value.Sections.Length} sections and the model has {fullPartCount} parts, so no section ordinal names a part."));
        }

        using ContentSources content = new(arguments.ContentRoot, arguments.SdfRoot);
        return MaterialAssembler.Assemble(posedModel, editordata.Value, content);
    }

    private static ExportCounts Count(GeometryModel geometry)
    {
        int vertices = 0;
        int triangles = 0;
        foreach (GeometryPart part in geometry.Parts)
        {
            vertices += part.Positions.Length;
            triangles += part.Indices.Length / 3;
        }

        return new ExportCounts(geometry.Parts.Length, vertices, triangles);
    }

    private static ImmutableArray<Diagnostic> Diagnose(
        GeometryModel geometry, MaterialSet materials, bool posed, ImmutableArray<string> unriggedParts, bool mouthAtlas)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        // A posed export places its parts, so it is not the overlaid part list the
        // unposed warning describes; a single-part model has no states to overlay.
        if (!posed && geometry.Parts.Length > 1)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.ExportUnposed,
                DiagnosticSeverity.Warning,
                UnposedWarning + geometry.Parts.Length.ToString(CultureInfo.InvariantCulture)));
        }

        // The material disclosure follows the unposed one, and is emitted once
        // whenever any material was reconstructed.
        if (!materials.IsEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                MaterialWarning));
        }

        // The transparent disclosure follows it when any surface blends. The
        // harness compares the warning set regardless of order, so its position
        // relative to the material warning is not load-bearing.
        if (materials.HasTransparent)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.TransparentMaterialApproximated,
                DiagnosticSeverity.Warning,
                TransparentWarning));
        }

        if (!materials.ClampedParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.TransparentMaterialApproximated,
                DiagnosticSeverity.Warning,
                ClampedWarning + SectionList(materials.ClampedParts)));
        }

        if (!materials.BakedParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.TransparentMaterialApproximated,
                DiagnosticSeverity.Warning,
                BakedWarning + SectionList(materials.BakedParts)));
        }

        if (!materials.OversizedOmissions.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.PrimitiveOmittedBakeTooLarge,
                DiagnosticSeverity.Warning,
                OversizedWarning + string.Join(
                    "; ",
                    materials.OversizedOmissions.Select(o => string.Create(
                        CultureInfo.InvariantCulture,
                        $"section {o.SourceOrdinal}: {o.Detail}")))));
        }

        if (!materials.OffsetBakedParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                ColourOffsetWarning + SectionList(materials.OffsetBakedParts)));
        }

        if (!materials.ClippedParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                ColourClipWarning + SectionList(materials.ClippedParts)));
        }

        if (!materials.MergedCompanions.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                EmissiveWarning + SectionList(materials.MergedCompanions)));
        }

        if (!materials.UnpairedCompanions.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                EmissiveUnpairedWarning + SectionList(materials.UnpairedCompanions)));
        }

        // A mouth atlas swaps material at runtime; with materials reconstructed,
        // the surface keeps each facial part's own editordata material instead.
        if (mouthAtlas && !materials.IsEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.MaterialApproximated,
                DiagnosticSeverity.Warning,
                FacialMaterialWarning));
        }

        if (!unriggedParts.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.FormatUnsupported,
                DiagnosticSeverity.Warning,
                UnriggedWarning + UnriggedList(unriggedParts)));
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Renders the omitted-part list the way the reference does: the names
    /// sorted, capped at twelve, with a count of the remainder.
    /// </summary>
    private static string UnriggedList(ImmutableArray<string> parts)
    {
        string[] sorted = [.. parts.OrderBy(p => p, System.StringComparer.Ordinal)];
        string shown = string.Join(", ", sorted.Take(12));
        return sorted.Length > 12
            ? shown + string.Create(CultureInfo.InvariantCulture, $" (and {sorted.Length - 12} more)")
            : shown;
    }

    /// <summary>
    /// Renders a sorted section list the way the reference does, as a bracketed
    /// comma-separated sequence, so the warning text matches verbatim.
    /// </summary>
    private static string SectionList(System.Collections.Immutable.ImmutableArray<int> ordinals) =>
        "[" + string.Join(", ", ordinals.Select(o => o.ToString(CultureInfo.InvariantCulture))) + "]";
}
