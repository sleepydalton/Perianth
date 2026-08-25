using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Core.Imaging;
using Perianth.Core.Materials;
using Perianth.Formats.Diagnostics;
using Perianth.Gltf;
using Xunit;

namespace Perianth.Tests.Gltf;

public sealed class GlbWriterMaterialTests
{
    [Fact]
    public void A_textured_part_references_its_material_image_texture_and_sampler()
    {
        JsonElement gltf = Json(Write(OneMaterial(TextureScale.Identity)));

        Assert.Equal(0, gltf.GetProperty("meshes")[0].GetProperty("primitives")[0]
            .GetProperty("material").GetInt32());

        JsonElement material = gltf.GetProperty("materials")[0];
        Assert.Equal("surface", material.GetProperty("name").GetString());
        Assert.Equal("OPAQUE", material.GetProperty("alphaMode").GetString());

        JsonElement pbr = material.GetProperty("pbrMetallicRoughness");
        Assert.Equal(0, pbr.GetProperty("baseColorTexture").GetProperty("index").GetInt32());
        Assert.Equal(0, pbr.GetProperty("metallicFactor").GetInt32());
        Assert.Equal(1, pbr.GetProperty("roughnessFactor").GetInt32());

        Assert.Equal(0, gltf.GetProperty("textures")[0].GetProperty("source").GetInt32());
        Assert.Equal("image/png", gltf.GetProperty("images")[0].GetProperty("mimeType").GetString());
    }

    [Fact]
    public void The_sampler_is_explicit_repeat_linear_state()
    {
        JsonElement sampler = Json(Write(OneMaterial(TextureScale.Identity))).GetProperty("samplers")[0];

        Assert.Equal(10497, sampler.GetProperty("wrapS").GetInt32());
        Assert.Equal(10497, sampler.GetProperty("wrapT").GetInt32());
        Assert.Equal(9729, sampler.GetProperty("magFilter").GetInt32());
        Assert.Equal(9987, sampler.GetProperty("minFilter").GetInt32());
    }

