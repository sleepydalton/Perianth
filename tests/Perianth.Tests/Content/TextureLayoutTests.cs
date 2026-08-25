using System;
using System.Collections.Immutable;
using System.IO;
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
/// Which of the two texture-layout rules a redrawn part ends up on.
/// </summary>
/// <remarks>
/// <para>
/// A part either works out where each bit of image goes from where its points
/// sit — a projection, right for the flat cut-outs all the shipped art is — or
/// stores the answer per vertex. 86% work it out, so an author who modelled
/// something solid lands on the wrong rule six times in seven, and the same
/// image is smeared down every side of it.
/// </para>
/// <para>
/// <b>Nothing in the written files shows which happened</b>, which is why these
/// tests exist and why the count is reported rather than left implicit: a model
/// painted as its author laid it out and one painted by a projection are the
/// same bytes apart from a flag, and both load.
/// </para>
/// </remarks>
public sealed class TextureLayoutTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"uv-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>A redraw: the shared corner is pulled apart, so a payload is written.</summary>
    private static readonly ImmutableArray<Vector3D> Redrawn =
        [new(9, 9, 7), new(-9, -9, 7), new(3, 4, 7)];

    private static readonly ImmutableArray<Vector2D> Layout =
        [new(0, 0), new(1, 0), new(0.5, 1)];

    /// <summary>A reshape: the shared corner stays shared, so no payload is written.</summary>
    private static readonly ImmutableArray<Vector3D> Reshaped =
        [new(9, 9, 7), new(9, 9, 7), new(3, 4, 7)];

    /// <summary>
    /// A layout whose first two vertices agree, as the entry they share requires.
    /// </summary>
    private static readonly ImmutableArray<Vector2D> Agreeing =
        [new(0.25, 0.5), new(0.25, 0.5), new(0.75, 0.125)];

    /// <summary>The fixture's own points, so nothing about the shape changes.</summary>
    private static readonly ImmutableArray<Vector3D> Unmoved =
        [new(1, 2, 7), new(1, 2, 7), new(3, 4, 7)];

    /// <summary>The coordinates the carrying fixture already holds — all zero.</summary>
    private static readonly ImmutableArray<Vector2D> Original =
        [new(0, 0), new(0, 0), new(0, 0)];

    [Fact]
    public void A_part_that_works_its_layout_out_keeps_doing_so_and_says_the_layout_went_unused()
    {
        GeometryImportResult edit = Applied(Projecting(), Redrawn, Layout, ownUv0: false);

        Assert.Equal(1, edit.Rebuilt);
        Assert.Equal(0, edit.Converted);
        Assert.Equal(1, edit.LayoutIgnored);
        Assert.False(edit.Cameldata.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void Asking_for_it_switches_the_part_to_store_the_layout()
    {
        GeometryImportResult edit = Applied(Projecting(), Redrawn, Layout, ownUv0: true);

        Assert.Equal(1, edit.Converted);
        Assert.Equal(0, edit.LayoutIgnored);
        Assert.True(edit.Cameldata.Constants[0].UsesUnifiedUv0);

        // One stored coordinate per point, in a pool the model did not have.
        Assert.Equal(3, edit.Cameldata.Uv0.Length);
    }

    [Fact]
    public void The_stored_layout_reads_back_as_what_was_given()
    {
        GeometryImportResult edit = Applied(Projecting(), Redrawn, Layout, ownUv0: true);
        Mode3Constant constant = edit.Cameldata.Constants[0];

        for (int slot = 0; slot < Layout.Length; slot++)
        {
            Vector2D read = Uv0Projection.Unified(
                edit.Cameldata.Uv0[(int)constant.Uv0Base + slot], constant.Uv0ScaleIndex).Value;

            Assert.Equal(Layout[slot].X, read.X, 3);
            Assert.Equal(Layout[slot].Y, read.Y, 3);
        }
    }

    [Fact]
    public void Switching_a_part_leaves_its_depth_index_width_alone()
    {
        // The flag lives in the same word as the Z index width. A record that
        // lost that would read its depths at the wrong scale and draw at the
        // wrong distances -- which nothing else here catches.
        //
        // The fixture's width is deliberately not 1: at 1 the bits are zero
        // anyway, so clearing the whole word looks identical to clearing three
        // bits of it, and this test passed against that mutation.
        Fixture pair = Projecting();
        Assert.Equal(4, pair.Cameldata.Constants[0].ZBitWidth);

        GeometryImportResult edit = Applied(pair, Redrawn, Layout, ownUv0: true);

        Assert.Equal(4, edit.Cameldata.Constants[0].ZBitWidth);
    }

    [Fact]
    public void A_switched_part_starts_blank_rather_than_reading_another_parts_layout()
    {
        // A record that was not carrying has no slice of its own, so its stored
        // base points at somebody else's. Copying from it would paint the new
        // part with the neighbour's layout -- which loads, draws, and is wrong.
        //
        // This needs a model where some other record *is* carrying, or the pool
        // is empty and a wrong read finds nothing to take. The second record's
        // stale base points past the pool, which is what a real file may hold.
        Fixture pair = OneOfEachRule();

        GeometryImportResult edit = Applied(
            pair, Redrawn, Layout, ownUv0: true, part: "mode3-record-1");

        Assert.Equal(1, edit.Converted);

        // The part that already carried keeps every one of its own values.
        Mode3Constant first = edit.Cameldata.Constants[0];
        Assert.True(first.UsesUnifiedUv0);
        for (int slot = 0; slot < 3; slot++)
        {
            Assert.Equal(Neighbour[slot], edit.Cameldata.Uv0[(int)first.Uv0Base + slot]);
        }

        // And the switched one holds what the mesh gave it, not what its stale
        // base pointed at.
        Mode3Constant second = edit.Cameldata.Constants[1];
        Assert.NotEqual(first.Uv0Base, second.Uv0Base);
        for (int slot = 0; slot < Layout.Length; slot++)
        {
            Vector2D read = Uv0Projection.Unified(
                edit.Cameldata.Uv0[(int)second.Uv0Base + slot], second.Uv0ScaleIndex).Value;

            Assert.Equal(Layout[slot].X, read.X, 3);
            Assert.Equal(Layout[slot].Y, read.Y, 3);
        }
    }

    [Fact]
    public void Two_points_at_one_place_wanting_different_image_is_refused()
    {
        // A stored coordinate belongs to the position, not to the vertex, so a
        // shape whose faces meet at a shared corner cannot keep one coordinate
        // per face. This used to keep whichever arrived first and say nothing --
        // which on a cube silently loses five faces of six, since every corner
        // is shared by three. Found by building the in-game probe box.
        // The first two vertices share a slot in the fixture, so pulling them
        // apart makes this a rebuild; the first and third then land on one
        // place, wanting different bits of the image.
        Fixture pair = Projecting();
        Result<GeometryImportResult> result = GeometryImport.Apply(
            pair.ModelFile,
            pair.Model,
            pair.Cameldata,
            [new EditedPart(
                "mode3-record-0",
                [new(9, 9, 7), new(-9, -9, 7), new(9, 9, 7)],
                default,
                default,
                [new(0, 0), new(1, 1), new(0.5, 0.5)])],
            ownUv0: true);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("same place", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_points_at_one_place_agreeing_on_the_image_is_fine()
    {
        // The distinction that keeps the refusal from being a blanket ban on
        // welded shapes: sharing a corner is fine, disagreeing about it is not.
        GeometryImportResult edit = Applied(
            Projecting(),
            [new(9, 9, 7), new(-9, -9, 7), new(9, 9, 7)],
            [new(0, 0), new(1, 1), new(0, 0)],
            ownUv0: true);

        Assert.Equal(1, edit.Converted);
        Assert.Equal(1, edit.Rebuilt);
    }

    [Fact]
    public void A_layout_within_zero_to_one_takes_the_narrowest_scale()
    {
        // The stored value is a signed fraction of the scale, so a wider scale
        // spends precision it does not need.
        Assert.Equal(1.0, Scale(Uv0Projection.ScaleFor([new(0, 0), new(1, -1)]).Value));
    }

    [Fact]
    public void A_tiled_layout_takes_the_wider_scale() =>
        Assert.True(Scale(Uv0Projection.ScaleFor([new(0, 0), new(4, 2)]).Value) > 1.0);

    [Fact]
    public void A_layout_wider_than_the_format_holds_is_refused_rather_than_clipped()
    {
        Result<int> chosen = Uv0Projection.ScaleFor([new(0, 0), new(40, 0)]);

        Assert.False(chosen.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, chosen.Refusal.Kind);
    }

    [Fact]
    public void A_part_that_already_stores_its_layout_needs_no_asking()
    {
        // The 14%. They took the mesh's layout before this option existed and
        // must go on doing so without it.
        GeometryImportResult edit = Applied(Carrying(), Redrawn, Layout, ownUv0: false);

        Assert.Equal(0, edit.Converted);
        Assert.Equal(0, edit.LayoutIgnored);
        Assert.True(edit.Cameldata.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void A_mesh_bringing_no_layout_is_neither_converted_nor_complained_about()
    {
        GeometryImportResult edit = Applied(Projecting(), Redrawn, [], ownUv0: true);

        Assert.Equal(0, edit.Converted);
        Assert.Equal(0, edit.LayoutIgnored);
        Assert.False(edit.Cameldata.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void A_reshaped_storing_part_carries_the_layout_its_author_brought()
    {
        // The sharper half of Roadmap §10.121's finding, and no flag is involved.
        // A storing part indexes UV0 by the same identifier as XY, so moving its
        // points without its coordinates leaves the layout describing the shape
        // the part used to be — and the author's edited layout is the one thing
        // they went to a 3D package to change.
        Fixture pair = Carrying();
        GeometryImportResult edit = Applied(pair, Reshaped, Agreeing, ownUv0: false);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(2, edit.Uv0Slots);
        Assert.NotEqual(pair.Cameldata.Uv0[0], edit.Cameldata.Uv0[0]);
        Assert.NotEqual(pair.Cameldata.Uv0[1], edit.Cameldata.Uv0[1]);

        // Still storing: a reshape changes where points are and nothing else.
        Assert.True(edit.Cameldata.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void Relaying_a_layout_without_moving_a_point_is_an_edit()
    {
        // The change detector counted positions and depths, so an author who
        // re-laid a storing part's texture coordinates and touched none of its
        // points was told nothing had moved and advised to use Edit Mode — which
        // is what they had just done. Unwrapping without reshaping is an ordinary
        // thing to do in a 3D package.
        Fixture pair = Carrying();
        GeometryImportResult edit = Applied(pair, Unmoved, Agreeing, ownUv0: false);

        Assert.Equal(0, edit.Slots);
        Assert.Equal(0, edit.Depths);
        Assert.Equal(2, edit.Uv0Slots);
        Assert.True(edit.Moved);
    }

    [Fact]
    public void A_round_trip_that_re_lays_nothing_still_reports_no_change()
    {
        // The complement, and the one the refusal exists for: same points, same
        // coordinates, so the mod would install and do nothing.
        Fixture pair = Carrying();
        GeometryImportResult edit = Applied(pair, Unmoved, Original, ownUv0: false);

        Assert.Equal(0, edit.Uv0Slots);
        Assert.False(edit.Moved);
    }

    [Fact]
    public void A_reshaped_projecting_part_leaves_the_pool_alone()
    {
        // The other 86%. Their UV0 is worked out from position, so there is no
        // entry to move and writing one would be writing into another record's
        // slice — the pool is shared between the records that use it.
        Fixture pair = Projecting();
        GeometryImportResult edit = Applied(pair, Reshaped, Agreeing, ownUv0: false);

        Assert.Equal(0, edit.Uv0Slots);
        Assert.Equal(pair.Cameldata.Uv0, edit.Cameldata.Uv0);
    }

    [Fact]
    public void A_reshaped_storing_part_refuses_a_seam_its_points_cannot_hold()
    {
        // Two vertices share one pool entry, so they share one coordinate. Giving
        // them different ones is a seam, and a seam needs a vertex the part does
        // not have — the same shape of refusal as pulling a shared corner apart.
        Result<GeometryImportResult> edit = Attempt(Carrying(), Reshaped, Layout, ownUv0: false);

        Assert.True(edit.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, edit.Refusal.Kind);
        Assert.Contains("texture coordinate", edit.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reshaped_storing_part_refuses_a_layout_of_the_wrong_size()
    {
        Result<GeometryImportResult> edit =
            Attempt(Carrying(), Reshaped, [new(0.25, 0.5)], ownUv0: false);

        Assert.True(edit.IsRefused);
        Assert.Contains("one for each", edit.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reshaped_storing_part_that_brings_no_layout_keeps_the_one_it_had()
    {
        // Not a refusal, unlike a redraw. A redrawn part's old coordinates
        // describe an arrangement that no longer exists; a reshaped part's still
        // describe its own points, so keeping them is the honest answer and is
        // what every reshape did before this.
        Fixture pair = Carrying();
        GeometryImportResult edit = Applied(pair, Reshaped, [], ownUv0: false);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(0, edit.Uv0Slots);
        Assert.Equal(pair.Cameldata.Uv0, edit.Cameldata.Uv0);
    }

    [Fact]
    public void A_reshape_says_so_rather_than_taking_the_option_in_silence()
    {
        // Roadmap §10.121: ownUv0 reaches GeometryReplace alone, so a part that
        // kept its arrangement took the flag and did nothing with it. The flag
        // still cannot be honoured — which rule a part uses is written in the
        // payload, and a reshape writes none — so what changes is that it is
        // said. A tickbox that silently does nothing is the worse half.
        GeometryImportResult edit = Applied(
            Projecting(), [new(9, 9, 7), new(9, 9, 7), new(3, 4, 7)], Layout, ownUv0: true);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(0, edit.Converted);
        Assert.Equal(1, edit.LayoutUnconvertible);
        Assert.False(edit.Cameldata.Constants[0].UsesUnifiedUv0);
    }

    [Fact]
    public void A_reshape_without_the_option_says_nothing_about_a_layout_it_reprojects()
    {
        // The complement, and the reason the count above is narrow. A projecting
        // part is re-projected from its new points, so its layout follows them
        // and nothing was lost — while a 3D package writes coordinates on
        // everything, so counting them here would report a loss on every reshape
        // anyone ever runs.
        GeometryImportResult edit = Applied(
            Projecting(), [new(9, 9, 7), new(9, 9, 7), new(3, 4, 7)], Layout, ownUv0: false);

        Assert.Equal(1, edit.Reshaped);
        Assert.Equal(0, edit.LayoutIgnored);
        Assert.Equal(0, edit.LayoutUnconvertible);
    }

    private static double Scale(int index) =>
        Uv0Projection.Unified(0x7FFF_0000u, index).Value.Y;

    /// <summary>Distinct packed values, so a wrong copy is visible as itself.</summary>
    private static readonly uint[] Neighbour = [0x1111_2222u, 0x3333_4444u, 0x5555_6666u];

    private static GeometryImportResult Applied(
        Fixture pair,
        ImmutableArray<Vector3D> positions,
        ImmutableArray<Vector2D> uv0,
        bool ownUv0,
        string part = "mode3-record-0")
    {
        Result<GeometryImportResult> result = GeometryImport.Apply(
            pair.ModelFile,
            pair.Model,
            pair.Cameldata,
            [new EditedPart(part, positions, default, default, uv0)],
            ownUv0);

        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    /// <summary>The same, for the runs that are meant to refuse.</summary>
    private static Result<GeometryImportResult> Attempt(
        Fixture pair,
        ImmutableArray<Vector3D> positions,
        ImmutableArray<Vector2D> uv0,
        bool ownUv0,
        string part = "mode3-record-0") =>
        GeometryImport.Apply(
            pair.ModelFile,
            pair.Model,
            pair.Cameldata,
            [new EditedPart(part, positions, default, default, uv0)],
            ownUv0);

    /// <summary>The Z index width the fixtures use, in the bits above the flag.</summary>
    private const uint WidthFour = 3u << 3;

    /// <summary>One part that works its layout out, which is what 86% do.</summary>
    private Fixture Projecting() => Build(packedFlags: WidthFour);

    /// <summary>One that stores it, at a scale of 1.</summary>
    private Fixture Carrying() => Build(packedFlags: WidthFour | 1u | (2u << 1));

    /// <summary>
    /// Two parts: the first stores its layout, the second works one out.
    /// </summary>
    private Fixture OneOfEachRule()
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 0, 1],
            EntrySize = 2,
            Repeat = 2,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)CameldataReader.Read(Load(
            new CameldataBuilder
            {
                Mode = 3,
                ConstantCount = 2,
                PerConstantPackedFlags =
                [
                    WidthFour | 1u | (2u << 1),
                    WidthFour,
                ],
                XyBases = [0, 3],
                ZBases = [0, 1],
                // The second record carries nothing, so its base is never read
                // and the file never has to keep it sane. A stale one past the
                // end of the pool is what a real file may hold, and reading
                // through it is the fault the zero-fill exists to prevent.
                Uv0Bases = [0, 99],
                Xy = [new(1, 2), new(3, 4), new(5, 6), new(1, 1), new(2, 2), new(3, 3)],
                Z = [7f, 8f],
                Uv0 = Neighbour,
                PackedZ = [0u],
            }.Build(),
            "cameldata")).Value;

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    private Fixture Build(uint packedFlags)
    {
        byte[] bytes = new MmbFileBuilder
        {
            VertexCount = 3,
            PositionEntries = [0, 0, 1],
            EntrySize = 2,
        }.Build();

        SourceFile file = Load(bytes, "mmb");
        Mode3Cameldata cameldata = (Mode3Cameldata)CameldataReader.Read(Load(
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = packedFlags,
                Xy = [new(1, 2), new(3, 4), new(5, 6)],
                Z = [7f],
                Uv0 = (packedFlags & 1) != 0 ? [0u, 0u, 0u] : [],
                PackedZ = [0u],
            }.Build(),
            "cameldata")).Value;

        return new Fixture(file, MmbReader.Read(file).Value, cameldata, bytes);
    }

    private SourceFile Load(byte[] bytes, string extension)
    {
        string path = Path.Combine(_directory.FullName, $"asset-{Guid.NewGuid():N}.{extension}");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }

    private sealed record Fixture(
        SourceFile ModelFile, MmbModel Model, Mode3Cameldata Cameldata, byte[] ModelBytes);
}
