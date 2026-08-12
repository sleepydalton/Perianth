using System;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks changing what a model's parts are painted with.
/// </summary>
/// <remarks>
/// The two operations act on disjoint halves of a real model — Roadmap §6.11 —
/// so most of what is checked here is that each one changes exactly the parts
/// it claims and leaves the rest byte-identical. An edit that quietly matched
/// nothing, or matched more than it said, would write a mod that looks like it
/// worked.
/// </remarks>
public sealed class MaterialEditTests
{
    private const string Paper = @"camel\baked\assets\textures\paperscans512\tex_ashgray_d.dds";
    private const string White = @"camel\baked\assets\textures\tex_white16_d.dds";
    private const string Replacement = "camel/mods/tex_myblue_d.dds";

    // W is deliberately not 1: a test that its value survives is inert if the
    // fixture holds whatever a bug would overwrite it with. Mutating the
    // writer to set W = 1 passed against a 1-valued fixture.
    private static EditordataCustomRecord Record(Rgb tint, float w = 0.25f) => new(
        Version: 3,
        Slot10: new Float4((float)tint.R, (float)tint.G, (float)tint.B, w),
        Slot20: new Float4(0, 0, 0, 1),
        UvRepeat: new Float2(1, 1),
        Slot30: new Float4(1, 1, 1, 1),
        Slot40: default,
        Slot50: default,
        Slot60: default);

    private static EditordataSection Section(int ordinal, string diffuse, Rgb tint) => new(
        ordinal,
        [new EditordataMaterial(
            $"mat{ordinal}",
            "CamelDefaultShader",
            [new EditordataChannel("DiffuseColor", diffuse),
             new EditordataChannel("TransparentColor", "")])],
        "intermediate",
        [.. new byte[12]],
        [Record(tint)]);

    /// <summary>
    /// A section drawing one texture while naming another in a channel that is
    /// not sampled for albedo — the shape every real section has, and the one
    /// that makes "binds it" and "draws it" different questions.
    /// </summary>
    private static EditordataSection Placeholder(
        int ordinal, string diffuse, string elsewhere, Rgb tint) => new(
        ordinal,
        [new EditordataMaterial(
            $"mat{ordinal}",
            "CamelDefaultShader",
            [new EditordataChannel("DiffuseColor", diffuse),
             new EditordataChannel("NormalMap", elsewhere),
             new EditordataChannel("SpecularColor", elsewhere)])],
        "intermediate",
        [.. new byte[12]],
        [Record(tint)]);

    /// <summary>
    /// Three parts of paper and two blank sheets, which is the shape a real
    /// file has: colour in the texture for one half, colour in the tint for the
    /// other.
    /// </summary>
    private static EditordataFile Model() => new(
        "chr_test.editordata",
        [
            Section(0, Paper, new Rgb(1, 1, 1)),
            Section(1, White, new Rgb(0, 0, 0)),
            Section(2, Paper, new Rgb(1, 1, 1)),
            Section(3, White, new Rgb(0.5, 0.5, 0.5)),
            Section(4, Paper, new Rgb(1, 1, 1)),
        ],
        CustomVersion: 3);

    private static string Diffuse(EditordataFile file, int section) =>
        file.Sections[section].Materials[0].Channels
            .First(channel => channel.Channel == "DiffuseColor").TexturePath;

    private static Rgb Tint(EditordataFile file, int section)
    {
        Float4 slot = file.Sections[section].CustomRecords[0].Slot10;
        return new Rgb(slot.X, slot.Y, slot.Z);
    }

    private static MaterialEditOutcome Ok(Result<MaterialEditOutcome> result)
    {
        Assert.False(result.IsRefused, result.IsRefused ? result.Refusal.Message : null);
        return result.Value;
    }

    [Fact]
    public void Repointing_changes_every_section_that_binds_the_texture()
    {
        MaterialEditOutcome outcome = Ok(MaterialEdit.Repoint(Model(), Paper, Replacement));

        Assert.Equal(3, outcome.Sections);
        Assert.Equal(3, outcome.Bindings);
        Assert.Equal(Replacement.Replace('/', '\\'), Diffuse(outcome.File, 0));
        Assert.Equal(Replacement.Replace('/', '\\'), Diffuse(outcome.File, 4));

        // And nothing else. The white sheets are a different half of the model.
        Assert.Equal(White, Diffuse(outcome.File, 1));
    }

