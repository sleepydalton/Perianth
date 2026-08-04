using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>How an asset was found.</summary>
public enum AssetMatch
{
    /// <summary>Named directly after the model's own stem.</summary>
    Exact,

    /// <summary>
    /// Named after the rig family the model's <c>_var_</c> suffix belongs to.
    /// </summary>
    /// <remarks>
    /// Not a weaker match, but a different claim: the family's rig is shared,
    /// and it fits the variant less exactly than a model's own. Worth showing,
    /// because a variant posed this way can leave a few parts unplaced where a
    /// direct match leaves none.
    /// </remarks>
    VariantBase,
}

/// <summary>One resolved file, and the rule that found it.</summary>
public sealed record ResolvedAsset(string VirtualPath, AssetMatch Match);

/// <summary>
/// Everything one model needs, gathered from the conventions that name it.
/// </summary>
/// <param name="Name">The animation-tree name this model resolves under.</param>
/// <param name="Model">The geometry.</param>
/// <param name="Cameldata">The companion pools, or none — without which nothing can be exported.</param>
/// <param name="Editordata">The materials and texture paths, or none.</param>
/// <param name="Setup">The setup ANIM that places and selects the parts.</param>
/// <param name="Mouth">The mouth atlas.</param>
/// <param name="Eyes">The eyes atlas.</param>
/// <param name="Pupils">The pupils atlas.</param>
/// <param name="Eyebrows">The eyebrows atlas.</param>
/// <param name="Clips">Clip ANIMs belonging to this model, ordered by path.</param>
/// <param name="LipsyncDatabase">The one shared lip-sync database.</param>
/// <param name="Unresolved">
/// What the conventions did not account for, in prose. Empty is the common case
/// and is not a promise that everything exists — only that nothing was expected
/// and missing.
/// </param>
public sealed record CharacterAssets(
    string Name,
    string Model,
    string? Cameldata,
    string? Editordata,
    ResolvedAsset? Setup,
    ResolvedAsset? Mouth,
    ResolvedAsset? Eyes,
    ResolvedAsset? Pupils,
    ResolvedAsset? Eyebrows,
    ImmutableArray<ResolvedAsset> Clips,
    string? LipsyncDatabase,
    ImmutableArray<string> Unresolved)
{
    /// <summary>Every resolved path, deduplicated and ordered, for extraction.</summary>
    public ImmutableArray<string> Paths()
    {
        SortedSet<string> paths = new(StringComparer.Ordinal) { Model };

        foreach (string? optional in new[] { Cameldata, Editordata, LipsyncDatabase })
        {
            if (optional is not null)
            {
                paths.Add(optional);
            }
        }

        foreach (ResolvedAsset? asset in new[] { Setup, Mouth, Eyes, Pupils, Eyebrows })
        {
            if (asset is not null)
            {
                paths.Add(asset.VirtualPath);
            }
        }

        foreach (ResolvedAsset clip in Clips)
        {
            paths.Add(clip.VirtualPath);
        }

        return [.. paths];
    }
}

/// <summary>
/// Assembles one model's asset set from the archive's naming conventions.
/// </summary>
/// <remarks>
/// <para>
/// The conventions are recorded in the roadmap as <em>observed, not proven</em>,
/// and the census that preceded this says how far each one actually goes. Every
/// rule below is measured over all 486,543 archive paths, and the numbers are
/// why the rules are shaped as they are rather than as they first read.
/// </para>
/// <para>
/// Nothing here guesses. Where a convention runs out, the asset is absent and
/// the reason is said in <see cref="CharacterAssets.Unresolved"/> — because the
/// caller can supply a file by hand, and cannot recover from being handed the
/// wrong one silently.
/// </para>
/// </remarks>
public static partial class CharacterResolver
{
    private const string AnimationFolder = "camel/baked/snowdrop/animation/";
    private const string ModelExtension = ".mmb";

    private static readonly string[] Systems = ["mouth", "eyes", "pupils", "eyebrows"];

