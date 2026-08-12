using System;
using System.Collections.Immutable;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// The game's own actor definitions, against the archives.
/// </summary>
/// <remarks>
/// <para>
/// Skips without the archives, as the DDS, SDF and PNG suites do, so an
/// ordinary <c>dotnet test</c> stays asset-free.
/// </para>
/// <para>
/// Which models this is run against is named by the environment rather than
/// here: they are the game's content, and the census that found them lives in
/// the research repository with the reasoning that produced it.
/// </para>
/// </remarks>
public sealed class AnimationSystemsConformanceTests
{
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    /// <summary>
    /// A comma-separated list of model virtual paths that have no setup ANIM by
    /// naming convention but do have one recorded by the game.
    /// </summary>
    private const string ModelsVariable = "PERIANTH_DECLARED_SETUP_MODELS";

    [Fact]
    public void Recovers_a_setup_for_a_model_the_naming_convention_cannot_reach()
    {
        if (!Environment(out string root, out string[] models))
        {
            Assert.Skip($"set {RootVariable} and {ModelsVariable} to run against the archives");
            return;
        }

        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        foreach (string model in models)
        {
            Result<string> setup = AnimationSystems.SetupFor(content, model);

            Assert.True(setup.IsSuccess, setup.IsSuccess ? "" : $"{model}: {setup.Refusal!.Message}");
            Assert.EndsWith("_setup.anim", setup.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Refuses_a_model_the_game_defines_no_actor_for()
    {
        if (!Environment(out string root, out _))
        {
            Assert.Skip($"set {RootVariable} and {ModelsVariable} to run against the archives");
            return;
        }

        using ContentSources content = new(contentRoot: null, sdfRoot: root);

        // A path shaped like a model that the archives do not hold. The answer
        // must be a refusal naming what is missing, never a plausible guess
        // assembled from a similar name.
        Result<string> setup = AnimationSystems.SetupFor(
            content, "camel/baked/assets/characters/npc/chr_no_such_actor.mmb");

        Assert.False(setup.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, setup.Refusal!.Kind);
    }

    private static bool Environment(out string root, out string[] models)
    {
        root = System.Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        string list = System.Environment.GetEnvironmentVariable(ModelsVariable) ?? string.Empty;
        models = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return root.Length > 0 && models.Length > 0;
    }
}
