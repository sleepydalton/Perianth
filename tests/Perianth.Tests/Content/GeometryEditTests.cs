using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Writing edited vertex positions back into a model's cameldata.
/// </summary>
public sealed class GeometryEditTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"geom-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Moving_a_vertex_writes_its_pool_slot_and_leaves_the_rest_alone()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        GeometryEditResult edited = Reshaped(model, cameldata,
            [new Vector3D(100, 200, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)]);

        Assert.Equal(1, edited.Parts);
        Assert.Equal(new Vector2(100, 200), edited.Cameldata.Xy[0]);
        Assert.Equal(new Vector2(3, 4), edited.Cameldata.Xy[1]);
        Assert.Equal(new Vector2(5, 6), edited.Cameldata.Xy[2]);

        // Everything a reshape must not touch. A base index off by one produces a
        // file that loads and draws another part's geometry.
        Assert.Equal(cameldata.Constants, edited.Cameldata.Constants);
        Assert.Equal(cameldata.PackedZ, edited.Cameldata.PackedZ);
        Assert.Equal(cameldata.Uv0, edited.Cameldata.Uv0);
    }

    [Fact]
    public void Moving_a_part_in_depth_writes_the_depth_its_vertices_share()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        GeometryEditResult edited = Reshaped(model, cameldata,
            [new Vector3D(1, 2, 99), new Vector3D(3, 4, 99), new Vector3D(5, 6, 99)]);

        Assert.Equal(99f, edited.Cameldata.Z[0]);
        Assert.Equal(1, edited.Depths);
    }

    [Fact]
    public void An_unchanged_reshape_reports_nothing_moved()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        GeometryEditResult edited = Reshaped(model, cameldata,
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)]);

        Assert.Equal(1, edited.Parts);
        Assert.Equal(0, edited.Slots);
        Assert.Equal(0, edited.Depths);
        Assert.Equal(cameldata.Xy, edited.Cameldata.Xy);
    }

    [Fact]
    public void Vertices_that_share_a_slot_move_together_without_complaint()
    {
        // The ordinary case, and the one that must not refuse: records are direct,
        // so the same slot is written out repeatedly and every copy names the same
        // new position.
        (MmbModel model, Mode3Cameldata cameldata) = Pair(entries: [0, 0, 1]);

        GeometryEditResult edited = Reshaped(model, cameldata,
            [new Vector3D(9, 9, 7), new Vector3D(9, 9, 7), new Vector3D(3, 4, 7)]);

        Assert.Equal(new Vector2(9, 9), edited.Cameldata.Xy[0]);
        Assert.Equal(1, edited.Slots);
    }

    [Fact]
    public void Tearing_two_vertices_that_share_a_slot_apart_refuses()
    {
        // One slot cannot hold two positions, and giving it new ones would need
        // slots the part does not have. Refusing says so; writing one of them
        // would move the other silently.
        (MmbModel model, Mode3Cameldata cameldata) = Pair(entries: [0, 0, 1]);

        Refusal refusal = ReshapeRefused(model, cameldata,
            [new Vector3D(9, 9, 7), new Vector3D(-9, -9, 7), new Vector3D(3, 4, 7)]);

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("move together", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Giving_vertices_on_one_plane_different_depths_refuses()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Refusal refusal = ReshapeRefused(model, cameldata,
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 50), new Vector3D(5, 6, 7)]);

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("depth", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stated_pool_slot_is_believed_over_the_vertex_order()
    {
        // The whole point of stating it. Here the vertices arrive in the reverse
        // of the order the model has them, as a tool that re-welded the mesh
        // might leave them; taking position in the list would write all three
        // positions into the wrong entries and say nothing.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Result<GeometryEditResult> result = GeometryEdit.Reshape(model, cameldata,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(5, 6, 7), new Vector3D(3, 4, 7), new Vector3D(100, 200, 7)],
                [2, 1, 0]),
        ]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        Assert.Equal(new Vector2(100, 200), result.Value.Cameldata.Xy[0]);
        Assert.Equal(new Vector2(3, 4), result.Value.Cameldata.Xy[1]);
        Assert.Equal(new Vector2(5, 6), result.Value.Cameldata.Xy[2]);
        Assert.Equal(1, result.Value.Slots);
    }

    [Fact]
    public void Without_a_stated_slot_the_vertex_order_is_still_used()
    {
        // A mesh modelled from nothing carries no mapping, and there is nothing
        // to map back to: the fallback is what every reshape did before the
        // export began stating it, and it must keep working.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        GeometryEditResult edited = Reshaped(model, cameldata,
            [new Vector3D(100, 200, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)]);

        Assert.Equal(new Vector2(100, 200), edited.Cameldata.Xy[0]);
    }

    [Fact]
    public void A_different_vertex_count_refuses_and_says_what_to_check()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Refusal refusal = ReshapeRefused(model, cameldata,
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 7)]);

        Assert.Contains("2 vertices", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("merge vertices by distance", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_mesh_naming_no_part_of_the_model_refuses()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Result<GeometryEditResult> result = GeometryEdit.Reshape(model, cameldata,
            [new EditedPart("Cube", [new Vector3D(1, 2, 7)])]);

        Assert.True(result.IsRefused);
        Assert.Contains("mode3-record-N", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edit_that_matched_nothing_refuses_rather_than_writing_an_unchanged_file()
    {
        // A mod indistinguishable from a working one is the worst outcome: the
        // author installs it and finds the art did not move, with nothing said.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Result<GeometryEditResult> result = GeometryEdit.Reshape(model, cameldata, []);

        Assert.True(result.IsRefused);
        Assert.Contains("nothing to reshape", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_2_model_refuses_because_its_parts_share_a_pool()
    {
        MmbModel model = ReadMmb(new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 });
        CameldataFile cameldata = ReadCameldata(new CameldataBuilder
        {
            Mode = 2,
            Positions = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)],
        });

        Result<GeometryEditResult> result = GeometryEdit.Reshape(model, cameldata,
            [new EditedPart("mode2-record-0", [new Vector3D(1, 2, 3)])]);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("shared between parts", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pool_whose_size_changed_refuses_at_the_format_boundary()
    {
        // Not reachable through GeometryEdit, which never resizes. It guards the
        // opening the format gives an edit, because a pool that grew would leave
        // every base index after it pointing somewhere else.
        (_, Mode3Cameldata cameldata) = Pair();

        Result<Mode3Cameldata> result = cameldata.WithPositions(
            [.. cameldata.Xy, new Vector2(0, 0)], cameldata.Z);

        Assert.True(result.IsRefused);
        Assert.Contains("cannot change", result.Refusal.Message, StringComparison.Ordinal);
    }

    private static GeometryEditResult Reshaped(MmbModel model, Mode3Cameldata cameldata, ImmutableArray<Vector3D> positions)
    {
        Result<GeometryEditResult> result = GeometryEdit.Reshape(
            model, cameldata, [new EditedPart("mode3-record-0", positions)]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static Refusal ReshapeRefused(MmbModel model, Mode3Cameldata cameldata, ImmutableArray<Vector3D> positions)
    {
        Result<GeometryEditResult> result = GeometryEdit.Reshape(
            model, cameldata, [new EditedPart("mode3-record-0", positions)]);

        Assert.True(result.IsRefused);
        return result.Refusal;
    }

    /// <summary>One three-vertex mode-3 part, on one depth plane.</summary>
    private (MmbModel Model, Mode3Cameldata Cameldata) Pair(uint[]? entries = null)
    {
        MmbModel model = ReadMmb(new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = entries ?? [0, 1, 2],
            EntrySize = 2,
        });

        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [7f],
            PackedZ = [0u],
        });

        return (model, cameldata);
    }

    private MmbModel ReadMmb(MmbFileBuilder builder) => MmbReader.Read(Load(builder.Build(), "mmb")).Value;

    private CameldataFile ReadCameldata(CameldataBuilder builder) =>
        CameldataReader.Read(Load(builder.Build(), "cameldata")).Value;

    private SourceFile Load(byte[] bytes, string extension)
    {
        string path = Path.Combine(_directory.FullName, $"asset-{Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }
}
