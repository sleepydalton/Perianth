using System;
using System.Text;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Deriving a character definition from one the game ships.
/// </summary>
/// <remarks>
/// The fixtures are invented, as the repository requires — no shipped name, uid,
/// oasis id or path belongs here. The shapes are the measured ones: an
/// <c>NPC</c> declaration with a <c>myGraphObjectFile</c>, one deriving from
/// another with <c>: Parent</c>, and a <c>myUIName</c> blob identical to an
/// item's.
/// </remarks>
public sealed class CharacterEditTests
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

    private const string Npc =
        "include ./made_up_includes.juice\n" +
        "\n" +
        "NPC made_up_template < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n" +
        "\tmyUIName \"contextComment = \\\"\\\", description = \\\"made_up Name\\\", enabled = true, guid = #AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA, lineVersion = 1, maxLength = 9, text = \\\"Made Up\\\"\"\n" +
        "\tmyGraphObjectFile \"camel/graph objects/actor/made_up.mgraphobject\"\n" +
        "\tmyBehavior \"camel/game system data/juice/ai/behavior/made_up.mbehavior\"\n" +
        "\tmyFaction Allies\n" +
        "}\n";

    private const string Derived =
        "NPC made_up_child < uid=FEDCBA9876543210FEDCBA9876543210 > : made_up_template\n" +
        "{\n" +
        "\tmyGraphObjectFile \"camel/graph objects/actor/made_up.mgraphobject\"\n" +
        "}\n";

    private const string NoGraph =
        "NPCTuningData made_up_tuning < uid=0123456789ABCDEF0123456789ABCDEF >\n" +
        "{\n\tmyFaction Allies\n}\n";

    private static SourceFile File(string text) =>
        SourceFile.FromMemory("made-up.mnpc", Encoding.Latin1.GetBytes(text));

    private static Result<CharacterDerivation> Derive(
        string source = Npc,
        string name = "brand_new_hero",
        string graph = "camel/graph objects/actor/brand_new.mgraphobject",
        string? displayName = null) =>
        CharacterEdit.Derive(File(source), name, graph, displayName);

    private static string Text(CharacterDerivation derivation) =>
        Encoding.Latin1.GetString(derivation.Npc.Span);

    [Fact]
    public void The_declaration_is_renamed_and_pointed_at_a_new_graph_object()
    {
        CharacterDerivation made = Derive().Value;
        string text = Text(made);

        Assert.Contains($"NPC brand_new_hero < uid={made.Uid} >", text, StringComparison.Ordinal);
        Assert.Contains(
            "\tmyGraphObjectFile \"camel/graph objects/actor/brand_new.mgraphobject\"\n",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("actor/made_up.mgraphobject", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Fields_the_operation_does_not_understand_survive_exactly()
    {
        // The reason a template is copied. These declarations draw on a schema of
        // 887 classes and carry up to thirty fields.
        string text = Text(Derive().Value);

        Assert.StartsWith("include ./made_up_includes.juice\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "\tmyBehavior \"camel/game system data/juice/ai/behavior/made_up.mbehavior\"\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("\tmyFaction Allies\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_display_name_moves_the_guid_as_well_as_the_text()
    {
        // Exactly as an item's does, through the same locpack. Keeping the
        // template's guid would make both share a name.
        CharacterDerivation made = Derive(displayName: "Brand New Hero").Value;
        string text = Text(made);

        Assert.DoesNotContain("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", text, StringComparison.Ordinal);
        Assert.Contains("text = \\\"Brand New Hero\\\"", text, StringComparison.Ordinal);
        Assert.Equal(ItemEdit.MintUid("brand_new_hero name"), made.NameGuid);
        Assert.Contains($"guid = #{made.NameGuid}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_display_name_nothing_claims_one()
    {
        CharacterDerivation made = Derive().Value;

        Assert.Null(made.NameGuid);
        Assert.Null(made.DisplayName);
        Assert.Contains("text = \\\"Made Up\\\"", Text(made), StringComparison.Ordinal);
    }

    [Fact]
    public void An_inherited_declaration_keeps_its_parent_and_says_so()
    {
        // 652 of 1,827 shipped declarations derive from another, and a copy keeps
        // the clause — so the new character inherits whatever the template did,
        // including fields the copy never mentions. Flattening it would mean
        // reading the parent, which means reading the schema.
        CharacterDerivation made = Derive(source: Derived).Value;

        Assert.Equal("made_up_template", made.Inherits);
        Assert.Contains(
            $"NPC brand_new_hero < uid={made.Uid} > : made_up_template",
            Text(made),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_template_without_a_parent_claims_none() =>
        Assert.Null(Derive().Value.Inherits);

    [Fact]
    public void A_template_that_draws_nothing_is_refused()
    {
        // 181 of 1,824 shipped declarations carry no graph object, and copying
        // one to make a character that draws is a mistake this can see and the
        // author cannot, once the file is written.
        Result<CharacterDerivation> result = Derive(source: NoGraph);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("myGraphObjectFile", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("camel/baked/assets/characters/made_up.mmb")]
    [InlineData("camel/graph objects/actor/made_up")]
    public void A_graph_object_that_is_not_one_is_refused(string path)
    {
        // The commonest mistake here: the graph object names the model, so
        // handing this the .mmb produces a character that draws nothing.
        Result<CharacterDerivation> result = Derive(graph: path);

        Assert.False(result.IsSuccess);
        Assert.Contains(".mgraphobject", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_name_is_refused() => Assert.False(Derive(name: string.Empty).IsSuccess);

    [Fact]
    public void The_uid_is_minted_from_the_name_and_never_the_templates()
    {
        Assert.Equal(Derive().Value.Uid, Derive().Value.Uid);
        Assert.NotEqual(Derive().Value.Uid, Derive(name: "other").Value.Uid);
        Assert.NotEqual("0123456789ABCDEF0123456789ABCDEF", Derive().Value.Uid);
    }

    [Fact]
    public void The_proposed_path_is_a_convention_and_lowercases_the_name()
    {
        // Unlike an item's, which is a lookup the executable performs. An
        // .mnpc's file name is not its declared name on 875 of 1,824 shipped
        // files, so this is what most of them do and not a rule.
        Assert.Equal(
            "camel/game system data/juice/ai/npc/brand_new_hero.mnpc",
            CharacterEdit.ProposePath("Brand_New_Hero"));
    }
}
