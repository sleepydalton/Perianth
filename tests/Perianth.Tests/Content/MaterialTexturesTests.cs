using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Editordata;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks which textures a model binds, and the order worth showing them in.
/// </summary>
public sealed class MaterialTexturesTests
{
    private static EditordataFile File(params EditordataChannel[][] materials) =>
        new(
            "model.editordata",
            [.. materials.Select((channels, ordinal) => new EditordataSection(
                ordinal,
                [new EditordataMaterial("shader", "instance", [.. channels])],
                "intermediate",
                [],
                []))],
            CustomVersion: 3);

    private static EditordataChannel Bind(string channel, string path) => new(channel, path);

    [Fact]
    public void The_same_texture_bound_by_many_materials_is_listed_once()
    {
        // A character makes on the order of a thousand channel bindings naming
        // under a hundred files. Listing a binding apiece would show the same
        // picture ninety times.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("DiffuseColor", "textures/shared.dds")],
                [Bind("DiffuseColor", "textures/shared.dds")],
                [Bind("DiffuseColor", "textures/shared.dds")]),
            "model");

        Assert.Single(listed);
        Assert.Equal(3, listed[0].Bindings);
    }

    [Fact]
    public void The_models_own_textures_come_first()
    {
        // The first screenful should say what this is. A shared library scan
        // bound by the same material does not, however many parts use it.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("DiffuseColor", "textures/library/aaa_paper.dds")],
                [Bind("TransparentColor", "textures/library/cartman/tex_cartmanfrnt.dds")]),
            "cartman");

        Assert.Equal("textures/library/cartman/tex_cartmanfrnt.dds", listed[0].Path);
        Assert.True(listed[0].Own);
        Assert.False(listed[1].Own);
    }

    [Fact]
    public void A_variants_own_textures_are_found_under_the_base_name()
    {
        // chr_stan_var_hero is painted with tex_stan*, so the variant suffix has
        // to come off before the name is looked for.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File([Bind("DiffuseColor", "textures/library/stan/tex_stanfrnt.dds")]),
            "stan_var_hero");

        Assert.True(listed[0].Own);
    }

    [Fact]
    public void The_texture_painting_more_of_the_model_comes_first()
    {
        // Most characters have no texture of their own at all — one measured
        // model is built entirely from the shared library — so this is the
        // order usually seen. A facial sheet is bound once because one part
        // wears it; the skin and clothing colours are bound by a hundred parts,
        // and are what says who this is.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("DiffuseColor", "textures/aaa_one_mouth_shape.dds")],
                [Bind("DiffuseColor", "textures/zzz_skin.dds")],
                [Bind("DiffuseColor", "textures/zzz_skin.dds")]),
            "unrelated");

        Assert.Equal(
            ["textures/zzz_skin.dds", "textures/aaa_one_mouth_shape.dds"],
            listed.Select(texture => texture.Path));
    }

    [Fact]
    public void Textures_painting_equal_shares_are_ordered_by_path()
    {
        // Binding counts tie constantly — every mouth sheet is bound once — so
        // the tie-break is what actually decides most of the order, and the
        // grid must not shuffle between runs.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("TransparentColor", "textures/aaa.dds")],
                [Bind("DiffuseColor", "textures/zzz.dds")],
                [Bind("DiffuseColor", "textures/mmm.dds")]),
            "unrelated");

        Assert.Equal(
            ["textures/mmm.dds", "textures/zzz.dds", "textures/aaa.dds"],
            listed.Select(texture => texture.Path));
    }

    [Fact]
    public void An_unreadable_path_is_skipped_rather_than_refused_over()
    {
        // This is a listing for someone to look at. One binding naming a PNG
        // among a thousand is no reason to show them nothing; the export path
        // judges the same paths strictly and is where a refusal belongs.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("DiffuseColor", "textures/not-an-image.png")],
                [Bind("DiffuseColor", "textures/fine.dds")]),
            "model");

        Assert.Equal("textures/fine.dds", Assert.Single(listed).Path);
    }

    [Fact]
    public void An_unbound_channel_names_no_texture()
    {
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File([Bind("EmissiveColor", string.Empty), Bind("DiffuseColor", "textures/fine.dds")]),
            "model");

        Assert.Single(listed);
    }

    [Fact]
    public void A_path_differing_only_in_case_is_the_same_texture()
    {
        // The archive folds case because the container does, so two spellings
        // resolve to one file and must not be decoded and shown twice.
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File(
                [Bind("DiffuseColor", "textures/Shared.dds")],
                [Bind("DiffuseColor", "textures/shared.dds")]),
            "model");

        Assert.Equal(2, Assert.Single(listed).Bindings);
    }

    [Fact]
    public void An_empty_name_claims_nothing_as_the_models_own()
    {
        ImmutableArray<TextureReference> listed = MaterialTextures.List(
            File([Bind("DiffuseColor", "textures/anything.dds")]),
            string.Empty);

        Assert.False(listed[0].Own);
    }
}
