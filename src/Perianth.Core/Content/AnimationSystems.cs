using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using Perianth.Formats.Anim;
using Perianth.Formats.Bvm;
using Perianth.Formats.Mmb;
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
/// <summary>How many of a model's binding nodes the rig it is paired with declares.</summary>
/// <param name="Setup">The setup ANIM the animation system names.</param>
/// <param name="Declared">Binding nodes the setup declares.</param>
/// <param name="Bindings">Distinct binding nodes the model's parts name.</param>
/// <param name="Unplaced">The ones the setup does not declare, in name order.</param>
public sealed record RigCoverage(
    string Setup, int Declared, int Bindings, ImmutableArray<string> Unplaced)
{
    /// <summary>Whether every part of the model has a node to hang on.</summary>
    public bool Complete => Unplaced.IsEmpty;
}

public static class AnimationSystems
{
    private const string ActorFolder = "camel/graph objects/actor/";
    private const string ActorExtension = ".mgraphobject";
    private const string SystemExtension = ".manimsys";
    private const string SetupSuffix = "_setup.anim";
    private const string ModelExtension = ".mmb";

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

        return SetupsInSystem(content, systems[0]);
    }

    /// <summary>Every setup ANIM one animation system mentions, in path order.</summary>
    /// <remarks>
    /// The second half of <see cref="SetupsFor"/>, separated because a caller may
    /// already know which system applies — the actor graph object it is editing
    /// names one — and going back through the actor to find it again would be a
    /// different question with a different failure.
    /// </remarks>
    public static Result<ImmutableArray<string>> SetupsInSystem(ContentSources content, string systemPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(systemPath);

        Result<BvmFile> system = ReadContainer(content, SdfIndex.NormalizePath(systemPath));
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

    /// <summary>
    /// How much of a model the rig it is paired with can actually place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A character's graph object names a model <b>and</b> an animation system,
    /// and a part draws only where the setup behind that system declares the node
    /// its label binds to. So the two are not independently choosable: repointing
    /// one without the other can pair a model with a rig that has nowhere to put
    /// most of it.
    /// </para>
    /// <para>
    /// <b>This exists because that produced a silently broken mod.</b> An in-game
    /// probe repointed one character at another's model and left the animation
    /// system alone; the mod installed, loaded, and drew nothing different, and
    /// the null result read as the game refusing the edit. It was not: the
    /// borrowed model's parts name 499 binding nodes and the rig it was given
    /// declares 168 of them, against 496 for its own. Roadmap §10.118.
    /// </para>
    /// <para>
    /// It reports rather than deciding. A number below 1 is a warning and not a
    /// refusal, because shipping the matching system in the same mod is the
    /// correct fix and a refusal here cannot tell that from a mistake — the same
    /// argument <see cref="ModCheck"/> makes about a repointed texture that has
    /// not been added yet.
    /// </para>
    /// </remarks>
    public static Result<RigCoverage> Coverage(ContentSources content, string modelPath, string systemPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(modelPath);
        ArgumentNullException.ThrowIfNull(systemPath);

        Result<ImmutableArray<string>> found = SetupsInSystem(content, systemPath);
        if (!found.TryGetValue(out ImmutableArray<string> setups, out Refusal? refusal))
        {
            return refusal;
        }

        if (setups.IsEmpty)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{systemPath}' names no setup ANIM, so there is no rig to check the model against."));
        }

        if (setups.Length > 1)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{systemPath}' names {setups.Length} setup ANIMs and this build cannot tell which applies: {string.Join(", ", setups)}"));
        }

        Result<byte[]?> modelBytes = content.Read(SdfIndex.NormalizePath(modelPath));
        if (!modelBytes.TryGetValue(out byte[]? model, out Refusal? modelRefusal))
        {
            return modelRefusal;
        }

        if (model is null)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture,
                $"No source holds '{modelPath}', so its parts cannot be checked against the rig."));
        }

        Result<MmbModel> read = MmbReader.Read(SourceFile.FromMemory(modelPath, model));
        if (!read.TryGetValue(out MmbModel? parts, out Refusal? modelBad))
        {
            return modelBad;
        }

        Result<byte[]?> setupBytes = content.Read(setups[0]);
        if (!setupBytes.TryGetValue(out byte[]? setup, out Refusal? setupRefusal))
        {
            return setupRefusal;
        }

        if (setup is null)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"No source holds '{setups[0]}'."));
        }

        Result<AnimDocument> rig = AnimReader.ReadDocument(SourceFile.FromMemory(setups[0], setup));
        if (!rig.TryGetValue(out AnimDocument? document, out Refusal? rigBad))
        {
            return rigBad;
        }

        HashSet<string> declared = new(document.Names, StringComparer.Ordinal);
        SortedSet<string> bindings = new(StringComparer.Ordinal);
        SortedSet<string> unplaced = new(StringComparer.Ordinal);
        foreach (MmbModelPart part in parts.Parts)
        {
            string node = part.BindingNode;
            if (bindings.Add(node) && !declared.Contains(node))
            {
                _ = unplaced.Add(node);
            }
        }

        return Result.Ok(new RigCoverage(
            setups[0], bindings.Count - unplaced.Count, bindings.Count, [.. unplaced]));
    }

    /// <summary>
    /// Every actor definition that names a model, by virtual path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A character can be drawn through more than one graph object — one per
    /// situation it appears in — and <b>264 of 933 models are named by several</b>,
    /// up to seven. So repointing one changes that character where that graph
    /// object applies and nowhere else.
    /// </para>
    /// <para>
    /// <b>Both are things somebody means to do.</b> Changing one situation is as
    /// legitimate as changing all of them, so this reports the set and decides
    /// nothing: with it in front of them a caller can edit one deliberately, or
    /// edit each in turn. What it replaces is finding out from the game.
    /// </para>
    /// <para>
    /// Loose-tree only, through <see cref="ContentSources.ListLoose"/>, and empty
    /// where no content root was given — which the caller must report as "not
    /// checked" rather than as "no others".
    /// </para>
    /// </remarks>
    public static Result<ImmutableArray<string>> ActorsNaming(ContentSources content, string modelPath)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(modelPath);

        Result<ImmutableArray<string>> listed = content.ListLoose(ActorFolder.TrimEnd('/'), ActorExtension);
        if (!listed.TryGetValue(out ImmutableArray<string> actors, out Refusal? refusal))
        {
            return refusal;
        }

        string wanted = SdfIndex.NormalizePath(modelPath);
        ImmutableArray<string>.Builder naming = ImmutableArray.CreateBuilder<string>();
        foreach (string actor in actors)
        {
            Result<BvmFile> read = ReadContainer(content, actor);
            if (!read.IsSuccess)
            {
                // One unreadable definition is not a reason to answer nothing
                // about the other 1,249, and it is not evidence about this model
                // either way.
                continue;
            }

            foreach (string text in read.Value.Strings)
            {
                if (text.EndsWith(ModelExtension, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(SdfIndex.NormalizePath(text), wanted, StringComparison.Ordinal))
                {
                    naming.Add(actor);
                    break;
                }
            }
        }

        return Result.Ok(naming.ToImmutable());
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
