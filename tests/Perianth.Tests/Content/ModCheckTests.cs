using System;
using System.Collections.Immutable;
using System.IO;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Tests.Editordata;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks that a mod provides every texture its materials name.
/// </summary>
/// <remarks>
/// The failure being guarded against has no symptom: a repointed path typed
/// once when repointing and once when adding the texture, differing by a
/// character, produces a mod that installs, loads, and draws the wrong thing.
/// These run without archives, which is the harder half — the check must then
/// say plainly that it could not tell a shipped texture from a missing one
/// rather than implying it did.
/// </remarks>
public sealed class ModCheckTests : IDisposable
{
    private const string Bound = "camel/mods/tex_mine_d.dds";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-modcheck-");

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>Writes a mod folder whose one material sheet binds <paramref name="texture"/>.</summary>
    private string Mod(string texture, bool carryIt)
    {
        string folder = Path.Combine(_directory.FullName, "Mine");
        string sheet = Path.Combine(folder, "camel", "baked", "assets", "chr_test.editordata");
        Directory.CreateDirectory(Path.GetDirectoryName(sheet)!);

        File.WriteAllBytes(sheet, new EditordataBuilder()
            .SectionWithCustom([MaterialSpec.Standard(diffuse: texture)], new CustomSpec())
            .Build());

        if (carryIt)
        {
            string carried = Path.Combine(folder, texture.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(carried)!);
            File.WriteAllBytes(carried, [1, 2, 3]);
        }

        return folder;
    }

    private static ModReport Ok(Result<ModReport> result)
    {
        Assert.False(result.IsRefused, result.IsRefused ? result.Refusal.Message : null);
        return result.Value;
    }

    [Fact]
    public void A_texture_the_mod_carries_is_provided()
    {
        ModReport report = Ok(ModCheck.Run(Mod(Bound, carryIt: true), sdfRoot: null));

        Assert.Empty(report.Missing);
        Assert.Equal(1, report.Editordata);
    }

    [Fact]
    public void A_texture_nothing_carries_is_reported_with_the_sheet_that_names_it()
    {
        ModReport report = Ok(ModCheck.Run(Mod(Bound, carryIt: false), sdfRoot: null));

        MissingTexture missing = Assert.Single(report.Missing);
        Assert.Equal(Bound, missing.Texture);
        Assert.Equal("camel/baked/assets/chr_test.editordata", missing.Editordata);
    }

    [Fact]
    public void Without_archives_the_report_says_it_could_not_tell()
    {
        // The distinction the caller has to make: "the mod is broken" and "I
        // did not give it enough to know" look identical in the missing list.
        Assert.False(Ok(ModCheck.Run(Mod(Bound, carryIt: false), sdfRoot: null)).Checked);
    }

    [Fact]
    public void A_path_spelled_with_backslashes_matches_the_file_on_disk()
    {
        // The shipped files spell paths with backslashes and a mod folder is a
        // filesystem tree, so the two are only the same path after folding.
        ModReport report = Ok(ModCheck.Run(
            Mod(Bound.Replace('/', '\\'), carryIt: true), sdfRoot: null));

        Assert.Empty(report.Missing);
    }

    [Fact]
    public void One_texture_bound_by_many_parts_is_one_thing_to_fix()
    {
        string folder = Path.Combine(_directory.FullName, "Many");
        string sheet = Path.Combine(folder, "camel", "chr_many.editordata");
        Directory.CreateDirectory(Path.GetDirectoryName(sheet)!);

        EditordataBuilder builder = new();
        for (int i = 0; i < 40; i++)
        {
            builder.SectionWithCustom([MaterialSpec.Standard(diffuse: Bound)], new CustomSpec());
        }

        File.WriteAllBytes(sheet, builder.Build());

        Assert.Single(Ok(ModCheck.Run(folder, sdfRoot: null)).Missing);
    }

    [Fact]
    public void Only_the_paths_asked_about_are_answered()
    {
        // What the write-time report needs. Asking the broad question there
        // lists every texture the model binds, because without archives a
        // shipped path cannot be told from a missing one.
        Result<ImmutableArray<string>> narrow = ModCheck.Provided(
            Mod(Bound, carryIt: false), sdfRoot: null, ["camel/mods/other.dds"]);

        Assert.Equal(["camel/mods/other.dds"], narrow.Value);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        Result<ModReport> result = ModCheck.Run(
            Path.Combine(_directory.FullName, "absent"), sdfRoot: null);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);
    }

    [Fact]
    public void A_mod_with_no_material_sheet_at_all_has_nothing_to_check()
    {
        // A texture-only mod is the common case and must not look broken.
        Directory.CreateDirectory(Path.Combine(_directory.FullName, "Textures"));

        ModReport report = Ok(ModCheck.Run(
            Path.Combine(_directory.FullName, "Textures"), sdfRoot: null));

        Assert.Equal(0, report.Editordata);
        Assert.Empty(report.Missing);
    }
}