    /// <summary>
    /// Splits a variant from the rig family it belongs to.
    /// </summary>
    /// <remarks>
    /// <c>chr_catskinny_var_a</c> resolves under <c>catskinny</c>, which names no
    /// model of its own: the family exists only in the animation tree. Without
    /// this clause the setup convention holds for 65.47% of characters; with it,
    /// 96.84%.
    /// </remarks>
    [GeneratedRegex(@"^(?<base>.+?)_var_[a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Variant { get; }

    /// <summary>
    /// Resolves the asset set for one model named by its virtual path.
    /// </summary>
    public static Result<CharacterAssets> Resolve(ImmutableArray<SdfPathEntry> paths, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(modelPath);

        string wanted = SdfIndex.NormalizePath(modelPath);
        if (!wanted.EndsWith(ModelExtension, StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{modelPath} does not name a {ModelExtension} model, and an asset set is assembled around one."));
        }

        HashSet<string> every = new(StringComparer.Ordinal);
        HashSet<string> animations = new(StringComparer.Ordinal);
        HashSet<string> models = new(StringComparer.Ordinal);

        foreach (SdfPathEntry entry in paths)
        {
            string path = SdfIndex.NormalizePath(entry.Path);
            every.Add(path);

            if (path.EndsWith(".anim", StringComparison.Ordinal))
            {
                animations.Add(Stem(path));
            }
            else if (path.EndsWith(ModelExtension, StringComparison.Ordinal))
            {
                models.Add(Stem(path));
            }
        }

        if (!every.Contains(wanted))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"The archives hold no model named {wanted}."));
        }

        string stem = Stem(wanted);
        string name = stem.StartsWith("chr_", StringComparison.Ordinal) ? stem[4..] : stem;
        string @base = wanted[..^ModelExtension.Length];
        List<string> unresolved = [];

        string? cameldata = Companion(every, @base, ".cameldata", unresolved,
            "without it there are no vertex positions, and nothing can be exported");
        string? editordata = Companion(every, @base, ".editordata", unresolved,
            "so no materials or textures can be reconstructed");

        ResolvedAsset? setup = Find(animations, name, "setup");

        ResolvedAsset?[] facial = new ResolvedAsset?[Systems.Length];
        for (int i = 0; i < Systems.Length; i++)
        {
            facial[i] = Find(animations, name, Systems[i] + "_all");
        }

        if (System.Array.TrueForAll(facial, asset => asset is null))
        {
            unresolved.Add(string.Create(
                CultureInfo.InvariantCulture, $"no facial atlas is named for '{name}'"));
        }

        Result<ImmutableArray<ResolvedAsset>> clips = Clips(animations, models, name, unresolved);
        if (!clips.TryGetValue(out ImmutableArray<ResolvedAsset> resolved, out Refusal? clipRefusal))
        {
            return clipRefusal;
        }

        if (setup is null)
        {
            // Said after the clips, because what is worth saying depends on
            // them. "No setup ANIM" is true of every prop in the archive — the
            // convention is a character one, 582 animations carry the word and
            // no prop has one — so reporting it as a limitation misdescribed
            // 3,317 props that are posed perfectly well by an idle.
            unresolved.Add(resolved.IsEmpty
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"no ANIM is named for '{name}', so this model can only be exported as its complete part list")
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{name}' has no setup ANIM, which is normal for a prop. Pose it with one of its {resolved.Length} animations — an idle is usually the resting state — or export it unposed and get every alternate state at once"));
        }

        string? lipsync = Lipsync(every, unresolved);

        return Result.Ok(new CharacterAssets(
            name,
            wanted,
            cameldata,
            editordata,
            setup,
            facial[0],
            facial[1],
            facial[2],
            facial[3],
            resolved,
            lipsync,
            [.. unresolved]));
    }

    /// <summary>
    /// Finds one conventionally named ANIM, directly or through the rig family.
    /// </summary>
    /// <summary>
    /// A model's name as its animations spell it, which is without the kind
    /// prefix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured, not guessed: <c>prp_aframe_sign_citywok</c> is posed by
    /// <c>anm_aframe_sign_citywok_idle_intact</c> — the <c>prp_</c> is dropped.
    /// Matching on the full stem found nothing, so 3,317 of the archive's 3,874
    /// props resolved as having no animations at all and exported as their
    /// complete part list: every alternate state overlaid, which for that sign
    /// is 25 parts where the standing sign is 9.
    /// </para>
    /// <para>
    /// Both spellings are tried, longest first, because a character's
    /// animations do carry the prefix and dropping it must not make
    /// <c>prp_x</c> answer to <c>chr_x</c>'s clips.
    /// </para>
    /// </remarks>
    private static string Bare(string name)
    {
        int underscore = name.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? name[(underscore + 1)..] : name;
    }

    private static ResolvedAsset? Find(HashSet<string> animations, string name, string suffix)
    {
        if (animations.Contains($"anm_{name}_{suffix}"))
        {
            return new ResolvedAsset($"{AnimationFolder}anm_{name}_{suffix}.anim", AssetMatch.Exact);
        }

        string bare = Bare(name);
        if (bare != name && animations.Contains($"anm_{bare}_{suffix}"))
        {
            return new ResolvedAsset($"{AnimationFolder}anm_{bare}_{suffix}.anim", AssetMatch.Exact);
        }

        Match variant = Variant.Match(name);
        if (variant.Success)
        {
            string family = variant.Groups["base"].Value;
            if (animations.Contains($"anm_{family}_{suffix}"))
            {
                return new ResolvedAsset($"{AnimationFolder}anm_{family}_{suffix}.anim", AssetMatch.VariantBase);
            }
        }

        return null;
    }