    [Fact]
    public void A_repointed_path_is_spelled_the_way_the_one_it_replaces_was()
    {
        // The shipped files use backslashes and the engine loads them. Imposing
        // forward slashes on a file we are only partly rewriting would be our
        // convention overriding the one demonstrably working.
        MaterialEditOutcome backslashes = Ok(MaterialEdit.Repoint(Model(), Paper, Replacement));
        Assert.DoesNotContain('/', Diffuse(backslashes.File, 0));

        EditordataFile forwardSlashes = Model() with
        {
            Sections = [Section(0, Paper.Replace('\\', '/'), new Rgb(1, 1, 1))],
        };

        MaterialEditOutcome kept = Ok(MaterialEdit.Repoint(forwardSlashes, Paper, Replacement));
        Assert.DoesNotContain('\\', Diffuse(kept.File, 0));
    }

    [Fact]
    public void A_path_matches_whatever_way_it_is_spelled()
    {
        // A user copies a path out of a listing, which normalizes separators
        // and may differ in case. It still names the same file.
        MaterialEditOutcome outcome = Ok(MaterialEdit.Repoint(
            Model(), Paper.Replace('\\', '/').ToUpperInvariant(), Replacement));

        Assert.Equal(3, outcome.Bindings);
    }

    [Fact]
    public void Repointing_can_be_aimed_at_named_sections()
    {
        MaterialEditOutcome outcome = Ok(MaterialEdit.Repoint(Model(), Paper, Replacement, [2]));

        Assert.Equal(1, outcome.Sections);
        Assert.Equal(Paper, Diffuse(outcome.File, 0));
        Assert.Equal(Replacement.Replace('/', '\\'), Diffuse(outcome.File, 2));
    }

    [Fact]
    public void Retinting_changes_only_the_parts_carrying_that_tint()
    {
        // 36 distinct tints share tex_white16 and black is 86% of them — the
        // ink line work. Recolouring every tint at once flattens the drawing,
        // so naming the tint being replaced is the useful grain.
        MaterialEditOutcome outcome = Ok(MaterialEdit.Retint(
            Model(), White, new Rgb(0, 0, 0), new Rgb(0.1, 0.2, 0.8)));

        Assert.Equal(1, outcome.Sections);
        Assert.Equal(new Rgb(0.5, 0.5, 0.5), Tint(outcome.File, 3));

        Rgb changed = Tint(outcome.File, 1);
        Assert.Equal(0.1, changed.R, 5);
        Assert.Equal(0.8, changed.B, 5);
    }

    [Fact]
    public void Retinting_every_tint_is_possible_when_asked_for()
    {
        MaterialEditOutcome outcome = Ok(MaterialEdit.Retint(
            Model(), White, replacing: null, new Rgb(1, 0, 0)));

        Assert.Equal(2, outcome.Sections);
    }

    [Fact]
    public void Naming_parts_proposes_a_path_of_their_own()
    {
        // Reported by a user: two parts of one paper sheet given two different
        // images, and both ended up with the second. Without the parts in the
        // name, one texture proposes one path however many parts are aimed at,
        // so the second image lands on the first one's path and the part
        // already pointed there changes with it.
        string whole = MaterialEdit.ProposePath("chr_test", Paper);
        string partA = MaterialEdit.ProposePath("chr_test", Paper, [47]);
        string partB = MaterialEdit.ProposePath("chr_test", Paper, [51]);

        Assert.NotEqual(partA, partB);
        Assert.NotEqual(whole, partA);

        // The whole-texture proposal is unchanged, so this adds a case rather
        // than moving the existing one.
        Assert.Equal("camel/baked/assets/textures/perianth/chr_test/tex_ashgray_d.dds", whole);
        Assert.EndsWith("tex_ashgray_d_part_47.dds", partA, StringComparison.Ordinal);
    }

    [Fact]
    public void One_set_of_parts_is_one_path_however_it_was_typed()
    {
        // Two spellings of the same aim must not become two textures, or
        // correcting an image would leave the first one bound to half the parts.
        Assert.Equal(
            MaterialEdit.ProposePath("chr_test", Paper, [47, 51]),
            MaterialEdit.ProposePath("chr_test", Paper, [51, 47]));
    }

