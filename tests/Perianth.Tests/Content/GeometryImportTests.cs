using System;
using System.Buffers.Binary;
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
/// Choosing between moving a part's points and redrawing it.
/// </summary>
/// <remarks>
/// The choice is the whole of this type, and it is the piece that can be wrong
/// without anything refusing: sending an edit to the operation that cannot
/// express it produces a refusal, which is visible, but sending one to an
/// operation that <em>can</em> express it differently would produce a file. So
/// these assert which way each edit went, not merely that it succeeded.
/// </remarks>
public sealed class GeometryImportTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Moving_points_that_stay_together_is_a_reshape_that_writes_no_payload()
    {
        Fixture pair = Pair(entries: [0, 0, 1]);

        GeometryImportResult edit = Applied(pair,
            [new Vector3D(9, 9, 7), new Vector3D(9, 9, 7), new Vector3D(3, 4, 7)]);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(0, edit.Rebuilt);
        Assert.Equal(new Vector2(9, 9), edit.Cameldata.Xy[0]);

        // The reshape's claim is about the *payload*, which is where the cost
        // is: identifiers and triangles are untouched, so nothing resizes and
        // no offset moves.
        MmbModel after = MmbReader.Read(
            SourceFile.FromMemory("after.mmb", edit.Model)).Value;
        Assert.Equal(pair.Model.Parts[0].Payload.ToArray(), after.Parts[0].Payload.ToArray());
        Assert.Equal(pair.Model.Parts[0].Descriptor, after.Parts[0].Descriptor);
    }

    [Fact]
    public void A_reshaped_part_states_the_volume_it_moved_to()
    {
        // A reshape wrote no MMB at all for three rungs, and the bounding block
        // is derived from geometry and lives in the MMB -- so every reshaped
        // part went on claiming the volume it used to fill. The direction that
        // hurts is growth: a part that claims less than it fills is one the
        // game may cull while it is on screen, and an offline render cannot
        // show it, because it does not cull.
        Fixture pair = Pair(entries: [0, 0, 1]);

        GeometryImportResult edit = Applied(pair,
            [new Vector3D(90, 90, 7), new Vector3D(90, 90, 7), new Vector3D(-90, -80, 7)]);

        Assert.Equal(1, edit.Reshaped);
        MmbModel after = MmbReader.Read(
            SourceFile.FromMemory("after.mmb", edit.Model)).Value;

        // The box, which is the first six of the twelve derived floats.
        Assert.Equal(-90f, after.Parts[0].Values[0]);
        Assert.Equal(-80f, after.Parts[0].Values[1]);
        Assert.Equal(90f, after.Parts[0].Values[3]);
        Assert.Equal(90f, after.Parts[0].Values[4]);
        Assert.NotEqual(pair.Model.Parts[0].Values, after.Parts[0].Values);
    }

    [Fact]
    public void Pulling_a_shared_corner_apart_is_a_rebuild_rather_than_a_refusal()
    {
        // The move this whole type exists for. A reshape refuses here -- one
        // slot cannot hold two positions -- and before the split that refusal
        // was the answer, so redrawing a part was unreachable from either front
        // end even though it was built.
        Fixture pair = Pair(entries: [0, 0, 1]);

        GeometryImportResult edit = Applied(pair,
            [new Vector3D(9, 9, 7), new Vector3D(-9, -9, 7), new Vector3D(3, 4, 7)]);

        Assert.Equal(0, edit.Reshaped);
        Assert.Equal(1, edit.Rebuilt);
        Assert.Equal(1, edit.Triangles);

        // Three vertices at three places now, so the part names three slots
        // where it named two, and its payload says so.
        Assert.NotEqual(pair.ModelBytes, edit.Model);
        Assert.Equal(new Vector2(9, 9), edit.Cameldata.Xy[0]);
        Assert.Equal(new Vector2(-9, -9), edit.Cameldata.Xy[1]);
        Assert.Equal(new Vector2(3, 4), edit.Cameldata.Xy[2]);
    }

    [Fact]
    public void Giving_points_on_one_plane_different_depths_is_a_rebuild_that_grows_the_depth_pool()
    {
        // Depth is the second thing a reshape cannot express, so this goes to
        // the rebuild. It used to refuse there too, because the depth pool was
        // not re-based and the part had one slot. It is re-based now, so the
        // part grows a second depth and keeps it.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair,
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 50), new Vector3D(5, 6, 7)]);

        Assert.Equal(1, result.Rebuilt);
        Assert.Equal([7f, 50f], result.Cameldata.Z);
    }

    [Fact]
    public void More_depths_than_the_index_can_name_widens_the_index()
    {
        // The last of the depth limits, and it used to refuse here: a one-bit
        // index names two entries however many the pool holds. Widening is a
        // model-wide re-cut of the packed stream, so it is chosen once from the
        // hungriest part rather than negotiated per record -- three planes want
        // two bits, and every record in the file is read at two bits afterwards.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair,
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 50), new Vector3D(5, 6, 90)]);

        Assert.Equal(1, result.Rebuilt);
        Assert.Equal([7f, 50f, 90f], result.Cameldata.Z);
        Assert.All(result.Cameldata.Constants, c => Assert.Equal(2, c.ZBitWidth));

        // Three two-bit fields reading 0, 1 and 2, from the low bit up.
        Assert.Equal([0b10_01_00u], result.Cameldata.PackedZ);
    }

    [Fact]
    public void The_width_is_chosen_from_the_hungriest_part_whichever_order_they_arrive_in()
    {
        // One width serves the whole model, so it is the maximum across every
        // part being written and not whichever part happened to be looked at
        // last. Both orders are stated because either one alone passes under a
        // rule that reads a single part -- which is the mutation that survived
        // the first draft of these tests.
        EditedPart deep = new("mode3-record-0",
        [
            new Vector3D(1, 1, 1), new Vector3D(2, 2, 2), new Vector3D(3, 3, 3),
            new Vector3D(4, 4, 4), new Vector3D(5, 5, 5), new Vector3D(6, 6, 5),
        ]);
        EditedPart flat = new("mode3-record-1",
        [
            new Vector3D(1, 1, 9), new Vector3D(2, 2, 9), new Vector3D(3, 3, 9),
            new Vector3D(4, 4, 9), new Vector3D(5, 5, 9), new Vector3D(6, 6, 9),
        ]);

        Assert.All(
            [Applied(TwoParts(), [deep, flat]), Applied(TwoParts(), [flat, deep])],
            result =>
            {
                Assert.Equal(2, result.Rebuilt);
                Assert.All(result.Cameldata.Constants, c => Assert.Equal(4, c.ZBitWidth));
                Assert.Equal([1f, 2f, 3f, 4f, 5f, 9f], result.Cameldata.Z);
            });
    }


    [Fact]
    public void A_resized_part_writes_a_model_that_can_be_read_back()
    {
        // The check the other assertions here could not make. They read the
        // fields the edit meant to change; a descriptor also carries fields
        // *derived* from those, and a stale one produces a file that looks
        // written and refuses on the way back in. Word 3 is a check field a
        // mode-3 record must carry its vertex count in, and word 6 is where the
        // index buffer begins -- both were left at the values they had.
        //
        // Reading it back is not enough on its own: the assembler is what
        // enforces those two, so the part is assembled rather than merely
        // parsed.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair,
        [
            new Vector3D(1, 1, 7), new Vector3D(2, 2, 7), new Vector3D(3, 3, 7),
            new Vector3D(4, 4, 7), new Vector3D(5, 5, 7), new Vector3D(6, 6, 7),
        ]);

        MmbModel written = MmbReader.Read(Load(result.Model, "mmb")).Value;
        MmbGeometryDescriptor descriptor = written.Parts[0].Descriptor;
        Assert.Equal(6u, descriptor.VertexCount);
        Assert.Equal(6u, descriptor.SecondaryVertexCount);
        Assert.Equal(12u, descriptor.IndexOffset);
        Assert.Equal(12u, descriptor.PayloadLength);

        Result<ImmutableArray<int>> ids = GeometryAssembler.LocalIds(written.Parts[0]);
        Assert.True(ids.IsSuccess, ids.IsRefused ? ids.Refusal.Message : "");
        Assert.Equal(6, ids.Value.Length);

        // The tail restates both counts, and a record disagreeing with itself
        // is exactly what a partial update produces. The third word from the
        // end is the vertex count and the last is the payload length.
        ReadOnlySpan<byte> tail = written.Parts[0].TailBytes.Span;
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(tail[^12..]));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(tail[^4..]));
    }

    [Fact]
    public void A_redraw_of_a_part_that_cannot_be_read_refuses_rather_than_vanishing()
    {
        // "Cannot tell whether the arrangement changed" is not "it did not".
        // Reading it as unchanged sends the edit to the reshape, which writes no
        // payload, so a part given twice the triangles comes back drawing what it
        // drew and nothing says so -- a mod that installs and looks broken for no
        // stated reason. The comparison that would have caught it needs the
        // host's own identifiers, which is exactly what such a part will not give
        // up.
        Refusal refusal = Refused(Indexed(trailing: 4),
        [
            new EditedPart(
                "mode3-record-0",
                [new(0, 0, 7), new(1, 0, 7), new(0, 1, 7), new(2, 2, 7)],
                [0, 1, 2, 3],
                [0, 1, 2, 1, 2, 3]),
        ]);

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
    }

    [Fact]
    public void A_redrawn_part_states_the_volume_it_now_occupies()
    {
        // The twelve floats at the head of a part envelope are its bounding box
        // and its vertex radii, so they are derived from the geometry and go
        // stale the moment it changes. They were carried over unchanged until
        // the census identified them, which left a redrawn part claiming a
        // volume it had left -- and a part the game may cull while it is on
        // screen is invisible to every check this project has, because an
        // offline render does not cull.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair,
        [
            new Vector3D(0, 0, 7), new Vector3D(10, 0, 7), new Vector3D(0, 20, 7),
            new Vector3D(10, 20, 7), new Vector3D(10, 0, 7), new Vector3D(0, 20, 7),
        ]);

        ImmutableArray<float> values = MmbReader.Read(Load(result.Model, "mmb")).Value.Parts[0].Values;

        // Minimum, maximum and centre of what it now draws.
        Assert.Equal([0f, 0f, 7f], values[0..3]);
        Assert.Equal([10f, 20f, 7f], values[3..6]);
        Assert.Equal([5f, 10f, 7f], values[7..10]);

        // The radii are of the vertices, not of the box: furthest from the
        // origin is (10,20,7), and furthest from the centre is any corner.
        Assert.Equal(MathF.Sqrt(100f + 400f + 49f), values[6], 4);
        Assert.Equal(MathF.Sqrt(25f + 100f), values[10], 4);
        Assert.Equal(5f, values[11], 4);
    }

    [Fact]
    public void A_width_is_only_ever_widened_to_one_the_engine_reads()
    {
        // Five planes want three bits and get four. The shader loads one word
        // per index and adds no padding, so a width that does not divide 32
        // truncates every field straddling a boundary -- which is why the choice
        // rounds up to a power of two rather than to what fits exactly.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair,
        [
            new Vector3D(1, 1, 1), new Vector3D(2, 2, 2), new Vector3D(3, 3, 3),
            new Vector3D(4, 4, 4), new Vector3D(5, 5, 5), new Vector3D(6, 6, 5),
        ]);

        Assert.Equal([1f, 2f, 3f, 4f, 5f], result.Cameldata.Z);
        Assert.Equal(4, result.Cameldata.Constants[0].ZBitWidth);
    }

    [Fact]
    public void An_unchanged_model_comes_back_as_a_reshape_that_moved_nothing()
    {
        // The identity case, and the one a front end refuses on: it must not be
        // read as a rebuild, or a file nobody edited would write a mod.
        Fixture pair = Pair(entries: [0, 0, 1]);

        GeometryImportResult edit = Applied(pair,
            [new Vector3D(1, 2, 7), new Vector3D(1, 2, 7), new Vector3D(3, 4, 7)]);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(0, edit.Rebuilt);
        Assert.False(edit.Moved);
    }

    [Fact]
    public void A_part_that_kept_its_corners_and_one_that_did_not_are_one_import()
    {
        // The split is per part, so a model whose hat was redrawn and whose arm
        // was merely stretched needs neither two passes nor a choice.
        Fixture pair = TwoParts();

        Result<GeometryImportResult> result = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
        [
            new EditedPart("mode3-record-0",
                [new Vector3D(9, 9, 7), new Vector3D(9, 9, 7), new Vector3D(3, 4, 7)]),
            new EditedPart("mode3-record-1",
                [new Vector3D(1, 1, 8), new Vector3D(2, 2, 8), new Vector3D(3, 3, 8)]),
        ]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        Assert.Equal(1, result.Value.Reshaped);
        Assert.Equal(1, result.Value.Rebuilt);
    }

    [Fact]
    public void A_count_that_shrank_is_a_rebuild_and_says_what_it_now_draws()
    {
        // This used to refuse, and the refusal named Blender's "Merge Vertices"
        // as the usual cause -- the one importer setting that quietly welds
        // points. A rebuild can now do it, so refusing would be refusing the
        // thing the tool is for.
        //
        // The protection moves rather than disappearing: the result says how
        // many triangles the part now draws, so a weld nobody intended shows up
        // as a number that is not the one the author expects. A silent success
        // is what this guards against, not a changed count.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(
            pair, [new EditedPart("mode3-record-0", [
                new(1, 2, 7), new(3, 4, 7), new(5, 6, 7),
                new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)])]);

        Assert.Equal(1, result.Rebuilt);
        Assert.Equal(2, result.Triangles);
        Assert.True(result.Moved);
    }

    [Fact]
    public void An_edited_file_with_no_meshes_refuses_rather_than_writing_nothing()
    {
        Fixture pair = Pair();

        Result<GeometryImportResult> result = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata, []);

        Assert.True(result.IsRefused);
        Assert.Contains("no meshes", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_2_model_refuses_before_anything_is_chosen()
    {
        MmbFileBuilder builder = new() { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 };
        byte[] bytes = builder.Build();
        SourceFile file = Load(bytes, "mmb");
        MmbModel model = MmbReader.Read(file).Value;
        CameldataFile cameldata = ReadCameldata(new CameldataBuilder
        {
            Mode = 2,
            Positions = [new(1, 2, 3), new(4, 5, 6), new(7, 8, 9)],
        });

        Result<GeometryImportResult> result = GeometryImport.Apply(file, model, cameldata,
            [new EditedPart("mode2-record-0", [new Vector3D(1, 2, 3)])]);

        Assert.True(result.IsRefused);
        Assert.Contains("shared between parts", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stated_pool_slot_decides_the_split_as_well_as_the_edit()
    {
        // Which points share a corner is read from the attribute when the file
        // carries one. Taking the vertex order instead would read this mesh --
        // arriving reversed, as a re-welding tool leaves it -- as having torn
        // its corners apart, and redraw a part that was only moved.
        Fixture pair = Pair(entries: [0, 0, 1]);

        Result<GeometryImportResult> result = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(3, 4, 7), new Vector3D(9, 9, 7), new Vector3D(9, 9, 7)],
                [1, 0, 0]),
        ]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        Assert.Equal(1, result.Value.Reshaped);
        Assert.Equal(0, result.Value.Rebuilt);
        Assert.Equal(new Vector2(9, 9), result.Value.Cameldata.Xy[0]);
        Assert.Equal(new Vector2(3, 4), result.Value.Cameldata.Xy[1]);
    }

    [Fact]
    public void An_indexed_part_is_redrawn_through_the_second_payload_path()
    {
        // Its vertices and its corners are different lists, and both are
        // written: the identifiers say which pool entry each vertex reads, the
        // index buffer says which vertex each corner draws. Changing the corners
        // is what makes this a redraw -- an indexed part can be re-topologised
        // without a single point moving.
        Fixture pair = Indexed();

        Result<GeometryImportResult> result = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(9, 9, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)],
                [0, 1, 2],
                [1, 0, 2]),
        ]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        Assert.Equal(1, result.Value.Rebuilt);
        Assert.Equal(0, result.Value.Reshaped);
        Assert.Equal(new Vector2(9, 9), result.Value.Cameldata.Xy[0]);
        Assert.Equal(new Vector2(3, 4), result.Value.Cameldata.Xy[1]);
    }

    [Fact]
    public void An_indexed_part_stores_its_indices_with_the_bias_the_reader_removes()
    {
        // The reader subtracts BaseBias from every stored index, so a writer
        // that omitted it would produce a file that loads and draws the wrong
        // triangles wherever the bias is not zero. Read the result back and
        // require the same corners.
        Fixture pair = Indexed(bias: 4);

        Result<GeometryImportResult> result = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(9, 9, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)],
                [0, 1, 2],
                [2, 1, 0]),
        ]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");

        MmbModel written = MmbReader.Read(SourceFile.FromMemory("written.mmb", result.Value.Model)).Value;
        Assert.Equal([2, 1, 0], written.Parts[0].StoredIndices);
    }

    [Fact]
    public void Re_indexing_an_indexed_part_alone_is_a_redraw_and_not_a_silent_nothing()
    {
        // The gap a synthetic test found after the whole corpus was green: an
        // unedited round trip never re-indexes anything, so nothing measured
        // could show that a changed index buffer with every point left where it
        // was would be reshaped -- writing no payload, and losing the edit.
        Fixture pair = Indexed();
        ImmutableArray<Vector3D> unmoved =
            [new Vector3D(1, 2, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)];

        Result<GeometryImportResult> kept = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
            [new EditedPart("mode3-record-0", unmoved, [0, 1, 2], [0, 1, 2])]);
        Result<GeometryImportResult> reordered = GeometryImport.Apply(pair.ModelFile, pair.Model, pair.Cameldata,
            [new EditedPart("mode3-record-0", unmoved, [0, 1, 2], [2, 0, 1])]);

        // The same points both times. Only the corners differ, and only the
        // second may be read as a redraw.
        Assert.True(kept.IsSuccess && reordered.IsSuccess);
        Assert.Equal(0, kept.Value.Rebuilt);
        Assert.False(kept.Value.Moved);
        Assert.Equal(1, reordered.Value.Rebuilt);
        Assert.True(reordered.Value.Moved);
    }

    [Fact]
    public void An_indexed_part_may_come_back_with_fewer_points_than_it_had()
    {
        // Blender drops a vertex no triangle references, correctly, so a mesh
        // can return shorter than it left. The payload is written to what the
        // mesh has rather than padded back out to what the record held, and the
        // descriptor says the shorter count.
        Fixture pair = Indexed(vertices: 4);

        GeometryImportResult result = Applied(pair,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(9, 9, 7), new Vector3D(3, 4, 7), new Vector3D(5, 6, 7)],
                [0, 1, 2],
                [1, 0, 2]),
        ]);

        MmbGeometryDescriptor written = MmbReader.Read(Load(result.Model, "mmb")).Value.Parts[0].Descriptor;
        Assert.Equal(3u, written.VertexCount);
        Assert.Equal(3u, written.IndexCount);
        Assert.Equal(6u, written.IndexOffset);
        Assert.Equal(12u, written.PayloadLength);
    }

    [Fact]
    public void An_indexed_part_may_be_given_more_points_than_it_held()
    {
        // The limit that stood longest, and it was never the format's. The
        // payload is written afresh rather than overwritten, so both arrays are
        // laid down at the size the mesh needs and the index buffer moves along
        // to follow the identifiers.
        Fixture pair = Indexed();

        GeometryImportResult result = Applied(pair,
        [
            new EditedPart(
                "mode3-record-0",
                [new Vector3D(1, 1, 7), new Vector3D(2, 2, 7), new Vector3D(3, 3, 7), new Vector3D(4, 4, 7)],
                [0, 1, 2, 3],
                [1, 0, 2]),
        ]);

        MmbGeometryDescriptor written = MmbReader.Read(Load(result.Model, "mmb")).Value.Parts[0].Descriptor;
        Assert.Equal(4u, written.VertexCount);
        Assert.Equal(8u, written.IndexOffset);
        Assert.Equal(14u, written.PayloadLength);
    }

    [Fact]
    public void An_indexed_part_stores_one_identifier_per_vertex_not_per_distinct_point()
    {
        // Two numbers that agree on most meshes and must not be confused. The
        // identifiers are indexed by vertex number, because that is what a
        // corner names, so there is one per vertex even where two vertices sit
        // at the same place and share a pool slot. The distinct count is the
        // pool slice, a different thing.
        //
        // Confusing them writes a short identifier array, which puts the index
        // buffer at the wrong offset and makes the part draw somebody else's
        // corners. Vertex three repeats vertex one here, which is what makes the
        // two counts differ at all.
        GeometryImportResult result = Applied(Indexed(vertices: 4), [
            new EditedPart(
                "mode3-record-0",
                [new(0, 0, 7), new(1, 0, 7), new(0, 1, 7), new(1, 0, 7)],
                [0, 1, 2, 3],
                [0, 1, 2, 1, 2, 3]),
        ]);

        // Three distinct points in the pool, four identifiers in the payload.
        Assert.Equal(3, result.Cameldata.Xy.Length);

        MmbGeometryDescriptor written = MmbReader.Read(Load(result.Model, "mmb")).Value.Parts[0].Descriptor;
        Assert.Equal(4u, written.VertexCount);
        Assert.Equal(8u, written.IndexOffset);
        Assert.Equal(20u, written.PayloadLength);
    }

    [Fact]
    public void An_indexed_payload_holding_anything_else_refuses_rather_than_inventing_it()
    {
        // The guard on the whole rebuild. A payload longer than its two arrays
        // holds bytes nothing here has decoded, and writing the payload means
        // writing those too. No editable record in the corpus is like this --
        // 1,595 of 1,595 account for every byte -- which is exactly why the
        // predicate is checked on the record rather than assumed from the count.
        Refusal refusal = Refused(Indexed(trailing: 4),
        [
            new EditedPart(
                "mode3-record-0",
                [new(0, 0, 7), new(1, 0, 7), new(0, 1, 7), new(2, 2, 7)],
                [0, 1, 2, 3],
                [0, 1, 2, 1, 2, 3]),
        ]);

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("invent the rest", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direct_part_may_be_given_more_triangles_than_it_had()
    {
        // The limit this lifts existed because a payload's length sat beside an
        // absolute file offset "in a file nothing can re-index". The container
        // is now written afresh, so every offset is recomputed as the file is
        // laid out and a part that grew pushes the rest along.
        Fixture pair = Pair();

        GeometryImportResult result = Applied(pair, [
            new EditedPart("mode3-record-0", [
                new(0, 0, 7), new(1, 0, 7), new(0, 1, 7),
                new(2, 0, 7), new(3, 0, 7), new(2, 1, 7)]),
        ]);

        Assert.Equal(1, result.Rebuilt);
        Assert.Equal(2, result.Triangles);

        // The MMB says so too: six vertices, and a payload of two bytes each.
        MmbModel written = MmbReader.Read(Load(result.Model, "mmb")).Value;
        Assert.Equal(6u, written.Parts[0].Descriptor.VertexCount);
        Assert.Equal(12u, written.Parts[0].Descriptor.PayloadLength);
    }

    [Fact]
    public void A_direct_part_may_use_more_points_than_its_slice_held()
    {
        // The other lifted limit: a record's pool slice was sized exactly to
        // what it used, with no spare slot anywhere, so the slot after the last
        // belonged to the next part. Re-basing moves that part instead.
        Fixture pair = TwoParts();

        GeometryImportResult result = Applied(pair, [
            new EditedPart("mode3-record-0", [
                new(0, 0, 7), new(1, 0, 7), new(0, 1, 7),
                new(2, 0, 7), new(3, 0, 7), new(2, 1, 7)]),
        ]);

        Assert.Equal(1, result.Rebuilt);

        // Six distinct points where the slice held three, so the second part's
        // slice has moved along by three and its own points came with it.
        Assert.Equal(6u, result.Cameldata.Constants[1].XyBase);
        Assert.Equal(new System.Numerics.Vector2(1, 1), result.Cameldata.Xy[6]);
        Assert.Equal(new System.Numerics.Vector2(2, 2), result.Cameldata.Xy[7]);
    }

    [Fact]
    public void An_indexed_part_may_be_given_more_triangles_than_it_drew()
    {
        // The last of rung 3's limits. One triangle becomes two, which needs
        // both arrays longer at once -- the identifiers for the new point and
        // the index buffer for the new corners -- and the index buffer's offset
        // moves because the identifiers before it did.
        GeometryImportResult result = Applied(Indexed(), [
            new EditedPart(
                "mode3-record-0",
                [new(0, 0, 7), new(1, 0, 7), new(0, 1, 7), new(2, 2, 7)],
                [0, 1, 2, 3],
                [0, 1, 2, 1, 2, 3]),
        ]);

        Assert.Equal(1, result.Rebuilt);
        Assert.Equal(2, result.Triangles);

        MmbGeometryDescriptor written = MmbReader.Read(Load(result.Model, "mmb")).Value.Parts[0].Descriptor;
        Assert.Equal(4u, written.VertexCount);
        Assert.Equal(6u, written.IndexCount);
        Assert.Equal(8u, written.IndexOffset);
        Assert.Equal(20u, written.PayloadLength);
    }

    [Fact]
    public void A_part_that_carries_its_own_texture_coordinates_can_be_redrawn()
    {
        // This used to refuse outright: there were no new coordinates to write,
        // so new geometry would have kept the old ones and been painted wrongly.
        // A GLB carries them. It is the branch an imported mesh most wants to be
        // on, because the computed alternative is a planar projection.
        // Two triangles where the part drew one, so this is a rebuild rather
        // than a reshape. A reshape keeps the coordinates it found and never
        // needed any, which is why the refusal only ever lived on this side.
        Fixture pair = Painted();

        GeometryImportResult result = Applied(pair, [
            new EditedPart(
                "mode3-record-0",
                [new(0, 0, 7), new(1, 0, 7), new(0, 1, 7),
                 new(2, 0, 7), new(3, 0, 7), new(2, 1, 7)],
                default,
                default,
                [new(0.0, 0.0), new(1.0, 0.0), new(0.0, 1.0),
                 new(0.5, 0.5), new(1.0, 0.5), new(0.5, 1.0)]),
        ]);

        Assert.Equal(1, result.Rebuilt);

        // Read back through the same unpacking the game uses.
        Result<Vector2D> first = Uv0Projection.Unified(
            result.Cameldata.Uv0[0], result.Cameldata.Constants[0].Uv0ScaleIndex);
        Result<Vector2D> last = Uv0Projection.Unified(
            result.Cameldata.Uv0[5], result.Cameldata.Constants[0].Uv0ScaleIndex);

        Assert.Equal(6, result.Cameldata.Uv0.Length);
        Assert.Equal(0.0, first.Value.X, 4);
        Assert.Equal(0.5, last.Value.X, 4);
        Assert.Equal(1.0, last.Value.Y, 4);
    }

    [Fact]
    public void A_painted_part_redrawn_without_coordinates_still_refuses()
    {
        // The refusal narrowed rather than disappearing. A mesh that brought no
        // coordinates would keep the ones it replaced, which is the case that
        // was always wrong.
        Refusal refusal = Refused(Painted(), [
            new EditedPart("mode3-record-0", [
                new(0, 0, 7), new(1, 0, 7), new(0, 1, 7),
                new(2, 0, 7), new(3, 0, 7), new(2, 1, 7)]),
        ]);

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("the mesh brought none", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>One three-vertex part that reads the unified UV0 array.</summary>
    private Fixture Painted()
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 1, 2],
            EntrySize = 2,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [7f],
            Uv0 = [0, 0, 0],
            PackedZ = [0u],

            // Bit 0 selects the unified array; bits 1 and 2 select scale 1,
            // which is the plain zero-to-one range.
            PackedFlags = 1 | (2 << 1),
        });

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    /// <summary>One three-corner indexed part, whose vertices are its own list.</summary>
    private Fixture Indexed(uint bias = 0, uint vertices = 3, int trailing = 0)
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = vertices,
            PositionEntries = [.. Enumerable.Range(0, (int)vertices).Select(i => (uint)i)],
            EntrySize = 2,
            Indices = [.. Enumerable.Range(0, 3).Select(i => (ushort)(i + bias))],
            BaseBias = bias,
            TrailingBytes = trailing,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6), new(7, 8)],
            Z = [7f],
            PackedZ = [0u],
        });

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    private static GeometryImportResult Applied(Fixture pair, ImmutableArray<EditedPart> parts)
    {
        Result<GeometryImportResult> whole = GeometryImport.Apply(
            pair.ModelFile, pair.Model, pair.Cameldata, parts);

        Assert.True(whole.IsSuccess, whole.IsRefused ? whole.Refusal.Message : "no outcome");
        return whole.Value;
    }

    [Fact]
    public void A_redrawn_part_loses_the_curves_that_trimmed_the_part_it_replaced()
    {
        // The engine trims a part's fragments with per-vertex Bezier selectors,
        // so a redrawn part left carrying the old ones is cut to the outline of
        // the shape it replaced. That is not a hypothetical: it is what two
        // probes of batch 3 drew in game, and what an offline render could not
        // show until the renderer learned to apply coverage. Roadmap §10.154.
        Fixture pair = Pair();

        // Six vertices where the part had three -- two triangles rather than
        // one -- so this is a rebuild rather than a reshape and the selectors
        // cannot be carried across.
        GeometryImportResult edit = Applied(pair, [
            new EditedPart("mode3-record-0", [
                new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f),
                new(0f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)])]);

        Assert.Equal(1, edit.Rebuilt);

        Mode3Constant constant = edit.Cameldata.Constants[0];
        Assert.True(constant.CoverageAgreesWith(6));

        for (int vertex = 0; vertex < 6; vertex++)
        {
            uint selector = (Word(edit.Cameldata, constant.CoverageBitsBase + (uint)(vertex / 16))
                >> ((vertex % 16) * 2)) & 3;
            uint sign = (Word(edit.Cameldata, constant.CoverageSignBase + (uint)(vertex / 32))
                >> (vertex % 32)) & 1;

            Assert.Equal(Mode3Cameldata.NeutralCoverageSelector, selector);
            Assert.Equal(Mode3Cameldata.NeutralCoverageSign, sign);
        }
    }

    private static uint Word(Mode3Cameldata file, uint index) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            file.BezierBytes.Span[(int)(index * 4)..]);

    private static Refusal Refused(Fixture pair, ImmutableArray<EditedPart> parts)
    {
        Result<GeometryImportResult> whole = GeometryImport.Apply(
            pair.ModelFile, pair.Model, pair.Cameldata, parts);

        Assert.True(whole.IsRefused);
        return whole.Refusal;
    }

    private static GeometryImportResult Applied(Fixture pair, ImmutableArray<Vector3D> positions)
    {
        Result<GeometryImportResult> result = GeometryImport.Apply(
            pair.ModelFile, pair.Model, pair.Cameldata, [new EditedPart("mode3-record-0", positions)]);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private static Refusal Refused(Fixture pair, ImmutableArray<Vector3D> positions)
    {
        Result<GeometryImportResult> result = GeometryImport.Apply(
            pair.ModelFile, pair.Model, pair.Cameldata, [new EditedPart("mode3-record-0", positions)]);

        Assert.True(result.IsRefused);
        return result.Refusal;
    }

    /// <summary>One three-vertex mode-3 part, on one depth plane.</summary>
    [Fact]
    public void A_mesh_past_the_models_end_makes_the_part_it_names()
    {
        // Rung 2 of the authoring ladder, and it is not a feature of its own:
        // an author who duplicated a part in Blender gets a mesh named past the
        // model's last, and the model grows to hold it. Refusing was the honest
        // thing to do while nothing could add a part.
        Fixture pair = Bindable();

        GeometryImportResult edit = Applied(pair, [
            new EditedPart("mode3-record-0", [
                new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)]),
            new EditedPart("mode3-record-1", [
                new(9, 9, 7), new(8, 8, 7), new(7, 7, 7)]),
        ]);

        Assert.Equal(1, edit.Added);
        Assert.True(edit.Moved);

        // Which node the new part binds to is chosen for the author, by the part
        // it copies, so it is reported. A node the animation hides makes the new
        // part invisible with nothing in the mod folder to say why.
        Assert.Equal(pair.Model.Parts[^1].BindingNode, edit.AddedBinding);
        Assert.NotEmpty(edit.AddedBinding);
        MmbModel written = MmbReader.Read(Load(edit.Model, "mmb")).Value;
        Assert.Equal(2, written.Parts.Length);
        Assert.Equal(2, edit.Cameldata.Constants.Length);
        Assert.Equal([0, 1], written.Parts.Select(part => part.SourceOrdinal));

        // The new part draws the mesh that asked for it, not the copy it started
        // as: its own slice of the pool holds the edited positions.
        Mode3Constant added = edit.Cameldata.Constants[1];
        Assert.Equal(
            [new Vector2(9, 9), new Vector2(8, 8), new Vector2(7, 7)],
            edit.Cameldata.Xy.Skip((int)added.XyBase).Take(3));
    }

    [Fact]
    public void A_model_that_only_gained_a_part_still_counts_as_changed()
    {
        // Moved is what a front end refuses on. A duplicate exported without
        // being touched is a small thing to do and the mod is not a no-op, so
        // the count has to reach that predicate.
        Fixture pair = Bindable();

        GeometryImportResult edit = Applied(pair, [
            new EditedPart("mode3-record-0", [
                new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)]),
            new EditedPart("mode3-record-1", [
                new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)]),
        ]);

        Assert.Equal(1, edit.Added);
        Assert.Equal(0, edit.Slots);
        Assert.True(edit.Moved);
    }

    [Fact]
    public void A_gap_in_the_new_ordinals_refuses()
    {
        // Blender renames a duplicate rather than renumbering the rest, so a
        // file naming 1 and 3 for a model of 1 has lost one. Inventing part 2 to
        // fill the gap would put a copy of somebody else's geometry into the
        // model unasked.
        Fixture pair = Bindable();

        Result<GeometryImportResult> result = GeometryImport.Apply(
            pair.ModelFile, pair.Model, pair.Cameldata, [
                new EditedPart("mode3-record-0", [
                    new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)]),
                new EditedPart("mode3-record-2", [
                    new(9, 9, 7), new(8, 8, 7), new(7, 7, 7)]),
            ]);

        Assert.True(result.IsRefused);
        Assert.Contains("without a gap", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_new_parts_are_both_made()
    {
        Fixture pair = Bindable();

        GeometryImportResult edit = Applied(pair, [
            new EditedPart("mode3-record-0", [new(1, 2, 7), new(3, 4, 7), new(5, 6, 7)]),
            new EditedPart("mode3-record-1", [new(9, 9, 7), new(8, 8, 7), new(7, 7, 7)]),
            new EditedPart("mode3-record-2", [new(4, 4, 7), new(5, 5, 7), new(6, 6, 7)]),
        ]);

        Assert.Equal(2, edit.Added);
        Assert.Equal(3, edit.Cameldata.Constants.Length);
        Assert.Equal(3, MmbReader.Read(Load(edit.Model, "mmb")).Value.Parts.Length);
    }

    /// <summary>
    /// One part whose label names a node the model declares.
    /// </summary>
    /// <remarks>
    /// The other fixtures leave the node table empty, which is enough for every
    /// edit that works on parts already there. Growing needs a node, because a
    /// new part's label has to name one of its own model's — so this is the
    /// fixture the added-part tests use and the others are left alone.
    /// </remarks>
    private Fixture Bindable()
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 1, 2],
            EntrySize = 2,
            Label = "joint|shape1",
            NodeNames = ["joint"],
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [7f],
            PackedZ = [0u],
        });

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    private Fixture Pair(uint[]? entries = null)
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = entries ?? [0, 1, 2],
            EntrySize = 2,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            Xy = [new(1, 2), new(3, 4), new(5, 6)],
            Z = [7f],
            PackedZ = [0u],
        });

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    /// <summary>Two three-vertex parts with private pool slices, as the format has them.</summary>
    private Fixture TwoParts()
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 0, 1],
            EntrySize = 2,
            Repeat = 2,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)ReadCameldata(new CameldataBuilder
        {
            Mode = 3,
            ConstantCount = 2,
            XyBases = [0, 3],
            ZBases = [0, 1],
            Xy = [new(1, 2), new(3, 4), new(5, 6), new(1, 1), new(2, 2), new(3, 3)],
            Z = [7f, 8f],
            PackedZ = [0u],
        });

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    private CameldataFile ReadCameldata(CameldataBuilder builder) =>
        CameldataReader.Read(Load(builder.Build(), "cameldata")).Value;

    private SourceFile Load(byte[] bytes, string extension)
    {
        string path = Path.Combine(_directory.FullName, $"asset-{Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }

    private sealed record Fixture(
        SourceFile ModelFile, MmbModel Model, Mode3Cameldata Cameldata, byte[] ModelBytes);
}
