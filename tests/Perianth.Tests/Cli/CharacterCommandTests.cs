using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Perianth.Cli;
using Perianth.Formats.Binary;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// The <c>character</c> verb, over invented files on disk.
/// </summary>
/// <remarks>
/// The grammar and what it tells the author. The two halves of a character —
/// a graph object and a definition — are covered by <c>GraphEditTests</c> and
/// <c>CharacterEditTests</c>; what is here is that the verb wires them together,
/// and that it says the things a written mod cannot show.
/// </remarks>
public sealed class CharacterCommandTests : IDisposable
{
    // The identifiers below are invented -- repeated nibbles, and the hex
    // digits counted up and back down. A uid is 128 opaque bits, so a fixture
    // needs one that is well-formed rather than one that is real, and the
    // game's own belong here no more than its textures do.
    //
    // This line is read by the content scan, which cannot tell an invented
    // identifier from a real one and so takes the claim from whoever wrote it.
    //
    // scan-ok: identifiers here are invented

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-character-");

    public void Dispose() => _directory.Delete(recursive: true);

    private static readonly string[] Table =
    [
        "MMAFile",
        "made/up/model.mmb",
        "made/up/anim.manimsys",
        "made_up_asset",
    ];

    private const string Npc =
        "NPC made_up_template < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyUIName \"contextComment = \\\"\\\", description = \\\"made_up Name\\\", enabled = true, guid = #AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, lineVersion = 1, maxLength = 9, text = \\\"Made Up\\\"\"\n" +
        "\tmyGraphObjectFile \"camel/graph objects/actor/made_up.mgraphobject\"\n" +
        "\tmyFaction Allies\n" +
        "}\n";

    [Fact]
    public void Listing_shows_the_assets_the_graph_object_names()
    {
        (int code, string output, _) = Run("--graph-template", GraphFile(), "--list");

        Assert.Equal(0, code);
        Assert.Contains("made/up/model.mmb", output, StringComparison.Ordinal);

        // A bare name is not a path, and listing all 78 strings of a real actor
        // would bury the handful worth changing.
        Assert.DoesNotContain("made_up_asset", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_is_repointed_by_asking_the_template_which_entry_it_means()
    {
        (int code, string output, _) = Run(
            "--graph-template", GraphFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("made/up/model.mmb", output, StringComparison.Ordinal);
        Assert.Contains("-> brand/new/model.mmb", output, StringComparison.Ordinal);
        Assert.Contains(
            "camel/graph objects/actor/brand_new_hero.mgraphobject",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Both_halves_are_written_when_a_definition_template_is_given()
    {
        (int code, string output, _) = Run(
            "--graph-template", GraphFile(),
            "--npc-template", NpcFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("Would write 2 files", output, StringComparison.Ordinal);
        Assert.Contains(
            "camel/game system data/juice/ai/npc/brand_new_hero.mnpc",
            output,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_graph_object_with_nothing_naming_it_is_warned_about()
    {
        // Art with no character attached to it, which looks in the mod folder
        // exactly like a finished character.
        (_, _, string error) = Run(
            "--graph-template", GraphFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run");

        Assert.Contains("nothing that names it", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unverified_load_path_is_always_said()
    {
        (_, _, string error) = Run(
            "--graph-template", GraphFile(),
            "--npc-template", NpcFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run");

        Assert.Contains("unverified", error, StringComparison.Ordinal);
        Assert.Contains("875 of 1,824", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ambiguous_model_refuses_rather_than_choosing()
    {
        // Eleven shipped actors name two models. The refusal names the way out.
        (int code, _, string error) = Run(
            "--graph-template", GraphFile("made/up/second.mmb"),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("would be a guess", error, StringComparison.Ordinal);
        Assert.Contains("outright", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repoint_names_the_entry_when_the_convenience_cannot()
    {
        (int code, string output, _) = Run(
            "--graph-template", GraphFile("made/up/second.mmb"),
            "--name", "brand_new_hero",
            "--repoint", "made/up/second.mmb=brand/new/model.mmb",
            "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("-> brand/new/model.mmb", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("no-equals-sign")]
    [InlineData("=missing-left")]
    [InlineData("missing-right=")]
    public void A_repoint_that_is_not_a_move_is_refused(string move)
    {
        (int code, _, string error) = Run(
            "--graph-template", GraphFile(),
            "--name", "brand_new_hero",
            "--repoint", move,
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_run_with_nothing_to_draw_is_refused()
    {
        (int code, _, string error) = Run(
            "--graph-template", GraphFile(), "--name", "brand_new_hero", "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("--model", error, StringComparison.Ordinal);
        Assert.Contains("--list", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_without_a_graph_template_is_refused()
    {
        (int code, _, string error) = Run("--name", "brand_new_hero");

        Assert.NotEqual(0, code);
        Assert.Contains("--graph-template", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_folder_holds_both_files_at_the_games_own_paths()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        (int code, _, _) = Run(
            "--graph-template", GraphFile(),
            "--npc-template", NpcFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--out", destination,
            "--mod-name", "Made Up Mod");

        Assert.Equal(0, code);
        string folder = Path.Combine(destination, "Made Up Mod");
        Assert.True(System.IO.File.Exists(Path.Combine(
            folder, "camel", "graph objects", "actor", "brand_new_hero.mgraphobject")));
        Assert.True(System.IO.File.Exists(Path.Combine(
            folder, "camel", "game system data", "juice", "ai", "npc", "brand_new_hero.mnpc")));
    }

    [Fact]
    public void The_json_report_names_the_uid_and_what_moved()
    {
        (_, string output, _) = Run(
            "--graph-template", GraphFile(),
            "--npc-template", NpcFile(),
            "--name", "brand_new_hero",
            "--model", "brand/new/model.mmb",
            "--dry-run",
            "--json");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("result").GetString());
        Assert.Equal(32, root.GetProperty("uid").GetString()!.Length);
        Assert.Single(root.GetProperty("repointed").EnumerateArray());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
        Assert.NotEmpty(root.GetProperty("warnings").EnumerateArray());
    }

    /// <summary>
    /// Writes a BVM container whose table holds <see cref="Table"/> and any
    /// further entries given, each referenced once by the graph.
    /// </summary>
    private string GraphFile(params string[] extra)
    {
        List<string> table = [.. Table, .. extra];
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        CompactInteger.Write(bytes, (uint)table.Count);

        foreach (string text in table)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            CompactInteger.Write(bytes, (uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        // One container holding a reference to every entry, so each is used.
        bytes.AddRange([0x01, (byte)table.Count, 0x00]);
        for (int i = 0; i < table.Count; i++)
        {
            bytes.AddRange([0x0d, (byte)i]);
        }

        string path = Path.Combine(_directory.FullName, $"made-up-{table.Count}.mgraphobject");
        System.IO.File.WriteAllBytes(path, [.. bytes]);
        return path;
    }

    private string NpcFile()
    {
        string path = Path.Combine(_directory.FullName, "made-up.mnpc");
        System.IO.File.WriteAllText(path, Npc);
        return path;
    }

    private static (int Code, string Out, string Error) Run(params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = Program.Run(["character", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }
}
