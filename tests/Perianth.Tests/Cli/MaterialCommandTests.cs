using System;
using System.IO;
using Perianth.Cli;
using Perianth.Tests.Editordata;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// The <c>material</c> verb, end to end over a synthetic editordata.
/// </summary>
/// <remarks>
/// What is checked here is the grammar and the reporting rather than the edit
/// itself, which <c>MaterialEditTests</c> covers. The reporting is worth its own
/// tests because it is the only thing standing between a user and a model
/// repainted throughout: one texture is typically bound by hundreds of parts,
/// so the count is the feature.
/// </remarks>
public sealed class MaterialCommandTests : IDisposable
{
    private const string Paper = @"camel\baked\assets\textures\tex_paper_d.dds";

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-material-");

    public void Dispose() => _directory.Delete(recursive: true);

    private string Source()
    {
        byte[] bytes = new EditordataBuilder()
            .SectionWithCustom([MaterialSpec.Standard(diffuse: Paper)], new CustomSpec())
            .SectionWithCustom([MaterialSpec.Standard(diffuse: Paper)], new CustomSpec())
            .SectionWithCustom([MaterialSpec.Standard(diffuse: @"camel\other.dds")], new CustomSpec())
            .Build();

        string path = Path.Combine(_directory.FullName, "chr_test.editordata");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static (int Code, string Out, string Error) Run(params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = Program.Run(["material", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void A_dry_run_says_how_much_it_would_change_and_writes_nothing()
    {
        (int code, string output, _) = Run(
            "--editordata", Source(), "--repoint", $"{Paper}=camel/mine.dds", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("2 sections", output, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", output, StringComparison.Ordinal);
        Assert.Single(_directory.GetFiles());
    }

    [Fact]
    public void A_written_mod_holds_the_edited_file_under_the_named_archive_path()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        (int code, string output, _) = Run(
            "--editordata", Source(),
            "--repoint", $"{Paper}=camel/mine.dds",
            "--replaces", "camel/baked/assets/characters/chr_test.editordata",
            "--out", destination, "--name", "Test Mod");

        Assert.Equal(0, code);
        Assert.Contains("Wrote 1 file", output, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(
            destination, "Test Mod", "camel", "baked", "assets", "characters", "chr_test.editordata")));
        Assert.True(File.Exists(Path.Combine(destination, "Test Mod", "manifest.ini")));
    }

    [Fact]
    public void Assigning_paints_the_named_parts_whatever_they_carried()
    {
        (int code, string output, _) = Run(
            "--editordata", Source(), "--assign", "camel/mine.dds",
            "--section", "0", "--section", "2", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("painted 2 parts", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Assigning_without_naming_a_part_refuses()
    {
        // Without one it would bind a texture across a whole model, which is a
        // thing to do by accident and never on purpose.
        (int code, _, string error) = Run(
            "--editordata", Source(), "--assign", "camel/mine.dds", "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("needs at least one --section", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edit_that_matches_nothing_refuses()
    {
        (int code, _, string error) = Run(
            "--editordata", Source(), "--repoint", "camel/absent.dds=camel/mine.dds", "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("Nothing in this editordata binds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_no_edit_at_all_refuses()
    {
        (int code, _, string error) = Run("--editordata", Source(), "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("nothing to change", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repoint_without_an_equals_sign_refuses()
    {
        (int code, _, string error) = Run("--editordata", Source(), "--repoint", "just-one-path");

        Assert.Equal(2, code);
        Assert.Contains("wants OLD=NEW", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_containing_an_equals_sign_splits_at_the_first_one()
    {
        // Splitting at the last '=' would make a new path holding one
        // unusable, and paths are not ours to forbid characters in.
        (int code, string output, _) = Run(
            "--editordata", Source(), "--repoint", $"{Paper}=camel/od=d.dds", "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("camel/od=d.dds", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colour_that_is_not_three_numbers_refuses()
    {
        (int code, _, string error) = Run("--editordata", Source(), "--retint", $"{Paper}=1,0");

        Assert.Equal(2, code);
        Assert.Contains("three numbers", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Narrowing_a_retint_that_was_never_asked_for_refuses()
    {
        // Silently ignoring it would leave a user believing an edit was
        // narrowed when the whole model changed.
        (int code, _, string error) = Run(
            "--editordata", Source(), "--repoint", $"{Paper}=camel/mine.dds",
            "--only-tint", "0,0,0", "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("--only-tint narrows a --retint", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_without_a_destination_points_at_the_dry_run()
    {
        (int code, _, string error) = Run("--editordata", Source(), "--repoint", $"{Paper}=camel/mine.dds");

        Assert.Equal(2, code);
        Assert.Contains("--dry-run", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_says_which_repointed_textures_nothing_provides_yet()
    {
        // Named rather than described, and only the paths just typed: without
        // the archives every texture the model binds is equally unaccounted
        // for, and listing them all would bury the one that matters.
        (_, string output, _) = Run(
            "--editordata", Source(),
            "--repoint", $"{Paper}=camel/mods/tex_mine_d.dds",
            "--replaces", "camel/chr_test.editordata",
            "--out", Path.Combine(_directory.FullName, "mods"), "--name", "Test Mod");

        Assert.Contains("camel/mods/tex_mine_d.dds", output, StringComparison.Ordinal);
        Assert.Contains("--sdf-root", output, StringComparison.Ordinal);

        // Copyable, because the failure is a path typed twice and differing by
        // a character. A message inviting the user to retype it invites it again.
        Assert.Contains(
            "perianth texture --from YOURS.png --replaces camel/mods/tex_mine_d.dds",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifying_a_finished_mod_refuses_when_a_texture_is_provided_by_nothing()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        Run("--editordata", Source(), "--repoint", $"{Paper}=camel/mods/tex_mine_d.dds",
            "--replaces", "camel/chr_test.editordata", "--out", destination, "--name", "Test Mod");

        (int code, _, string error) = Run("--verify", Path.Combine(destination, "Test Mod"));

        Assert.Equal(2, code);
        Assert.Contains("provided by nothing", error, StringComparison.Ordinal);
        Assert.Contains("camel/mods/tex_mine_d.dds", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifying_passes_once_the_texture_is_in_the_mod()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        Run("--editordata", Source(), "--repoint", $"{Paper}=camel/mods/tex_mine_d.dds",
            "--replaces", "camel/chr_test.editordata", "--out", destination, "--name", "Test Mod");

        // Both of them: the fixture's third section binds a texture of its own,
        // and a check that stopped at the repointed one would be answering a
        // narrower question than --verify asks.
        foreach (string texture in new[] { "camel/mods/tex_mine_d.dds", "camel/other.dds" })
        {
            string carried = Path.Combine(
                destination, "Test Mod", texture.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(carried)!);
            File.WriteAllBytes(carried, [1, 2, 3]);
        }

        (int code, string output, _) = Run("--verify", Path.Combine(destination, "Test Mod"));

        Assert.Equal(0, code);
        Assert.Contains("Every texture they name is provided", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_restriction_that_binds_nothing_names_how_many_were_asked_for()
    {
        (int code, _, string error) = Run(
            "--editordata", Source(), "--repoint", $"{Paper}=camel/mine.dds",
            "--section", "2", "--dry-run");

        Assert.Equal(2, code);
        Assert.Contains("1 named sections", error, StringComparison.Ordinal);
    }
}