    [Fact]
    public void Retinting_ignores_a_texture_named_off_the_diffuse_channel()
    {
        // The fault this pins cost a real in-game test. tex_white16_d.dds is
        // both the blank sheet Retint exists for and the placeholder sitting in
        // the other four channels of nearly every section in the game, so
        // selecting any-channel recoloured an entire prop while truthfully
        // reporting the count. Across 2,272 corpus files it is the only texture
        // both drawn somewhere and named off-diffuse somewhere — Roadmap §6.14.
        EditordataFile file = new(
            "chr_test.editordata",
            [
                Section(0, White, new Rgb(0, 0, 0)),
                Placeholder(1, Paper, White, new Rgb(1, 1, 1)),
            ],
            CustomVersion: 3);

        MaterialEditOutcome outcome = Ok(MaterialEdit.Retint(
            file, White, replacing: null, new Rgb(1, 0, 0)));

        Assert.Equal(1, outcome.Sections);
        Assert.Equal(new Rgb(1, 0, 0), Tint(outcome.File, 0));
        Assert.Equal(new Rgb(1, 1, 1), Tint(outcome.File, 1));

        // Repoint keeps every binding, because a texture named in NormalMap
        // really is bound there and moving only the diffuse one would leave a
        // model naming a file the author meant to replace.
        MaterialEditOutcome moved = Ok(MaterialEdit.Repoint(file, White, Replacement));
        Assert.Equal(2, moved.Sections);
    }

    [Fact]
    public void Retinting_keeps_the_unresolved_w_component()
    {
        // Slot10's W is not documented as anything. Writing it would be
        // inventing a value for a field nobody has read out of the engine.
        EditordataFile file = Model();
        float before = file.Sections[1].CustomRecords[0].Slot10.W;

        MaterialEditOutcome outcome = Ok(MaterialEdit.Retint(
            file, White, replacing: null, new Rgb(1, 0, 0)));

        Assert.Equal(before, outcome.File.Sections[1].CustomRecords[0].Slot10.W);
    }

    [Fact]
    public void An_edit_leaves_the_original_alone()
    {
        EditordataFile file = Model();
        Ok(MaterialEdit.Repoint(file, Paper, Replacement));
        Ok(MaterialEdit.Retint(file, White, null, new Rgb(1, 0, 0)));

        Assert.Equal(Paper, Diffuse(file, 0));
        Assert.Equal(new Rgb(0, 0, 0), Tint(file, 1));
    }

