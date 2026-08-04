using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Core.Materials;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Tests.Dds;
using Perianth.Tests.Editordata;
using Xunit;

namespace Perianth.Tests.Materials;

public sealed class MaterialAssemblerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-mat-");
    private readonly DirectoryInfo _loose;

    public MaterialAssemblerTests()
    {
        _loose = _directory.CreateSubdirectory("content");
    }

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void An_opaque_material_resolves_its_texture_and_records_a_default_factor()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().Section(
                MaterialSpec.Standard(name: "surface", diffuse: "tex/a.dds")));

        SurfaceMaterial material = Assert.Single(set.Materials);
        Assert.Equal("surface", material.Name);
        Assert.Equal(0, material.ImageIndex);
        Assert.False(material.IsTransparent);
        Assert.Equal(ColorRgba.White, material.BaseColorFactor);
        Assert.Equal(TextureWrap.Repeat, material.Wrap);

        TextureImage image = Assert.Single(set.Images);
        Assert.Equal("tex/a.dds", image.Name);
        Assert.Equal([0], set.MaterialOfPart);
    }

    [Fact]
    public void Two_parts_sharing_a_texture_share_one_image()
    {
        WriteTexture("tex/shared.dds");

        MaterialSet set = Assemble(
            parts: 2,
            new EditordataBuilder()
                .Section(MaterialSpec.Standard(name: "a", diffuse: "tex/shared.dds"))
                .Section(MaterialSpec.Standard(name: "b", diffuse: "tex/shared.dds")));

        Assert.Single(set.Images);
        Assert.Equal(2, set.Materials.Length);
        Assert.Equal(0, set.Materials[0].ImageIndex);
        Assert.Equal(0, set.Materials[1].ImageIndex);
    }

    [Fact]
    public void The_albedo_tint_and_colour_gain_combine_into_the_base_colour_factor()
    {
        // The zero-offset mapping: base colour factor is albedo tint times gain,
        // and the image is left untouched. slot_10 carries the tint, slot_30 the
        // gain, slot_20.w the constant alpha.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec
                {
                    Slot10 = (0.5f, 0.5f, 0.5f, 1f),
                    Slot20 = (0f, 0f, 0f, 0.25f),
                    Slot30 = (0.4f, 0.6f, 0.8f, 1f),
                }));

        ColorRgba factor = set.Materials[0].BaseColorFactor;
        Assert.Equal(0.5 * 0.4, factor.R, 6);
        Assert.Equal(0.5 * 0.6, factor.G, 6);
        Assert.Equal(0.5 * 0.8, factor.B, 6);
        Assert.Equal(0.25, factor.A, 6);
    }

    [Fact]
    public void The_uv_repeat_becomes_the_material_scale()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec { UvRepeat = (10f, 10f) }));

        TextureScale scale = set.Materials[0].Scale;
        Assert.Equal(10.0, scale.U, 6);
        Assert.Equal(10.0, scale.V, 6);
        Assert.False(scale.IsIdentity);
    }

    [Fact]
    public void An_absent_or_unit_repeat_is_the_identity_scale()
    {
        WriteTexture("tex/a.dds");

        MaterialSet noCustom = Assemble(
            parts: 1,
            new EditordataBuilder().Section(MaterialSpec.Standard(diffuse: "tex/a.dds")));
        Assert.True(noCustom.Materials[0].Scale.IsIdentity);

        MaterialSet unit = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec { UvRepeat = (1f, 1f) }));
        Assert.True(unit.Materials[0].Scale.IsIdentity);
    }

    [Fact]
    public void A_negative_repeat_is_carried_verbatim_rather_than_refused()
    {
        // For an opaque surface the repeat is reproduced as-is; only varying
        // alpha with a non-positive repeat is a problem, and that is a later
        // slice.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec { UvRepeat = (-2f, 3f) }));

        Assert.Equal(-2.0, set.Materials[0].Scale.U, 6);
        Assert.False(set.Materials[0].Scale.IsIdentity);
    }

    [Fact]
    public void A_zero_offset_folds_the_gain_into_the_factor_and_bakes_nothing()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec { Slot30 = (2f, 2f, 2f, 1f), Slot40 = (0f, 0f, 0f, 0f) }));

        // baseColorFactor.rgb = tint * gain; nothing baked.
        Assert.Equal(2.0, set.Materials[0].BaseColorFactor.R, 6);
        Assert.Empty(set.OffsetBakedParts);
    }

    [Fact]
    public void A_non_zero_offset_keeps_the_factor_at_the_tint_and_records_the_bake()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                new CustomSpec { Slot10 = (0.5f, 0.5f, 0.5f, 1f), Slot30 = (2f, 2f, 2f, 1f), Slot40 = (0.1f, 0f, 0f, 0f) }));

        // The gain does not fold in: the factor is the tint alone, gain and
        // offset went into the image instead.
        Assert.Equal(0.5, set.Materials[0].BaseColorFactor.R, 6);
        Assert.Equal([0], set.OffsetBakedParts);
    }

    [Fact]
    public void Two_parts_with_the_same_texture_but_different_offsets_do_not_share_an_image()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 2,
            new EditordataBuilder()
                .SectionWithCustom(
                    [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                    new CustomSpec { Slot40 = (0.1f, 0f, 0f, 0f) })
                .SectionWithCustom(
                    [MaterialSpec.Standard(diffuse: "tex/a.dds")],
                    new CustomSpec { Slot40 = (0.2f, 0f, 0f, 0f) }));

        Assert.Equal(2, set.Images.Length);
        Assert.Equal([0, 1], set.OffsetBakedParts);
    }

    [Fact]
    public void A_section_with_no_material_leaves_its_part_untextured()
    {
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 2,
            new EditordataBuilder()
                .Section()
                .Section(MaterialSpec.Standard(diffuse: "tex/a.dds")));

        Assert.Equal([-1, 0], set.MaterialOfPart);
        Assert.Single(set.Materials);
    }

    [Fact]
    public void A_transparent_material_composes_and_is_marked_blend()
    {
        // A DXT1 texture from the builder decodes to a constant alpha, which
        // composes on any size. The material is transparent and its image name
        // records both channels.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().Section(Transparent("t", "tex/a.dds", "tex/a.dds")));

        SurfaceMaterial material = Assert.Single(set.Materials);
        Assert.True(material.IsTransparent);
        Assert.Equal("tex/a.dds + tex/a.dds.a", Assert.Single(set.Images).Name);
    }

    [Fact]
    public void A_transparent_alpha_factor_comes_from_the_custom_record()
    {
        // baseColorFactor.a reproduces slot_20.w.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().SectionWithCustom(
                [Transparent("t", "tex/a.dds", "tex/a.dds")],
                new CustomSpec { Slot20 = (0f, 0f, 0f, 0.5f) }));

        Assert.Equal(0.5, set.Materials[0].BaseColorFactor.A, 6);
    }

    [Fact]
    public void An_ordinary_and_a_composed_use_of_one_path_are_distinct_images()
    {
        // The composition identity keeps them apart even though the diffuse path
        // is the same, so a diffuse-only image is never reused for a composite.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 2,
            new EditordataBuilder()
                .Section(MaterialSpec.Standard(name: "opaque", diffuse: "tex/a.dds"))
                .Section(Transparent("blend", "tex/a.dds", "tex/a.dds")));

        Assert.Equal(2, set.Images.Length);
        Assert.False(set.Materials[0].IsTransparent);
        Assert.True(set.Materials[1].IsTransparent);
    }

    [Fact]
    public void A_transparent_material_missing_its_transparent_texture_refuses()
    {
        WriteTexture("tex/a.dds");

        Refusal refusal = AssembleRefused(
            parts: 1,
            new EditordataBuilder().Section(new MaterialSpec("t", "CamelDefaultShader_Trans",
            [
                ("DiffuseColor", "tex/a.dds"),
                ("TransparentColor", ""),
            ])));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("exactly one TransparentColor", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_emissive_companion_with_no_base_is_unpaired_and_dropped()
    {
        // A lone emissive part matches no base, so it is omitted rather than
        // drawn as an occluding surface, and reported as unpaired.
        WriteTexture("tex/a.dds");

        MaterialSet set = Assemble(
            parts: 1,
            new EditordataBuilder().Section(new MaterialSpec("e", "CamelDefaultShader_Emissive",
            [
                ("EmissiveColor", "tex/a.dds"),
            ])));

        Assert.Empty(set.Materials);
        Assert.Empty(set.SurvivingParts);
        Assert.Equal([0], set.UnpairedCompanions);
        Assert.Empty(set.MergedCompanions);
    }

    [Fact]
    public void An_emissive_companion_merges_onto_a_geometry_matched_base()
    {
        // The base 'lit' and companion 'lit__E' share geometry, so the companion
        // merges: it is dropped, the base survives with an emissive texture and
        // its slot_60 factor, and the merge is reported.
        WriteTexture("tex/base.dds");
        WriteTexture("tex/glow.dds");

        MaterialSet set = Assemble(
            parts: 2,
            new EditordataBuilder()
                .Section(MaterialSpec.Standard(name: "lit", diffuse: "tex/base.dds"))
                .SectionWithCustom(
                    [new MaterialSpec("lit__E", "CamelDefaultShader_Emissive", [("EmissiveColor", "tex/glow.dds")])],
                    new CustomSpec { Slot60 = (0.5f, 0.25f, 0f, 0f) }));

        Assert.Equal([0], set.SurvivingParts);
        Assert.Equal([1], set.MergedCompanions);
        Assert.Empty(set.UnpairedCompanions);

        SurfaceMaterial lit = Assert.Single(set.Materials);
        Assert.NotNull(lit.EmissiveImageIndex);
        Assert.NotNull(lit.EmissiveFactor);
        Assert.Equal(0.5, lit.EmissiveFactor!.Value.R, 6);
        Assert.Equal("tex/glow.dds (EmissiveColor)", set.Images[lit.EmissiveImageIndex!.Value].Name);
    }

    [Fact]
    public void A_companion_whose_geometry_differs_from_its_base_is_unpaired()
    {
        // Names pair, but the geometry does not match, so the merge is refused
        // and the companion omitted rather than lighting geometry it misses.
        WriteTexture("tex/base.dds");
        WriteTexture("tex/glow.dds");

        MaterialSet set = Assemble(
            parts: 2,
            differentGeometry: true,
            new EditordataBuilder()
                .Section(MaterialSpec.Standard(name: "lit", diffuse: "tex/base.dds"))
                .Section(new MaterialSpec("lit__E", "CamelDefaultShader_Emissive", [("EmissiveColor", "tex/glow.dds")])));

        Assert.Equal([0], set.SurvivingParts);
        Assert.Equal([1], set.UnpairedCompanions);
        Assert.Empty(set.MergedCompanions);
        Assert.Null(set.Materials[0].EmissiveImageIndex);
    }

    [Fact]
    public void An_empty_shader_family_refuses()
    {
        WriteTexture("tex/a.dds");

        Refusal refusal = AssembleRefused(
            parts: 1,
            new EditordataBuilder().Section(new MaterialSpec("m", "", [("DiffuseColor", "tex/a.dds")])));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("<empty>", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_texture_no_source_holds_is_a_resource_refusal()
    {
        // Every source has been asked, so this is missing bytes, not the
        // absence the precedence rule tolerates.
        Refusal refusal = AssembleRefused(
            parts: 1,
            new EditordataBuilder().Section(MaterialSpec.Standard(diffuse: "tex/absent.dds")));

        Assert.Equal(RefusalKind.Resource, refusal.Kind);
        Assert.Equal(DiagnosticIds.ResourceMissing, refusal.DiagnosticId);
    }

    [Fact]
    public void A_section_count_that_does_not_equal_the_part_count_refuses()
    {
        // The ordinal is the association; a mismatch means no section names a
        // part, which is malformed rather than something to guess around.
        WriteTexture("tex/a.dds");

        Refusal refusal = AssembleRefused(
            parts: 3,
            new EditordataBuilder().Section(MaterialSpec.Standard(diffuse: "tex/a.dds")));

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void A_material_without_exactly_one_diffuse_texture_refuses()
    {
        Refusal refusal = AssembleRefused(
            parts: 1,
            new EditordataBuilder().Section(new MaterialSpec("m", "CamelDefaultShader",
            [
                ("DiffuseColor", "tex/a.dds"),
                ("DiffuseColor", "tex/b.dds"),
            ])));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("exactly one DiffuseColor", refusal.Message, StringComparison.Ordinal);
    }

    private static MaterialSpec Transparent(string name, string diffuse, string transparent) =>
        new(name, "CamelDefaultShader_Trans",
        [
            ("DiffuseColor", diffuse),
            ("NormalMap", ""),
            ("SpecularColor", ""),
            ("TransparentColor", transparent),
            ("EmissiveColor", ""),
        ]);

    private void WriteTexture(string normalizedPath)
    {
        // A minimal 4x4 DXT1 texture; its pixels do not matter to assembly, only
        // that it decodes.
        string full = _loose.FullName;
        foreach (string component in normalizedPath.Split('/'))
        {
            full = Path.Combine(full, component);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new DdsFileBuilder { Width = 4, Height = 4 }.Build());
    }

    private MaterialSet Assemble(int parts, EditordataBuilder editordata) =>
        Assemble(parts, differentGeometry: false, editordata);

    private MaterialSet Assemble(int parts, bool differentGeometry, EditordataBuilder editordata)
    {
        Result<MaterialSet> result = Run(parts, differentGeometry, editordata);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal AssembleRefused(int parts, EditordataBuilder editordata)
    {
        Result<MaterialSet> result = Run(parts, differentGeometry: false, editordata);
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }

    private Result<MaterialSet> Run(int parts, bool differentGeometry, EditordataBuilder editordata)
    {
        GeometryModel model = Geometry(parts, differentGeometry);
        EditordataFile file = ParseEditordata(editordata);
        using ContentSources content = new(_loose.FullName, null);
        return MaterialAssembler.Assemble(model, file, content);
    }

    private EditordataFile ParseEditordata(EditordataBuilder builder)
    {
        string path = Path.Combine(_directory.FullName, $"m{Guid.NewGuid():N}.editordata");
        File.WriteAllBytes(path, builder.Build());
        Result<SourceFile> source = SourceFileReader.Read(path);
        Assert.True(source.IsSuccess);
        Result<EditordataFile> file = EditordataReader.Read(source.Value);
        Assert.True(file.IsSuccess, file.IsRefused ? file.Refusal.Message : "no outcome");
        return file.Value;
    }

    private static GeometryModel Geometry(int parts, bool differentGeometry = false)
    {
        // Every part shares one triangle, so a base and companion match by
        // default; differentGeometry shifts the last part so a merge is refused.
        ImmutableArray<GeometryPart> built =
        [
            .. Enumerable.Range(0, parts).Select(i =>
            {
                double shift = differentGeometry && i == parts - 1 ? 5.0 : 0.0;
                return new GeometryPart(
                    i,
                    $"mode3-record-{i}",
                    "label",
                    "label",
                    [new Vector3D(shift, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
                    [0, 1, 2],
                    [new Vector2D(0, 0), new Vector2D(1, 0), new Vector2D(0, 1)],
                    [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);
            }),
        ];

        return new GeometryModel(3, built, false);
    }
}
