using System;
using System.IO;
using System.Numerics;
using Perianth.Core.Geometry;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Geometry;

public sealed class GeometryAssemblerTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-geom-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_mode_two_part_resolves_its_vertices_through_the_absolute_pool()
    {
        GeometryModel model = Assemble(
            new MmbFileBuilder
            {
                Label = "mesh|node",
                VertexCount = 3,
                PositionEntries = [2, 0, 1],
                EntrySize = 4,
            },
            new CameldataBuilder
            {
                Mode = 2,
                Positions = [new(10, 0, 0), new(0, 10, 0), new(0, 0, 10)],
            });

        GeometryPart part = Assert.Single(model.Parts);
        Assert.Equal(2, model.Mode);
        Assert.Equal("mode2-record-0", part.Name);

        // The stored identifier indexes the pool absolutely, in the order the
        // stream gave them, not the order the pool is in.
        Assert.Equal(new Vector3D(0, 0, 10), part.Positions[0]);
        Assert.Equal(new Vector3D(10, 0, 0), part.Positions[1]);
        Assert.Equal(new Vector3D(0, 10, 0), part.Positions[2]);
        Assert.Equal([0, 1, 2], part.Indices);
    }

    [Fact]
    public void A_mode_three_part_resolves_XY_and_Z_through_its_bases()
    {
        GeometryModel model = Assemble(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 2,
            },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = 0,
                Xy = [new(1, 2), new(3, 4), new(5, 6)],
                Z = [7f, 8f, 9f],
                PackedZ = [0u],
            });

        GeometryPart part = Assert.Single(model.Parts);
        Assert.Equal("mode3-record-0", part.Name);
        Assert.Equal(new Vector3D(1, 2, 7), part.Positions[0]);
        Assert.Equal(new Vector3D(3, 4, 7), part.Positions[1]);
        Assert.Equal(new Vector3D(5, 6, 7), part.Positions[2]);
    }

    [Fact]
    public void A_mode_three_part_reads_its_Z_index_out_of_the_packed_bit_stream()
    {
        // Four bits per index, so the first three local identifiers read nibbles
        // 0, 1 and 2 of the packed word: 2, 1 and 0.
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = (4 - 1) << 3,
                Xy = [new(0, 0), new(0, 0), new(0, 0)],
                Z = [10f, 20f, 30f],
                PackedZ = [0x0012u],
            });

        // Nibble 0 is 2, nibble 1 is 1, nibble 2 is 0, and each is a Z index.
        GeometryPart part = model.Parts[0];
        Assert.Equal(30, part.Positions[0].Z);
        Assert.Equal(20, part.Positions[1].Z);
        Assert.Equal(10, part.Positions[2].Z);
    }

    [Fact]
    public void Mode_three_refuses_when_the_record_and_constant_counts_disagree()
    {
        // No second association rule is known, so attaching surfaces by guess is
        // exactly what must not happen.
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
            new CameldataBuilder { Mode = 3, ConstantCount = 2 });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("one to one", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_two_exports_without_UV0_when_the_counts_disagree()
    {
        // The same mismatch that refuses in mode 3 only costs UV0 here.
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 },
            new CameldataBuilder
            {
                Mode = 2,
                ConstantCount = 2,
                Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            });

        Assert.True(model.SurfaceUv0Unavailable);
        Assert.False(model.Parts[0].HasUv0);
        Assert.Equal(3, model.Parts[0].Positions.Length);
    }

    [Fact]
    public void Mode_two_projects_surface_UV0_when_the_counts_agree()
    {
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 },
            new CameldataBuilder
            {
                Mode = 2,
                Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)],
            });

        Assert.False(model.SurfaceUv0Unavailable);
        Assert.True(model.Parts[0].HasUv0);
        Assert.Equal(3, model.Parts[0].Uv0.Length);
    }

    [Fact]
    public void Surface_UV0_follows_the_projection_term_for_term()
    {
        // Hand-computed from section 5.4 against the builder's fixed constant.
        // Columns 0 and 1 of the inverse-local matrix are (0,4,8,12) and
        // (1,5,9,13); the origin is (1,1,...), U is (4,5,6,7), V is (8,9,10,11);
        // the position-X scale is 2 and the inverse unit scale is 0.5.
        //
        // For (1,0,0):  px = (12) * 0.5 * 2 = 12,  py = (14) * 0.5 = 7
        //               u  = ((12-1)*4 + (7-1)*5) * 7            = 518
        //               v  = 1 - (((12-1)*8 + (7-1)*9) * 11)     = -1561
        //
        // Only the U axis carries the position-X scale, and only V is flipped.
        // Every one of those is a separate way to be wrong.
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 },
            new CameldataBuilder
            {
                Mode = 2,
                Positions = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)],
            });

        GeometryPart part = model.Parts[0];
        Assert.Equal(518, part.Uv0[0].X, 1e-9);
        Assert.Equal(-1561, part.Uv0[0].Y, 1e-9);
        Assert.Equal(700, part.Uv0[1].X, 1e-9);
        Assert.Equal(-2111, part.Uv0[1].Y, 1e-9);
        Assert.Equal(882, part.Uv0[2].X, 1e-9);
        Assert.Equal(-2661, part.Uv0[2].Y, 1e-9);
    }

    [Fact]
    public void A_mode_three_part_reads_unified_UV0_by_position_identifier()
    {
        // The shader reads uv0Offset + Gfx_PosId, so entry order follows the
        // stored identifiers rather than the draw order.
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [2, 0, 1], EntrySize = 2 },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = 1 | (2u << 1),
                Xy = [new(0, 0), new(0, 0), new(0, 0)],
                Z = [0f],
                Uv0 = [0x0000_0000, 0x0000_4000, 0x0000_7FFF],
                PackedZ = [0u],
            });

        GeometryPart part = model.Parts[0];
        Assert.Equal(1.0, part.Uv0[0].X, 1e-12);
        Assert.Equal(0.0, part.Uv0[1].X, 1e-12);
        Assert.Equal(0.500015259254738, part.Uv0[2].X, 1e-12);
    }

    [Theory]
    [InlineData(4, "nonzero stream-0 offset")]
    [InlineData(9, "reserved descriptor word")]
    public void Mode_three_requires_two_descriptor_words_to_be_zero(int word, string expected)
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 2,
                Adjust = descriptor => descriptor[word] = 1,
            },
            new CameldataBuilder { Mode = 3 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_three_with_one_declaration_requires_its_check_field_to_match()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 2,
                Declarations = [0, 0, 0, 0],
                Adjust = descriptor => descriptor[3] = 99,
            },
            new CameldataBuilder { Mode = 3 });

        Assert.Contains("check field", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Mode_three_requires_the_payload_to_end_exactly_after_the_indices()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 2,
                Adjust = descriptor => descriptor[6] += 4,
            },
            new CameldataBuilder { Mode = 3 });

        Assert.Contains("end exactly after", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_two_identifier_whose_high_half_is_set_refuses()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 0x0001_0000, 1], EntrySize = 4 },
            new CameldataBuilder { Mode = 2, Positions = [new(0, 0, 0), new(1, 0, 0)] });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("high half", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_two_identifier_beyond_the_pool_refuses()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 9], EntrySize = 4 },
            new CameldataBuilder { Mode = 2, Positions = [new(0, 0, 0), new(1, 0, 0)] });

        Assert.Contains("beyond the cameldata pool", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_three_identifier_beyond_the_XY_array_refuses()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 99], EntrySize = 2 },
            new CameldataBuilder { Mode = 3, Xy = [new(0, 0), new(1, 1)] });

        Assert.Contains("XY entry outside", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_resolved_Z_index_beyond_the_Z_array_refuses()
    {
        // One bit per index, and the stream's first bit is set, so the resolved
        // Z index is one past a single-entry array.
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = 0,
                Xy = [new(0, 0), new(1, 1), new(2, 2)],
                Z = [0f],
                PackedZ = [0xFFFF_FFFF],
            });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("Z entry outside", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unified_UV0_index_beyond_the_UV0_array_refuses()
    {
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = 1,
                Xy = [new(0, 0), new(1, 1), new(2, 2)],
                Z = [0f, 1f],
                Uv0 = [0u],
                PackedZ = [0u],
            });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("UV0 entry outside", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_packed_Z_read_running_off_the_end_of_the_bit_stream_refuses()
    {
        // Thirty-two bits per index against a single word: the second vertex
        // asks for bits the stream does not have.
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
            new CameldataBuilder
            {
                Mode = 3,
                PackedFlags = 31u << 3,
                Xy = [new(0, 0), new(1, 1), new(2, 2)],
                Z = [0f],
                PackedZ = [0u],
            });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("outside the cameldata bit stream", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexplained_mode_two_gap_is_unsupported_rather_than_malformed()
    {
        // Bytes between the positions and the indices that no auxiliary stream
        // accounts for. The file is coherent; the layout is simply not read.
        Refusal refusal = AssembleRefused(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 4,
                Indices = [0, 1, 2],
                GapBytes = 8,
            },
            new CameldataBuilder { Mode = 2, Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)] });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("unexplained gap", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_mode_two_gap_an_auxiliary_stream_accounts_for_is_skipped()
    {
        GeometryModel model = Assemble(
            new MmbFileBuilder
            {
                VertexCount = 3,
                PositionEntries = [0, 1, 2],
                EntrySize = 4,
                Indices = [0, 1, 2],
                GapBytes = 8,

                // The auxiliary stream sits exactly where the gap starts, which
                // is what makes those bytes accounted for rather than a mystery.
                Adjust = descriptor => descriptor[5] = 12,
            },
            new CameldataBuilder { Mode = 2, Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)] });

        Assert.Equal(3, model.Parts[0].Positions.Length);
    }

    [Theory]
    [InlineData(2, "left|right", "right")]
    [InlineData(3, "left|right", "left")]
    [InlineData(2, "plain", "plain")]
    [InlineData(3, "plain", "plain")]
    public void The_hierarchy_binding_name_takes_the_side_its_mode_uses(int mode, string label, string expected)
    {
        GeometryModel model = mode == 2
            ? Assemble(
                new MmbFileBuilder { Label = label, VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 },
                new CameldataBuilder { Mode = 2, Positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)] })
            : Assemble(
                new MmbFileBuilder { Label = label, VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 2 },
                new CameldataBuilder { Mode = 3, Xy = [new(0, 0), new(1, 0), new(0, 1)], Z = [0f] });

        Assert.Equal(expected, model.Parts[0].HierarchyBindingName);
        Assert.Equal(label, model.Parts[0].SourceLabel);
    }

    [Fact]
    public void Geometry_arithmetic_keeps_more_precision_than_a_binary32_pipeline_would()
    {
        // Section 7.4 puts every geometry and UV calculation in binary64 and
        // narrows only where a GLB payload is packed. A position that survives
        // the pool unchanged still arrives as a double here, and the normal
        // computed from it carries digits a float could not hold.
        GeometryModel model = Assemble(
            new MmbFileBuilder { VertexCount = 3, PositionEntries = [0, 1, 2], EntrySize = 4 },
            new CameldataBuilder
            {
                Mode = 2,
                Positions = [new(0, 0, 0), new(1, 0, 0), new(0.1f, 0.7f, 0)],
            });

        Vector3D normal = model.Parts[0].Normals[0];
        Assert.Equal(1.0, normal.Length, 1e-15);
        Assert.IsType<double>(normal.Z);
    }

    [Fact]
    public void Assembling_without_a_model_or_a_cameldata_is_a_fault()
    {
        Assert.Throws<ArgumentNullException>(() => GeometryAssembler.Assemble(null!, null!));
    }

    private GeometryModel Assemble(MmbFileBuilder mmb, CameldataBuilder cameldata)
    {
        Result<GeometryModel> result = GeometryAssembler.Assemble(ReadMmb(mmb), ReadCameldata(cameldata));
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal AssembleRefused(MmbFileBuilder mmb, CameldataBuilder cameldata)
    {
        Result<GeometryModel> result = GeometryAssembler.Assemble(ReadMmb(mmb), ReadCameldata(cameldata));
        Assert.True(result.IsRefused);
        return result.Refusal;
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
