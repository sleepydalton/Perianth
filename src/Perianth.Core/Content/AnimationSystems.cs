using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// What setup ANIM the game itself assigns to a model, rather than what a
/// filename convention implies.
/// </summary>
/// <remarks>
/// <para>
/// An actor has a definition under <c>graph objects/actor</c> naming an
/// animation system, and the system's string table carries the setup path. That
/// is the game's own answer, and it reaches models no naming rule can:
/// <see cref="CharacterResolver"/> resolves 889 of 918 characters by convention,
/// and four of the remaining 29 have a setup recorded here.
/// </para>
/// <para>
/// It is deliberately a supplement and not a replacement. Measured against the
/// convention over all 918 characters, the two agree 158 times, the game names a
/// setup the convention misses 4 times, and <b>they never contradict each
/// other</b> — so the measured rules stand, and this fills gaps in them.
/// </para>
/// <para>
/// The system's table is a flat list. It says which setups a system mentions,
/// not which node references which, because that lives in a graph this build
/// does not read. So a system naming several is refused rather than guessed at.
/// Over 800 sampled systems, 73.5% name none, 26.2% name exactly one and 0.2%
/// name two, so the refusal is rare — and where a character has variants, the
/// game gives each variant its own system, which is what keeps it rare.
/// </para>
/// </remarks>
public static class AnimationSystems
{
    private const string ActorFolder = "camel/graph objects/actor/";
    private const string ActorExtension = ".mgraphobject";
    private const string SystemExtension = ".manimsys";
    private const string SetupSuffix = "_setup.anim";

    /// <summary>
    /// The setup ANIM the game names for one model, by its virtual path.
    /// </summary>
    /// <remarks>
    /// Refuses rather than choosing whenever the answer is not single: no actor
    /// definition, no system, or a system naming more than one setup. Every one
    /// of those is <see cref="RefusalKind.Unsupported"/> — the files are
    /// well-formed and the question simply has no single answer in them.
    /// </remarks>
    public static Result<string> SetupFor(ContentSources content, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(modelPath);

        Result<ImmutableArray<string>> found = SetupsFor(content, modelPath);
        if (!found.TryGetValue(out ImmutableArray<string> setups, out Refusal? refusal))
        {
            return refusal;
        }

        string stem = Stem(modelPath);

        if (setups.IsEmpty)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The animation system for '{stem}' names no setup ANIM."));
        }

        if (setups.Length > 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The animation system for '{stem}' names {setups.Length} setup ANIMs, and which one belongs to this model is recorded in a part of the file this build does not read. Choose one by hand: {string.Join(", ", setups)}"));
        }

        return Result.Ok(setups[0]);
    }

    /// <summary>
    /// Every setup ANIM the model's animation system mentions, in path order.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="SetupFor"/> because a front end offering a
    /// choice needs the whole list, and because "this system names eleven" is a
    /// useful thing to be able to show rather than only to refuse over.
    /// </remarks>
    public static Result<ImmutableArray<string>> SetupsFor(ContentSources content, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(modelPath);

        string stem = Stem(modelPath);

        Result<BvmFile> actor = ReadContainer(content, ActorFolder + stem + ActorExtension);
        if (!actor.TryGetValue(out BvmFile? definition, out Refusal? actorRefusal))
        {
            return actorRefusal;
        }

        ImmutableArray<string> systems =
        [
            .. definition.Strings
                .Where(s => s.EndsWith(SystemExtension, StringComparison.OrdinalIgnoreCase))
                .Select(SdfIndex.NormalizePath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ];

        if (systems.IsEmpty)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The actor definition for '{stem}' names no animation system."));
        }

        if (systems.Length > 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The actor definition for '{stem}' names {systems.Length} animation systems, and this build cannot tell which one applies."));
        }

        Result<BvmFile> system = ReadContainer(content, systems[0]);
        if (!system.TryGetValue(out BvmFile? animations, out Refusal? systemRefusal))
        {
            return systemRefusal;
        }

        return Result.Ok<ImmutableArray<string>>(
        [
            .. animations.Strings
                .Where(s => s.EndsWith(SetupSuffix, StringComparison.OrdinalIgnoreCase))
                .Select(SdfIndex.NormalizePath)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
        ]);
    }

    private static Result<BvmFile> ReadContainer(ContentSources content, string path)
    {
        Result<byte[]?> read = content.Read(path);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"No source holds {path}."));
        }

        return BvmReader.Read(SourceFile.FromMemory(path, bytes));
    }

    /// <summary>The model's file name without its folder or extension.</summary>
    private static string Stem(string modelPath)
    {
        string normalized = SdfIndex.NormalizePath(modelPath);
        int slash = normalized.LastIndexOf('/');
        string file = slash < 0 ? normalized : normalized[(slash + 1)..];
        int dot = file.LastIndexOf('.');
        return dot < 0 ? file : file[..dot];
    }
}
