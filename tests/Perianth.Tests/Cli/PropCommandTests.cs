using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Perianth.Cli;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// The <c>prop</c> verb, over an invented layer on disk.
/// </summary>
/// <remarks>
/// The grammar and what it tells the author, rather than the placement, which
/// <c>PropPlaceTests</c> covers. The warnings are most of it: the template
/// decides more than the options do, and none of that is visible in the layer
/// afterwards.
/// </remarks>
public sealed class PropCommandTests : IDisposable
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

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-prop-");

    public void Dispose() => _directory.Delete(recursive: true);

    private const string Chunk =
        "{\n" +
        "\t{\n" +
        "\t\tmatrix = {\n\t\t\t1 0 0 1,\n\t\t\t0 1 0 2,\n\t\t\t0 0 1 3,\n\t\t\t0 0 0 1,\n\t\t},\n" +
        "\t\tname = \"made_up_sink\",\n" +
        "\t\tresource = F\"camel/graph objects/prop/made_up_sink.mgraphobject\",\n" +
        "\t\tsphereRadius = 30.41,\n" +
        "\t\ttype = \"Prop\",\n" +
        "\t\tuid = #0123456789ABCDEF0123456789ABCDEF,\n" +
        "\t},\n" +
        "}";

    private static readonly string LayerText =
        "{\n\tcontent = {\n\t\tquadTreeHeader = {\n\t\t\t[7] = {\n" +
        string.Create(CultureInfo.InvariantCulture, $"\t\t\t\toffset = 0,\n\t\t\t\tsize = {Chunk.Length},\n") +
        "\t\t\t},\n\t\t},\n\t},\n\theader = {\n\t\tentities = 2,\n\t},\n}\n\0" + Chunk;

    private const string VirtualPath =
        "camel/maps/00000000000000000000000000000000/entities/11111111111111111111111111111111/layerdata.mlayer";

    [Fact]
    public void Listing_says_what_each_entity_is_and_what_it_draws()
    {
        (int code, string output, _) = Run("--layer", Layer(), "--list");

        Assert.Equal(0, code);
        Assert.Contains("Prop", output, StringComparison.Ordinal);
        Assert.Contains("made_up_sink", output, StringComparison.Ordinal);
        Assert.Contains("made_up_sink.mgraphobject", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Placing_reports_the_template_it_copied_and_the_chunk_it_used()
    {
        (int code, string output, _) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "10,0,-5",
            "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("copied from 'made_up_sink'", output, StringComparison.Ordinal);
        Assert.Contains("chunk 0", output, StringComparison.Ordinal);
        Assert.Contains(VirtualPath, output, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", output, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_template_decided_is_said_out_loud()
    {
        // The culling bound is the sharp one, and it is the reason rendering the
        // mod offline is not enough here: an offline render does not cull.
        (_, _, string error) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "0,0,0",
            "--dry-run");

        Assert.Contains("sphereRadius", error, StringComparison.Ordinal);
        Assert.Contains("cull", error, StringComparison.Ordinal);
        Assert.Contains("named, not checked", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_one_thing_known_to_go_wrong_is_said_on_every_run()
    {
        // Roadmap §10.165: a layer written here has twice been installed and
        // honoured by nothing — the game read the file and drew none of the
        // layer's props, the eleven that were already there included. Every
        // check this tool can make passes, so the warning is the only thing
        // standing between an author and a lost room.
        (_, _, string error) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "0,0,0",
            "--dry-run");

        Assert.Contains("does not yet draw properly", error, StringComparison.Ordinal);
        Assert.Contains("unproven", error, StringComparison.Ordinal);
        Assert.Contains("backup", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Placing_without_the_four_things_it_needs_is_refused()
    {
        (int code, _, string error) = Run("--layer", Layer(), "--name", "brand_new_prop");

        Assert.NotEqual(0, code);
        Assert.Contains("--template", error, StringComparison.Ordinal);
        Assert.Contains("--list", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1,2")]
    [InlineData("1,2,3,4")]
    [InlineData("1,2,over there")]
    public void A_position_that_is_not_three_numbers_is_refused(string at)
    {
        (int code, _, string error) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", at,
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void A_layer_with_no_recorded_provenance_is_refused()
    {
        // Where the layer came from is read, never inferred: the archive path is
        // two uids long, and a mod written one folder out is one the game
        // ignores while looking as though it worked.
        string path = Path.Combine(_directory.FullName, "layerdata.mlayer");
        System.IO.File.WriteAllText(path, LayerText);

        (int code, _, string error) = Run(
            "--layer", path,
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "0,0,0",
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("archive path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_folder_holds_the_layer_at_its_own_archive_path()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        (int code, _, _) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "0,0,0",
            "--out", destination,
            "--mod-name", "Made Up Mod");

        Assert.Equal(0, code);
        Assert.True(System.IO.File.Exists(Path.Combine(
            destination,
            "Made Up Mod",
            Path.Combine(VirtualPath.Split('/')))));
    }

    [Fact]
    public void The_json_report_names_the_uid_and_the_path()
    {
        (_, string output, _) = Run(
            "--layer", Layer(),
            "--template", "made_up_sink",
            "--name", "brand_new_prop",
            "--graph-object", "camel/graph objects/prop/brand_new.mgraphobject",
            "--at", "0,0,0",
            "--dry-run",
            "--json");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("result").GetString());
        Assert.Equal(32, root.GetProperty("uid").GetString()!.Length);
        Assert.Equal(VirtualPath, root.GetProperty("path").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.NotEmpty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void A_run_without_a_layer_is_refused()
    {
        (int code, _, string error) = Run("--list");

        Assert.NotEqual(0, code);
        Assert.Contains("--layer", error, StringComparison.Ordinal);
    }

    /// <summary>Writes the layer and the manifest an extraction would leave beside it.</summary>
    private string Layer()
    {
        string path = Path.Combine(_directory.FullName, "layerdata.mlayer");
        System.IO.File.WriteAllText(path, LayerText);

        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(LayerText)))
            .ToLowerInvariant();

        System.IO.File.WriteAllText(
            Path.Combine(_directory.FullName, "perianth-extraction.json"),
            "{\"request\":\"made up\",\"files\":1,\"extracted\":[{" +
            $"\"path\":\"{VirtualPath}\",\"output\":\"layerdata.mlayer\"," +
            $"\"bytes\":{Encoding.UTF8.GetByteCount(LayerText)},\"sha256\":\"{digest}\",\"archives\":[0]}}]}}");

        return path;
    }

    private static (int Code, string Out, string Error) Run(params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = Program.Run(["prop", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }
}