    [Fact]
    public void An_identity_scale_writes_no_texture_transform()
    {
        JsonElement gltf = Json(Write(OneMaterial(TextureScale.Identity)));

        JsonElement texture = gltf.GetProperty("materials")[0]
            .GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture");

        Assert.False(texture.TryGetProperty("extensions", out _));

        // The extension list is not empty, though: every material is unlit, so
        // KHR_materials_unlit is always declared and the texture transform is
        // the only conditional entry.
        Assert.Equal(["KHR_materials_unlit"], gltf.GetProperty("extensionsUsed")
            .EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public void A_non_identity_scale_becomes_a_texture_transform_and_declares_the_extension()
    {
        JsonElement gltf = Json(Write(OneMaterial(new TextureScale(10, 4))));

        JsonElement transform = gltf.GetProperty("materials")[0]
            .GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorTexture")
            .GetProperty("extensions")
            .GetProperty("KHR_texture_transform");

        Assert.Equal([10, 4], transform.GetProperty("scale").EnumerateArray()
            .Select(v => v.GetInt32()).ToArray());

        // The extension is declared exactly once, at the top level, beside the
        // unlit declaration every material carries.
        Assert.Equal(["KHR_texture_transform", "KHR_materials_unlit"], gltf.GetProperty("extensionsUsed")
            .EnumerateArray().Select(v => v.GetString() ?? string.Empty).ToArray());

        // No offset accompanies the scale: the V orientation is the engine's.
        Assert.False(transform.TryGetProperty("offset", out _));
    }

    [Fact]
    public void A_negative_scale_is_written_verbatim()
    {
        JsonElement transform = Json(Write(OneMaterial(new TextureScale(-2, 3))))
            .GetProperty("materials")[0]
            .GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorTexture")
            .GetProperty("extensions")
            .GetProperty("KHR_texture_transform");

        Assert.Equal(-2, transform.GetProperty("scale")[0].GetInt32());
    }

    [Fact]
    public void A_merged_emissive_material_carries_a_texture_and_factor_between_pbr_and_alpha()
    {
        MaterialSet set = new(
            [
                new TextureImage("tex/a.dds", [.. new byte[] { 1, 2, 3, 4 }]),
                new TextureImage("tex/e.dds (EmissiveColor)", [.. new byte[] { 5, 6, 7, 8 }]),
            ],
            [
                new SurfaceMaterial(
                    "lit", 0, ColorRgba.White, IsTransparent: false, TextureWrap.Repeat, TextureScale.Identity,
                    EmissiveImageIndex: 1, EmissiveFactor: new Rgb(0.9, 0.4, 0)),
            ],
            [0], [0], [], [], [], [], [], [], [], []);

        JsonElement material = Json(Write(set)).GetProperty("materials")[0];
        string[] keys = material.EnumerateObject().Select(p => p.Name).ToArray();

        // emissiveTexture and emissiveFactor sit between the pbr block and the
        // alpha mode, matching the reference document order. doubleSided closes
        // it: every assembled Camel plane carries it now, posed or not, because
        // the presentation basis mirrors X and a plane facing away is culled —
        // 17 of 25 on a prop that has no setup ANIM to be posed by. That is a
        // deliberate deviation from the frozen reference; see the baseline note
        // and Roadmap §6.13.
        // "extensions" closes it, carrying KHR_materials_unlit: the camel shader
        // is forward and writes its colour straight out with no normal anywhere
        // in the path, so declaring metallic-roughness alone says the opposite
        // of what the source is and a viewer with no lamp draws it black. The
        // fourth deliberate deviation from the frozen reference; see the
        // baseline note and Roadmap §10.125.
        Assert.Equal(
            ["name", "pbrMetallicRoughness", "emissiveTexture", "emissiveFactor", "alphaMode",
             "doubleSided", "extensions"],
            keys);

        Assert.True(material.GetProperty("extensions")
            .TryGetProperty("KHR_materials_unlit", out _));

        Assert.Equal(1, material.GetProperty("emissiveTexture").GetProperty("index").GetInt32());
        double[] factor = material.GetProperty("emissiveFactor").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Assert.Equal(0.9, factor[0], 6);
        Assert.Equal(0.4, factor[1], 6);
        Assert.Equal(0.0, factor[2], 6);
    }

    [Fact]
    public void A_material_without_a_companion_writes_no_emissive_fields()
    {
        JsonElement material = Json(Write(OneMaterial(TextureScale.Identity))).GetProperty("materials")[0];
        Assert.False(material.TryGetProperty("emissiveTexture", out _));
        Assert.False(material.TryGetProperty("emissiveFactor", out _));
    }

    private static MaterialSet OneMaterial(TextureScale scale) => new(
        [new TextureImage("tex/a.dds", [.. new byte[] { 1, 2, 3, 4 }])],
        [new SurfaceMaterial("surface", 0, ColorRgba.White, IsTransparent: false, TextureWrap.Repeat, scale)],
        [0],
        [0],
        [],
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    private static byte[] Write(MaterialSet materials)
    {
        GeometryModel model = new(3, [Part(0)], false);
        Result<byte[]> result = GlbWriter.Write(model, materials, new GlbWriteOptions());
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static GeometryPart Part(int ordinal) => new(
        ordinal,
        string.Create(CultureInfo.InvariantCulture, $"mode3-record-{ordinal}"),
        "label",
        "label",
        [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
        [0, 1, 2],
        [new Vector2D(0, 0), new Vector2D(1, 0), new Vector2D(0, 1)],
        [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);

    private static JsonElement Json(byte[] glb)
    {
        // The JSON chunk begins after the 12-byte header and the 8-byte chunk
        // header, and runs for the length the chunk declares.
        int jsonLength = BitConverter.ToInt32(glb, 12);
        return JsonDocument.Parse(glb.AsMemory(20, jsonLength)).RootElement;
    }
}
