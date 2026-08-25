using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using Perianth.Core.Geometry;
using Perianth.Formats.Diagnostics;
using Perianth.Gltf;
using Xunit;

namespace Perianth.Tests.Gltf;

/// <summary>
/// The reader import needs, checked against the writer it mirrors.
/// </summary>
/// <remarks>
/// Reading back what <see cref="GlbWriter"/> produced is the strongest available
/// oracle without an asset: the two are written independently, so agreement is
/// evidence rather than a tautology, and the writer's output is the file format
/// an author will actually edit.
/// </remarks>
public sealed class GlbReaderTests
{
    [Fact]
    public void What_the_writer_emitted_is_what_the_reader_gets_back()
    {
        GeometryModel model = Model(Part(0), Part(7), Part(41));
        byte[] glb = GlbWriter.Write(model, new GlbWriteOptions()).Value;

        ImmutableArray<GlbMesh> meshes = GlbReader.Read(glb).Value;

        Assert.Equal(3, meshes.Length);
        Assert.Equal(["mode3-record-0", "mode3-record-7", "mode3-record-41"], meshes.Select(m => m.Name));

        // Bit equality, not proximity: the whole reshape stage rests on a
        // position surviving the trip out and back unchanged, so a tolerance
        // here would hide the one thing worth checking.
        for (int i = 0; i < meshes.Length; i++)
        {
            Assert.Equal(model.Parts[i].Positions, meshes[i].Positions);
        }
    }

    [Fact]
    public void A_mesh_keeps_its_vertex_order_including_repeated_positions()
    {
        // Every record in the models measured is direct, so the same position is
        // written out two to twelve times and the order is what identifies which
        // pool slot each vertex came from. A reader that deduplicated would be
        // unusable for import and would look correct in every other respect.
        ImmutableArray<Vector3D> repeated =
        [
            new(1, 2, 3), new(4, 5, 6), new(1, 2, 3),
            new(4, 5, 6), new(1, 2, 3), new(7, 8, 9),
        ];

        GeometryModel model = Model(WithPositions(0, repeated));
        byte[] glb = GlbWriter.Write(model, new GlbWriteOptions()).Value;

        GlbMesh mesh = Assert.Single(GlbReader.Read(glb).Value);
        Assert.Equal(repeated, mesh.Positions);
    }

    [Fact]
    public void The_corner_order_follows_the_indices_rather_than_the_vertex_order()
    {
        // What the two views are for. A mesh whose indices do not run 0..n-1 --
        // which is any mesh something has re-indexed -- draws its triangles in
        // the order the indices give, and reading the vertices in file order
        // would silently draw different triangles.
        GeometryModel model = Model(WithIndices(
            0,
            [new Vector3D(1, 0, 0), new Vector3D(2, 0, 0), new Vector3D(3, 0, 0)],
            [2, 0, 1]));

        GlbMesh mesh = Assert.Single(GlbReader.Read(GlbWriter.Write(model, new GlbWriteOptions()).Value).Value);

        Assert.Equal(model.Parts[0].Positions, mesh.Positions);
        Assert.Equal(
            [new Vector3D(3, 0, 0), new Vector3D(1, 0, 0), new Vector3D(2, 0, 0)],
            mesh.Corners());
    }

    [Fact]
    public void A_mesh_indexed_in_order_has_the_same_corners_as_vertices()
    {
        // The ordinary case, and what makes reading the indices safe to add: a
        // direct record exports as 0..n-1, so the two views agree and nothing
        // that relied on vertex order changed.
        GeometryModel model = Model(Part(0));

        GlbMesh mesh = Assert.Single(GlbReader.Read(GlbWriter.Write(model, new GlbWriteOptions()).Value).Value);

        Assert.Equal(mesh.Positions, mesh.Corners());
    }

