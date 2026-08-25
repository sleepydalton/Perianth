using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Perianth.Cli;
using Xunit;

namespace Perianth.Tests.Cli;

/// <summary>
/// The <c>item</c> verb, over invented files on disk.
/// </summary>
/// <remarks>
/// <para>
/// The grammar and what it tells the author, rather than the edits — those are
/// <c>ItemEditTests</c> and the gated corpus oracle. What is tested here is
/// mostly the <em>warnings</em>, because every one of them names something that
/// is invisible in the written mod: an item nothing sells looks exactly like one
/// that is for sale, a display name with no localisation row looks like a name,
/// and a copied parent entry looks like a copied hairstyle.
/// </para>
/// <para>
/// The economy files each need a provenance manifest beside them, since the verb
/// reads an archive path rather than inferring one. That is the behaviour under
/// test in <see cref="An_input_with_no_recorded_provenance_is_refused"/>.
/// </para>
/// </remarks>
public sealed class ItemCommandTests : IDisposable
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

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-item-");

    public void Dispose() => _directory.Delete(recursive: true);

    private const string Template =
        "CostumeItemStreetHairLow made_up_template_hair_low < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyUIName \"contextComment = \\\"made_up\\\", description = \\\"made_up\\\", enabled = true, guid = #AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, lineVersion = 0, maxLength = 7, text = \\\"Old Hat\\\"\"\n" +
        "\tmyModel \"[\\\"prefab:Skeleton\\\"] = { MMAFile = \\\"made/up/old.mmb\\\", }, [\\\"prefab:UberMeshCamel\\\"] = { ModelFile = \\\"made/up/old.mmb\\\", }\"\n" +
        "}\n";

    private const string Parent =
        "CostumeItemStreetHair made_up_parent < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyModel \"[\\\"prefab:Skeleton\\\"] = { MMAFile = \\\"made/up/old.mmb\\\", }\"\n" +
        "\tmyExtraPieces\n\t{\n" +
        "\t\tCostumeItemStreetHairLow \"A\" < uid=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA > = made_up_low\n" +
        "\t\tCostumeItemStreetHairTop \"B\" < uid=BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB > = made_up_top\n" +
        "\t}\n}\n";

    private const string Inventory =
        "StartingInventory made_up\n" +
        "{\n\tmySettings\n\t{\n" +
        "\t\tStartingInventorySetting MadeUpSettings\n\t\t{\n\t\t\tmyItemList\n\t\t\t{\n\t\t\t}\n\t\t}\n" +
        "\t}\n}\n";

    [Fact]
    public void An_item_is_written_under_its_own_name_and_nothing_else_is_claimed()
    {
        (int code, string output, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--dry-run");

        Assert.Equal(0, code);

        // The path is the lookup, not a convention: the executable turns an
        // item's name into exactly this path.
        Assert.Contains(
            "camel/game system data/juice/items/brand_new_hat.mitem",
            output,
            StringComparison.Ordinal);
        Assert.Contains("Nothing was written", output, StringComparison.Ordinal);

        // Declared but unobtainable, which is invisible in the file itself.
        Assert.Contains("unobtainable", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_in_capitals_is_written_lower_case()
    {
        (_, string output, _) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "Brand_New_Hat",
            "--model", "made/up/new.mmb",
            "--dry-run");

        Assert.Contains("items/brand_new_hat.mitem", output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_display_name_without_a_locpack_is_warned_about()
    {
        // The item carries the name, and nothing resolves it. Both files look
        // correct on their own, so only the tool can say this.
        (_, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--display-name", "Brand New Hat",
            "--dry-run");

        Assert.Contains("--locpack", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Copying_a_parent_entry_says_that_its_variants_came_too()
    {
        (_, _, string error) = Run(
            "--template", Write("parent.mitem", Parent),
            "--name", "brand_new_hair",
            "--model", "made/up/new.mmb",
            "--dry-run");

        Assert.Contains("2 variants", error, StringComparison.Ordinal);
        Assert.Contains("copy one of the variants instead", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_route_named_by_half_is_refused_rather_than_guessed()
    {
        // One vendor file holds forty shops, so "sell it" without saying where
        // would mean "put it in whichever comes first".
        (int code, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--vendors", Write("vendors.mvendorconfig", "x"),
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("--shop", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_input_with_no_recorded_provenance_is_refused()
    {
        // Where an economy file came from is read, never inferred: a mod written
        // one folder out is one the game silently ignores.
        (int code, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--inventory", Write("starting_inventory.juice", Inventory),
            "--setting", "MadeUpSettings",
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("archive path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grant_lands_at_the_archive_path_the_extraction_recorded()
    {
        string inventory = Write("starting_inventory.juice", Inventory);
        Provenance("starting_inventory.juice", "camel/made/up/starting_inventory.juice", Inventory);

        (int code, string output, _) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--inventory", inventory,
            "--setting", "MadeUpSettings",
            "--count", "3",
            "--dry-run");

        Assert.Equal(0, code);
        Assert.Contains("camel/made/up/starting_inventory.juice", output, StringComparison.Ordinal);
        Assert.Contains("granted 3 through 'MadeUpSettings'", output, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unverified_load_path_is_always_said()
    {
        // Never conditional. No amount of correct authoring settles whether the
        // game reads an item file the archives never held, and an author about
        // to spend a loader use should know which part is the guess.
        (_, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--dry-run");

        Assert.Contains("unverified", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Writing_a_mod_needs_somewhere_to_put_it()
    {
        (int code, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb");

        Assert.NotEqual(0, code);
        Assert.Contains("--out", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_folder_holds_the_item_at_the_games_own_path()
    {
        string destination = Path.Combine(_directory.FullName, "mods");

        (int code, _, _) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--out", destination,
            "--mod-name", "Made Up Mod");

        Assert.Equal(0, code);
        Assert.True(System.IO.File.Exists(Path.Combine(
            destination, "Made Up Mod", "camel", "game system data", "juice", "items", "brand_new_hat.mitem")));
        Assert.True(System.IO.File.Exists(Path.Combine(destination, "Made Up Mod", "manifest.ini")));
    }

    [Fact]
    public void The_json_report_names_the_uid_a_route_must_use()
    {
        (_, string output, _) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--dry-run",
            "--json");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;

        Assert.Equal("ok", root.GetProperty("result").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(32, root.GetProperty("uid").GetString()!.Length);
        Assert.NotEmpty(root.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void An_ingredient_that_is_not_uid_and_count_is_refused()
    {
        (int code, _, string error) = Run(
            "--template", Write("template.mitem", Template),
            "--name", "brand_new_hat",
            "--model", "made/up/new.mmb",
            "--ingredient", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA:many",
            "--dry-run");

        Assert.NotEqual(0, code);
        Assert.Contains("UID:count", error, StringComparison.Ordinal);
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(_directory.FullName, name);
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Writes the manifest an extraction would have left beside a file.</summary>
    private void Provenance(string name, string virtualPath, string text)
    {
        string digest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        Write("perianth-extraction.json",
            "{\"request\":\"made up\",\"files\":1,\"extracted\":[{" +
            $"\"path\":\"{virtualPath}\",\"output\":\"{name}\"," +
            $"\"bytes\":{Encoding.UTF8.GetByteCount(text)},\"sha256\":\"{digest}\",\"archives\":[0]}}]}}");
    }

    private static (int Code, string Out, string Error) Run(params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        int code = Program.Run(["item", .. arguments], output, error);
        return (code, output.ToString(), error.ToString());
    }
}
