using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;
using Perianth.Gltf;
using Xunit;

namespace Perianth.Tests.Gltf;

public sealed class GlbWriterTests
{
    [Fact]
    public void The_container_is_a_well_formed_GLB_2()
    {
        byte[] glb = Write(Model(Part(0)));

        Assert.Equal("glTF", Encoding.ASCII.GetString(glb.AsSpan(0, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));

        Chunks chunks = Split(glb);
        Assert.Equal("JSON", chunks.JsonType);
        Assert.Equal("BIN\0", chunks.BinType);
        Assert.Equal(0, chunks.JsonLength % 4);
        Assert.Equal(0, chunks.BinLength % 4);
    }

    [Fact]
    public void The_JSON_chunk_pads_with_spaces_and_the_binary_chunk_with_zeroes()
    {
        // A part with a vertex count that leaves the JSON at an awkward length.
        byte[] glb = Write(Model(Part(0), Part(1), Part(2)));
        Chunks chunks = Split(glb);

        for (int i = chunks.JsonBytes.Length; i < chunks.JsonLength; i++)
        {
            Assert.Equal((byte)' ', glb[chunks.JsonStart + i]);
        }

        for (int i = chunks.BinBytes.Length; i < chunks.BinLength; i++)
        {
            Assert.Equal(0, glb[chunks.BinStart + i]);
        }
    }

    [Fact]
    public void The_asset_block_names_this_tool_rather_than_its_predecessor()
    {
        JsonElement gltf = Json(Write(Model(Part(0))));

        Assert.Equal("Perianth 0.1", gltf.GetProperty("asset").GetProperty("generator").GetString());
        Assert.Equal("2.0", gltf.GetProperty("asset").GetProperty("version").GetString());
        Assert.Equal(0, gltf.GetProperty("scene").GetInt32());
    }

    [Fact]
    public void The_presentation_root_is_the_scenes_only_root_and_reflects_X()
    {
        JsonElement gltf = Json(Write(Model(Part(0), Part(1))));

        JsonElement scene = gltf.GetProperty("scenes")[0];
        Assert.Equal("unposed-all-parts", scene.GetProperty("name").GetString());
        Assert.Equal([2], Ints(scene.GetProperty("nodes")));

        JsonElement nodes = gltf.GetProperty("nodes");
        Assert.Equal(3, nodes.GetArrayLength());

        JsonElement root = nodes[2];

        // Renamed from MMBTool 2026-08-03: the string is written into every file
        // and read by whoever opens one, and it named a program that no longer
        // exists. The C# harness declares that divergence, so the baseline still
        // holds what the reference wrote and any other name change still fails.
        Assert.Equal("Perianth source-to-glTF presentation basis", root.GetProperty("name").GetString());
        Assert.Equal([0, 1], Ints(root.GetProperty("children")));
        Assert.Equal([-1, 1, 1], Doubles(root.GetProperty("scale")));
        Assert.False(root.TryGetProperty("mesh", out _));
    }

    [Fact]
    public void Source_space_omits_the_presentation_root_and_leaves_the_meshes_as_roots()
    {
        JsonElement gltf = Json(Write(Model(Part(0), Part(1)), new GlbWriteOptions { IncludePresentationBasis = false }));

        Assert.Equal(2, gltf.GetProperty("nodes").GetArrayLength());
        Assert.Equal([0, 1], Ints(gltf.GetProperty("scenes")[0].GetProperty("nodes")));
    }

    [Fact]
    public void A_posed_scene_carries_the_other_name()
    {
        JsonElement gltf = Json(Write(Model(Part(0)), new GlbWriteOptions { SceneName = GlbNames.PosedScene }));

        Assert.Equal("posed", gltf.GetProperty("scenes")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Each_mesh_gets_one_node_named_after_it_with_no_identity_transform()
    {
        JsonElement gltf = Json(Write(Model(Part(0), Part(1))));
        JsonElement node = gltf.GetProperty("nodes")[1];

        Assert.Equal("mode3-record-1-node", node.GetProperty("name").GetString());
        Assert.Equal(1, node.GetProperty("mesh").GetInt32());

        // An identity local transform is omitted rather than written out.
        Assert.False(node.TryGetProperty("translation", out _));
        Assert.False(node.TryGetProperty("rotation", out _));
        Assert.False(node.TryGetProperty("scale", out _));
    }

    [Fact]
    public void Each_mesh_is_one_triangle_list_primitive()
    {
        JsonElement mesh = Json(Write(Model(Part(0)))).GetProperty("meshes")[0];

        Assert.Equal("mode3-record-0", mesh.GetProperty("name").GetString());
        JsonElement primitive = Assert.Single(mesh.GetProperty("primitives").EnumerateArray());
        Assert.Equal(4, primitive.GetProperty("mode").GetInt32());
    }

    [Fact]
    public void A_part_with_UV0_gets_four_accessors_and_one_without_gets_three()
    {
        Assert.Equal(4, Json(Write(Model(Part(0)))).GetProperty("accessors").GetArrayLength());
        Assert.Equal(3, Json(Write(Model(Part(0, uv0: false)))).GetProperty("accessors").GetArrayLength());

        JsonElement bare = Json(Write(Model(Part(0, uv0: false))));
        JsonElement attributes = bare.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
        Assert.False(attributes.TryGetProperty("TEXCOORD_0", out _));
    }

    [Fact]
    public void Indices_are_unsigned_int_scalars_and_attributes_are_floats()
    {
        JsonElement gltf = Json(Write(Model(Part(0))));
        JsonElement accessors = gltf.GetProperty("accessors");
        JsonElement attributes = gltf.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");

        JsonElement position = accessors[attributes.GetProperty("POSITION").GetInt32()];
        JsonElement normal = accessors[attributes.GetProperty("NORMAL").GetInt32()];
        JsonElement uv = accessors[attributes.GetProperty("TEXCOORD_0").GetInt32()];
        JsonElement indices = accessors[gltf.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32()];

        Assert.Equal(5126, position.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC3", position.GetProperty("type").GetString());
        Assert.Equal("VEC3", normal.GetProperty("type").GetString());
        Assert.Equal("VEC2", uv.GetProperty("type").GetString());

        // Always UInt32, even where the values would fit in sixteen bits.
        Assert.Equal(5125, indices.GetProperty("componentType").GetInt32());
        Assert.Equal("SCALAR", indices.GetProperty("type").GetString());
    }

    [Fact]
    public void Only_the_position_accessor_carries_bounds_and_they_bound_the_stored_values()
    {
        JsonElement gltf = Json(Write(Model(Part(0))));
        JsonElement accessors = gltf.GetProperty("accessors");

        JsonElement position = accessors[0];
        Assert.Equal([0, 0, 0], Doubles(position.GetProperty("min")));
        Assert.Equal([1, 1, 0], Doubles(position.GetProperty("max")));

        Assert.False(accessors[1].TryGetProperty("min", out _));
        Assert.False(accessors[3].TryGetProperty("min", out _));
    }

    [Fact]
    public void Bounds_keep_the_binary64_coordinate_rather_than_the_narrowed_one()
    {
        // Positions are packed as binary32, but the bounds are computed over
        // the binary64 values and written unnarrowed. 0.1 is the cheapest
        // witness: it survives as 0.1 here and would come back as
        // 0.10000000149011612 if the bounds were taken after the narrowing.
        GeometryPart part = new(
            0,
            "mode3-record-0",
            "label",
            "label",
            [new Vector3D(0.1, 0, 0), new Vector3D(1.1, 0, 0), new Vector3D(0.1, 1, 0)],
            [0, 1, 2],
            [new Vector2D(0, 0), new Vector2D(1, 0), new Vector2D(0, 1)],
            [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);

        JsonElement position = Json(Write(Model(part))).GetProperty("accessors")[0];
        Assert.Equal(0.1, Doubles(position.GetProperty("min"))[0]);
    }

    [Fact]
    public void Every_accessor_states_its_byte_offset_into_its_view()
    {
        // A view holds exactly one accessor, so the offset is always zero.
        // It is written rather than left to the glTF default because the
        // reference writes it, and a reader that diffs the two documents
        // should see no difference at all.
        foreach (JsonElement accessor in Json(Write(Model(Part(0), Part(1)))).GetProperty("accessors").EnumerateArray())
        {
            Assert.Equal(0, accessor.GetProperty("byteOffset").GetInt32());
        }
    }

    [Fact]
    public void Buffer_views_abut_exactly_and_sum_to_the_declared_buffer()
    {
        JsonElement gltf = Json(Write(Model(Part(0), Part(1))));
        JsonElement views = gltf.GetProperty("bufferViews");

        int expected = 0;
        foreach (JsonElement view in views.EnumerateArray())
        {
            Assert.Equal(0, view.GetProperty("buffer").GetInt32());
            Assert.Equal(expected, view.GetProperty("byteOffset").GetInt32());

            // Every element is four bytes wide, so a view never needs padding.
            Assert.Equal(0, view.GetProperty("byteOffset").GetInt32() % 4);
            expected += view.GetProperty("byteLength").GetInt32();
        }

        Assert.Equal(expected, gltf.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32());
    }

    [Fact]
    public void Buffer_views_target_the_array_or_element_array_binding()
    {
        JsonElement views = Json(Write(Model(Part(0)))).GetProperty("bufferViews");

        Assert.Equal(34962, views[0].GetProperty("target").GetInt32());
        Assert.Equal(34962, views[1].GetProperty("target").GetInt32());
        Assert.Equal(34962, views[2].GetProperty("target").GetInt32());
        Assert.Equal(34963, views[3].GetProperty("target").GetInt32());
    }

    [Fact]
    public void The_binary_chunk_is_exactly_the_size_the_attribute_widths_imply()
    {
        // The same arithmetic the real baseline records: three vertices and one
        // triangle per part, positions and normals at twelve bytes a vertex,
        // texture coordinates at eight, and indices at four.
        byte[] glb = Write(Model(Part(0), Part(1)));
        Chunks chunks = Split(glb);

        const int Vertices = 3;
        const int Triangles = 1;
        int perPart = (Vertices * 3 * 4) + (Vertices * 3 * 4) + (Vertices * 2 * 4) + (Triangles * 3 * 4);

        Assert.Equal(2 * perPart, chunks.BinBytes.Length);
    }

    [Fact]
    public void The_narrowing_to_binary32_rounds_to_nearest_with_ties_to_even()
    {
        // Exactly half way between 1.0f and the next float. Ties to even keeps
        // 1.0f; rounding away from zero would not.
        double tie = 1.0 + Math.Pow(2, -24);
        GeometryPart part = new(
            0, "mode3-record-0", "label", "label",
            [new Vector3D(tie, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
            [0, 1, 2],
            [],
            [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);

        Chunks chunks = Split(Write(Model(part)));

        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(chunks.BinBytes));
    }

    [Fact]
    public void The_same_model_writes_the_same_bytes()
    {
        // Determinism is the product, and a serializer's ordering is exactly the
        // thing that would break it silently.
        byte[] first = Write(Model(Part(0), Part(1)));
        byte[] second = Write(Model(Part(0), Part(1)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_value_that_is_not_finite_once_narrowed_refuses()
    {
        GeometryPart part = new(
            7, "mode3-record-7", "label", "label",
            [new Vector3D(double.MaxValue, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
            [0, 1, 2],
            [],
            [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);

        Result<byte[]> result = GlbWriter.Write(Model(part), new GlbWriteOptions());

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Contains("part 7", result.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("binary32", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_chunk_pads_with_its_own_filler_byte()
    {
        // Reached directly: no geometry can produce a binary chunk that needs
        // padding, because every element this writer emits is four bytes wide.
        byte[] glb = GlbWriter.Assemble(Encoding.ASCII.GetBytes("{}"), [1, 2, 3]).Value;

        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(4, jsonLength);
        Assert.Equal((byte)' ', glb[20 + 2]);
        Assert.Equal((byte)' ', glb[20 + 3]);

        int binHeader = 20 + jsonLength;
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binHeader)));
        Assert.Equal(0, glb[binHeader + 8 + 3]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    public void Padding_rounds_a_length_up_to_four(int length, int expected)
    {
        Assert.Equal(expected, GlbWriter.Padding(length));
    }

    [Fact]
    public void Writing_a_null_model_is_a_fault()
    {
        Assert.Throws<ArgumentNullException>(() => GlbWriter.Write(null!, new GlbWriteOptions()));
    }

    private static byte[] Write(GeometryModel model) => Write(model, new GlbWriteOptions());

    private static byte[] Write(GeometryModel model, GlbWriteOptions options)
    {
        Result<byte[]> result = GlbWriter.Write(model, options);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static GeometryModel Model(params GeometryPart[] parts) => new(3, [.. parts], false);

    private static GeometryPart Part(int ordinal, bool uv0 = true) => new(
        ordinal,
        string.Create(CultureInfo.InvariantCulture, $"mode3-record-{ordinal}"),
        "label",
        "label",
        [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
        [0, 1, 2],
        uv0 ? [new Vector2D(0, 0), new Vector2D(1, 0), new Vector2D(0, 1)] : [],
        [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)]);

    private static JsonElement Json(byte[] glb) =>
        JsonDocument.Parse(Split(glb).JsonBytes).RootElement;

    private static int[] Ints(JsonElement array)
    {
        int[] values = new int[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            values[i++] = value.GetInt32();
        }

        return values;
    }

    private static double[] Doubles(JsonElement array)
    {
        double[] values = new double[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            values[i++] = value.GetDouble();
        }

        return values;
    }

    private static Chunks Split(byte[] glb)
    {
        int jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        string jsonType = Encoding.ASCII.GetString(glb.AsSpan(16, 4));
        int jsonStart = 20;

        int binHeader = jsonStart + jsonLength;
        int binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binHeader));
        string binType = Encoding.ASCII.GetString(glb.AsSpan(binHeader + 4, 4));
        int binStart = binHeader + 8;

        // The declared chunk length includes the padding; the payload is what is
        // left once the trailing filler is removed.
        // The JSON payload can be recovered by trimming its space padding, but
        // the binary payload cannot: an index value of 2 ends in three zero
        // bytes that are data, not filler. Its true length is the buffer length
        // the JSON declares.
        byte[] json = glb.AsSpan(jsonStart, jsonLength).TrimEnd((byte)' ').ToArray();
        int declared = JsonDocument.Parse(json).RootElement
            .GetProperty("buffers")[0].GetProperty("byteLength").GetInt32();
        byte[] bin = glb.AsSpan(binStart, declared).ToArray();

        return new Chunks(jsonType, jsonLength, jsonStart, json, binType, binLength, binStart, bin);
    }

    private sealed record Chunks(
        string JsonType, int JsonLength, int JsonStart, byte[] JsonBytes,
        string BinType, int BinLength, int BinStart, byte[] BinBytes);
}