    [Fact]
    public void An_index_naming_a_vertex_the_mesh_does_not_have_refuses()
    {
        byte[] glb = Rewritten(json => json.Replace("\"count\":3,\"type\":\"SCALAR\"", "\"count\":9,\"type\":\"SCALAR\"", StringComparison.Ordinal));

        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(glb);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public void A_file_that_is_not_a_GLB_refuses()
    {
        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(Encoding.ASCII.GetBytes("not a glb at all"));

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_chunk_reaching_past_the_end_refuses_rather_than_reading_what_is_there()
    {
        byte[] glb = GlbWriter.Write(Model(Part(0)), new GlbWriteOptions()).Value;
        byte[] truncated = glb[..(glb.Length - 32)];

        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(truncated);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void Positions_backed_by_no_bufferView_refuse_rather_than_importing_as_zeroes()
    {
        // glTF defines such an accessor as all zeroes. That is a legal file and a
        // meaningless import: accepting it would move every vertex of the part to
        // the origin and report success.
        byte[] glb = Rewritten(json =>
            json.Replace("\"bufferView\":0,", string.Empty, StringComparison.Ordinal));

        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(glb);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("import as zeroes", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sparse_accessor_refuses_rather_than_reading_only_its_dense_half()
    {
        byte[] glb = Rewritten(json =>
            json.Replace("\"type\":\"VEC3\"", "\"type\":\"VEC3\",\"sparse\":{}", StringComparison.Ordinal));

        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(glb);

        Assert.True(result.IsRefused);
        Assert.Contains("sparse", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refusal_names_the_mesh_so_the_author_can_find_it()
    {
        byte[] glb = Rewritten(json =>
            json.Replace("\"bufferView\":0,", string.Empty, StringComparison.Ordinal));

        Assert.Contains("mode3-record-0", GlbReader.Read(glb).Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_GLB_carrying_no_meshes_refuses_as_unsupported_rather_than_malformed()
    {
        // Nothing is wrong with the file; it just cannot answer the question.
        byte[] glb = Rewritten(json => json.Replace("\"meshes\"", "\"notmeshes\"", StringComparison.Ordinal));

        Result<ImmutableArray<GlbMesh>> result = GlbReader.Read(glb);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    /// <summary>Rewrites the JSON chunk of a one-part GLB and rebuilds the container.</summary>
    private static byte[] Rewritten(Func<string, string> edit)
    {
        byte[] glb = GlbWriter.Write(Model(Part(0)), new GlbWriteOptions()).Value;

        int jsonLength = BitConverter.ToInt32(glb, 12);
        string json = Encoding.UTF8.GetString(glb, 20, jsonLength);
        byte[] replaced = Encoding.UTF8.GetBytes(edit(json));

        // Padded to four with spaces, as the JSON chunk must be.
        int padded = (replaced.Length + 3) / 4 * 4;
        byte[] chunk = new byte[padded];
        replaced.CopyTo(chunk, 0);
        for (int i = replaced.Length; i < padded; i++)
        {
            chunk[i] = (byte)' ';
        }

        byte[] rest = glb[(20 + jsonLength)..];
        byte[] rebuilt = new byte[20 + chunk.Length + rest.Length];
        glb.AsSpan(0, 12).CopyTo(rebuilt);
        BitConverter.GetBytes(chunk.Length).CopyTo(rebuilt, 12);
        BitConverter.GetBytes(0x4E4F_534A).CopyTo(rebuilt, 16);
        chunk.CopyTo(rebuilt, 20);
        rest.CopyTo(rebuilt, 20 + chunk.Length);
        BitConverter.GetBytes(rebuilt.Length).CopyTo(rebuilt, 8);
        return rebuilt;
    }

    private static GeometryModel Model(params GeometryPart[] parts) => new(3, [.. parts], false);

    private static GeometryPart WithIndices(
        int ordinal, ImmutableArray<Vector3D> positions, ImmutableArray<int> indices) => new(
        ordinal,
        string.Create(CultureInfo.InvariantCulture, $"mode3-record-{ordinal}"),
        "label",
        "label",
        positions,
        indices,
        [.. positions.Select(_ => new Vector2D(0, 0))],
        [.. positions.Select(_ => new Vector3D(0, 0, 1))]);

    private static GeometryPart Part(int ordinal) => WithPositions(
        ordinal, [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)]);

    private static GeometryPart WithPositions(int ordinal, ImmutableArray<Vector3D> positions) => new(
        ordinal,
        string.Create(CultureInfo.InvariantCulture, $"mode3-record-{ordinal}"),
        "label",
        "label",
        positions,
        [.. Enumerable.Range(0, positions.Length)],
        [.. positions.Select(_ => new Vector2D(0, 0))],
        [.. positions.Select(_ => new Vector3D(0, 0, 1))]);
}