    [Fact]
    public void An_edited_file_still_writes()
    {
        // The edit is only useful if the writer will take it, and the writer
        // refuses several things a hand-assembled record can hold.
        MaterialEditOutcome outcome = Ok(MaterialEdit.Repoint(Model(), Paper, Replacement));
        Result<byte[]> written = EditordataWriter.Write(outcome.File);

        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : null);
    }

    [Fact]
    public void Bindings_report_the_tint_beside_the_path()
    {
        // What lets a front end offer the operation that will do something:
        // a (1,1,1) tint on a paper scan means the colour is in the image.
        ImmutableArray<MaterialBinding> bindings = MaterialEdit.Bindings(Model());

        Assert.Equal(5, bindings.Length);
        Assert.All(bindings, binding => Assert.Equal("DiffuseColor", binding.Channel));
        Assert.Equal(new Rgb(1, 1, 1), bindings[0].Tint);
        Assert.Equal(new Rgb(0, 0, 0), bindings[1].Tint);
    }

    // --- Painting a named part, whatever it carried.

    [Fact]
    public void A_named_part_takes_the_texture_whatever_it_had()
    {
        // The operation Repoint cannot express: naming the part already says
        // which binding is meant, so there is nothing to be told to replace.
        MaterialEditOutcome outcome = Ok(MaterialEdit.Bind(
            Model(), [1, 4], "DiffuseColor", Replacement));

        Assert.Equal(2, outcome.Sections);
        Assert.Equal(Replacement.Replace('/', '\\'), Diffuse(outcome.File, 1));
        Assert.Equal(Replacement.Replace('/', '\\'), Diffuse(outcome.File, 4));

        // Section 1 was a white sheet and section 4 was paper: what they held
        // is irrelevant, which is the whole difference from repointing.
        Assert.Equal(White, Diffuse(Model(), 1));
        Assert.Equal(Paper, Diffuse(Model(), 4));
    }

    [Fact]
    public void Only_the_named_parts_change()
    {
        MaterialEditOutcome outcome = Ok(MaterialEdit.Bind(Model(), [1], "DiffuseColor", Replacement));

        Assert.Equal(1, outcome.Sections);
        Assert.Equal(Paper, Diffuse(outcome.File, 0));
        Assert.Equal(White, Diffuse(outcome.File, 3));
    }

    [Fact]
    public void Painting_a_channel_no_part_declares_is_refused()
    {
        // Adding the channel would invent a binding the material never had,
        // and the shader samples what the material declares.
        Result<MaterialEditOutcome> result = MaterialEdit.Bind(
            Model(), [0], "NoSuchChannel", Replacement);

        Assert.True(result.IsRefused);
        Assert.Contains("NoSuchChannel", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Painting_no_parts_at_all_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Bind(
            Model(), [], "DiffuseColor", Replacement);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public void Painting_a_part_outside_the_file_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Bind(
            Model(), [99], "DiffuseColor", Replacement);

        Assert.True(result.IsRefused);
        Assert.Contains("5 sections", result.Refusal.Message, StringComparison.Ordinal);
    }

    // --- Proposing a path for a texture only one model uses.

    [Theory]
    [InlineData("chr_cartman", @"camel\baked\assets\textures\tex_skin_d.dds",
        "camel/baked/assets/textures/perianth/chr_cartman/tex_skin_d.dds")]
    [InlineData("chr_cartman", "camel/x/tex_skin.dds",
        "camel/baked/assets/textures/perianth/chr_cartman/tex_skin.dds")]
    public void A_proposed_path_keeps_the_model_and_the_texture_apart(
        string model, string original, string expected)
    {
        Assert.Equal(expected, MaterialEdit.ProposePath(model, original));
    }

    [Fact]
    public void A_proposed_path_is_one_the_resolution_rule_accepts()
    {
        // It is about to be written into a material record, so a name holding
        // a space or a colon would produce a binding nothing can resolve.
        string proposed = MaterialEdit.ProposePath("Chr Cartman: v2", @"tex_a b:c.dds");

        Assert.False(TexturePath.Normalize(proposed, "DiffuseColor").IsRefused);
        Assert.Equal("camel/baked/assets/textures/perianth/chr_cartman__v2/tex_a_b_c.dds", proposed);
    }

    [Fact]
    public void Two_names_differing_only_in_punctuation_stay_apart()
    {
        // Removing the offending characters instead of replacing them would
        // collapse "a.b" and "ab" onto one path, and silently onto one texture.
        Assert.NotEqual(
            MaterialEdit.ProposePath("m", "a.b.dds"),
            MaterialEdit.ProposePath("m", "ab.dds"));
    }

    [Fact]
    public void A_name_with_nothing_usable_in_it_still_makes_a_path()
    {
        string proposed = MaterialEdit.ProposePath("///", "***.dds");

        Assert.Equal("camel/baked/assets/textures/perianth/model/texture.dds", proposed);
        Assert.False(TexturePath.Normalize(proposed, "DiffuseColor").IsRefused);
    }

    // --- What it refuses.

    [Fact]
    public void Repointing_a_texture_nothing_binds_is_refused()
    {
        // The single most likely mistake: a mistyped or stale path. Writing an
        // unchanged file would produce a mod indistinguishable from one that
        // worked, and the user would look for the fault in the game.
        Result<MaterialEditOutcome> result = MaterialEdit.Repoint(
            Model(), "camel/nothing/here.dds", Replacement);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("Nothing in this editordata binds", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repointing_in_sections_that_do_not_bind_it_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Repoint(Model(), Paper, Replacement, [1, 3]);

        Assert.True(result.IsRefused);
        Assert.Contains("2 named sections", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_section_outside_the_file_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Repoint(Model(), Paper, Replacement, [99]);

        Assert.True(result.IsRefused);
        Assert.Contains("5 sections", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Repointing_at_nothing_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Repoint(Model(), Paper, "");

        Assert.True(result.IsRefused);
        Assert.Contains("needs a path to bind", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retinting_a_file_with_no_custom_data_is_refused()
    {
        // Adding a tail would mean choosing a version and writing a record for
        // every section, which is authoring a structure the file never had.
        EditordataFile bare = new(
            "f",
            [new EditordataSection(0, [new EditordataMaterial("m", "s",
                [new EditordataChannel("DiffuseColor", White)])], "i", [.. new byte[12]], [])],
            CustomVersion: null);

        Result<MaterialEditOutcome> result = MaterialEdit.Retint(bare, White, null, new Rgb(1, 0, 0));

        Assert.True(result.IsRefused);
        Assert.Contains("no custom data", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Retinting_a_tint_nothing_carries_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Retint(
            Model(), White, new Rgb(0.25, 0.25, 0.25), new Rgb(1, 0, 0));

        Assert.True(result.IsRefused);
    }

    [Fact]
    public void A_tint_that_is_not_a_colour_is_refused()
    {
        Result<MaterialEditOutcome> result = MaterialEdit.Retint(
            Model(), White, null, new Rgb(double.NaN, 0, 0));

        Assert.True(result.IsRefused);
        Assert.Contains("finite", result.Refusal.Message, StringComparison.Ordinal);
    }
}