    /// <summary>
    /// The clips belonging to this model, and only this model.
    /// </summary>
    /// <remarks>
    /// Two rules, both measured. The separator is required, or <c>bebe</c>
    /// selects <c>bebesdad</c>'s clips — 6.64% of names prefix another, and
    /// requiring it cuts that to eight. Those eight are then settled by the
    /// longest matching name winning: without it <c>monsterranged</c> absorbs
    /// the 178 clips of <c>monsterranged_milka</c> and <c>monsterranged_milkb</c>,
    /// and 225 clips are misattributed across the archive. Neither rule is a
    /// preference; each removes a known wrong answer.
    /// </remarks>
    private static Result<ImmutableArray<ResolvedAsset>> Clips(
        HashSet<string> animations,
        HashSet<string> models,
        string name,
        List<string> unresolved)
    {
        ImmutableArray<ResolvedAsset>.Builder found = ImmutableArray.CreateBuilder<ResolvedAsset>();
        AddClips(animations, name, Longer(models, name), AssetMatch.Exact, found);

        if (found.Count == 0 && Bare(name) != name)
        {
            // A prop's animations drop its kind prefix. Tried only when the
            // full name found nothing, so a character keeps the exact match it
            // already had and cannot be given a prop's clips by accident.
            AddClips(animations, Bare(name), Longer(models, Bare(name)), AssetMatch.Exact, found);
        }

        if (found.Count == 0)
        {
            // A variant with no clips of its own plays its family's, the same
            // claim the setup makes. The exclusions are recomputed for the
            // family: falling back must yield the family's own clips, not every
            // sibling's as well. Applying the variant's exclusions here gave
            // monsterranged_var_c 371 clips where monsterranged itself has 195.
            Match variant = Variant.Match(name);
            if (variant.Success)
            {
                string family = variant.Groups["base"].Value;
                AddClips(animations, family, Longer(models, family), AssetMatch.VariantBase, found);
            }
        }

        if (found.Count == 0)
        {
            unresolved.Add(string.Create(
                CultureInfo.InvariantCulture, $"no clip ANIM is named for '{name}'"));
        }

        found.Sort(static (left, right) => string.CompareOrdinal(left.VirtualPath, right.VirtualPath));
        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// The character names that extend this one, and so own their own clips.
    /// </summary>
    /// <remarks>
    /// Derived from the models present rather than from the clip names, because
    /// a clip cannot say which character it belongs to. Note that an extending
    /// name need not be a <c>_var_</c> variant: <c>monsterranged_milka</c> and
    /// <c>monsterranged_milkb</c> are separate characters, and between them they
    /// own the 178 clips <c>monsterranged</c> would otherwise absorb.
    /// </remarks>
    private static List<string> Longer(HashSet<string> models, string name)
    {
        List<string> longer = [];

        foreach (string other in models)
        {
            string candidate = other.StartsWith("chr_", StringComparison.Ordinal) ? other[4..] : other;
            if (candidate.Length > name.Length && candidate.StartsWith(name + "_", StringComparison.Ordinal))
            {
                longer.Add(candidate);
            }
        }

        return longer;
    }

    private static void AddClips(
        HashSet<string> animations,
        string name,
        List<string> longer,
        AssetMatch match,
        ImmutableArray<ResolvedAsset>.Builder found)
    {
        string prefix = $"anm_{name}_";

        foreach (string stem in animations)
        {
            if (!stem.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            // The setup and the atlases are resolved by name; a clip is what is
            // left, so listing them here would export them twice.
            if (stem.EndsWith("_setup", StringComparison.Ordinal) || IsAtlas(stem))
            {
                continue;
            }

            bool owned = false;
            foreach (string other in longer)
            {
                if (stem.StartsWith($"anm_{other}_", StringComparison.Ordinal))
                {
                    owned = true;
                    break;
                }
            }

            if (!owned)
            {
                found.Add(new ResolvedAsset($"{AnimationFolder}{stem}.anim", match));
            }
        }
    }

    private static bool IsAtlas(string stem)
    {
        foreach (string system in Systems)
        {
            if (stem.EndsWith($"_{system}_all", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Companion(
        HashSet<string> every,
        string @base,
        string extension,
        List<string> unresolved,
        string why)
    {
        string path = @base + extension;
        if (every.Contains(path))
        {
            return path;
        }

        unresolved.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"this model ships without its {extension}, {why}"));
        return null;
    }

    /// <summary>
    /// The one shared lip-sync database.
    /// </summary>
    /// <remarks>
    /// Nothing in a character's own files names it: exactly one exists in the
    /// whole archive set, which is why it is found by being the only one rather
    /// than by a convention. More than one would mean that reasoning no longer
    /// holds, so the count is checked rather than assumed.
    /// </remarks>
    private static string? Lipsync(HashSet<string> every, List<string> unresolved)
    {
        string? found = null;
        int count = 0;

        foreach (string path in every)
        {
            if (path.EndsWith(".mlipsyncdatabase", StringComparison.Ordinal))
            {
                found ??= path;
                count++;
            }
        }

        if (count == 1)
        {
            return found;
        }

        unresolved.Add(count == 0
            ? "no lip-sync database is present, so speech cannot drive the mouth"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{count} lip-sync databases are present, and nothing names which one belongs to this model"));

        return null;
    }

    private static string Stem(string path)
    {
        int slash = path.LastIndexOf('/');
        string name = slash < 0 ? path : path[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }
}
