using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Xunit;

namespace Perianth.Tests.Mmb;

public sealed class MmbReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-mmb-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_direct_record_is_found_with_its_label_and_descriptor()
    {
        MmbModel model = ReadOk(new MmbFileBuilder { Label = "torso|spine", VertexCount = 6 });

        MmbModelPart part = Assert.Single(model.Parts);
        Assert.Equal(0, part.SourceOrdinal);
        Assert.Equal("torso|spine", part.Label);
        Assert.Equal(6u, part.Descriptor.VertexCount);
        Assert.False(part.Descriptor.IsIndexed);

        // A direct record stores no index buffer at all; emptiness here is the
        // absence of stored data, not an absence of geometry.
        Assert.Empty(part.StoredIndices);
    }

    [Fact]
    public void The_label_is_kept_as_bytes_as_well_as_text()
    {
        MmbModel model = ReadOk(new MmbFileBuilder { Label = "prop|root" });

        MmbModelPart part = model.Parts[0];
        Assert.Equal("prop|root", part.Label);
        Assert.Equal(Encoding.ASCII.GetBytes("prop|root"), part.LabelBytes.ToArray());

        // The label is not split on the pipe here. Which side binds to the
        // hierarchy depends on the cameldata mode, which this reader has not seen.
        Assert.Contains("|", part.Label, StringComparison.Ordinal);
    }

    [Fact]
    public void The_twelve_envelope_values_are_kept_although_nothing_reads_them()
    {
        float[] values = [-1.5f, 0, 0.25f, 1, 2, 3, 4, 5, 6, 7, 8, 999999f];

        MmbModel model = ReadOk(new MmbFileBuilder { Values = values });

        Assert.Equal(values, model.Parts[0].Values);
    }

    [Fact]
    public void Declaration_bytes_are_kept_verbatim_rather_than_only_counted()
    {
        byte[] declarations = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02, 0x03, 0x04];

        MmbModel model = ReadOk(new MmbFileBuilder { Declarations = declarations });

        Assert.Equal(2, model.Parts[0].DeclarationCount);
        Assert.Equal(declarations, model.Parts[0].DeclarationBytes.ToArray());
    }

    [Fact]
    public void The_envelope_byte_range_points_back_at_the_record()
    {
        // Two nodes, so the record does not begin at a fixed offset and the
        // range has to have been tracked rather than assumed.
        MmbFileBuilder builder = new() { NodeCount = 2 };
        MmbModel model = ReadOk(builder);

        Assert.Equal(builder.HeaderLength, model.Parts[0].Envelope.Offset);
        Assert.True(model.Parts[0].Envelope.Length > 0);
    }

    [Fact]
    public void An_indexed_record_subtracts_its_base_bias()
    {
        MmbModel model = ReadOk(new MmbFileBuilder
        {
            Indices = [100, 101, 102, 102, 101, 100],
            BaseBias = 100,
            VertexCount = 3,
        });

        MmbModelPart part = model.Parts[0];
        Assert.True(part.Descriptor.IsIndexed);
        Assert.Equal([0, 1, 2, 2, 1, 0], part.StoredIndices);

        // The bias is kept, so the stored values remain reconstructible.
        Assert.Equal(100u, part.Descriptor.BaseBias);
    }

    [Fact]
    public void Records_are_reported_in_byte_order_with_consecutive_ordinals()
    {
        MmbModel model = ReadOk(new MmbFileBuilder { Label = "alpha", Repeat = 2 });

        Assert.Equal(2, model.Parts.Length);
        Assert.Equal([0, 1], (ImmutableArray<int>)[model.Parts[0].SourceOrdinal, model.Parts[1].SourceOrdinal]);
        Assert.True(model.Parts[0].Envelope.Offset < model.Parts[1].Envelope.Offset);

        // Each record names its own payload, so the second is not the first
        // found twice -- which is what the scan this replaced had to guard against.
        Assert.NotEqual(model.Parts[0].Descriptor.PayloadOffset, model.Parts[1].Descriptor.PayloadOffset);
    }

    [Fact]
    public void A_record_planted_inside_the_declarations_is_not_a_second_record()
    {
        // The scan had to deduplicate candidates, because a printable run
        // inside a record's own bytes could match the envelope shape and be
        // reported as a nested record. Section 5.1 resolved that by keeping the
        // later start, a rule derived from the corpus that nobody could explain.
        //
        // A reader that walks the table cannot be fooled at all: the
        // declarations are a counted block, so their contents are never
        // examined. This plants a complete, valid-looking envelope inside them
        // and requires exactly one part to come out.
        byte[] declarations = new byte[200];
        BinaryPrimitives.WriteUInt16LittleEndian(declarations.AsSpan(42), 104);
        for (int i = 44; i < 148; i++)
        {
            declarations[i] = (byte)'x';
        }

        for (int value = 0; value < 12; value++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                declarations.AsSpan(148 + (value * 4)), BitConverter.SingleToInt32Bits(1f));
        }

        MmbModel model = ReadOk(new MmbFileBuilder { Label = "abcd", Declarations = declarations });

        MmbModelPart part = Assert.Single(model.Parts);
        Assert.Equal("abcd", part.Label);
        Assert.Equal(50, part.DeclarationCount);
    }

    [Theory]
    [InlineData(new ushort[] { 99, 100, 101 }, "below its own base bias")]
    [InlineData(new ushort[] { 100, 101, 103 }, "beyond its own vertex array")]
    [InlineData(new ushort[] { 100, 100, 101 }, "the same vertex twice")]
    [InlineData(new ushort[] { 100, 101 }, "whole number of triangles")]
    public void An_incoherent_index_buffer_refuses(ushort[] indices, string expected)
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder
        {
            Indices = indices,
            BaseBias = 100,
            VertexCount = 3,
        });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains(expected, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direct_record_with_a_nonzero_bias_refuses()
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder { BaseBias = 7, VertexCount = 3 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("nonzero index bias", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direct_record_whose_vertices_are_not_whole_triangles_refuses()
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder { VertexCount = 4 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("whole number of triangles", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_declaring_no_vertices_refuses()
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder { VertexCount = 0 });

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("no vertices", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payload_reaching_outside_the_file_refuses()
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder
        {
            Adjust = descriptor => descriptor[8] = 0xFFFF_0000,
        });

        Assert.Contains("does not lie inside the file", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_buffer_reaching_outside_its_payload_refuses()
    {
        Refusal refusal = ReadRefused(new MmbFileBuilder
        {
            Indices = [0, 1, 2],
            VertexCount = 3,
            Adjust = descriptor => descriptor[6] = 4,
        });

        Assert.Contains("does not lie inside its payload", refusal.Message, StringComparison.Ordinal);
    }

    // Everything below was a heuristic of the signature scan this reader
    // replaced -- printable labels, a zero prefix, an exact suffix, a bounded
    // float range. None of them is a rule of the format: they were the shape of
    // the commonest record, used to find records in a container nobody had
    // derived. Roadmap §10.53 derived it, so the questions are different now,
    // and each test below states the new one rather than being deleted quietly.

    [Fact]
    public void A_file_that_does_not_begin_with_the_magic_refuses_as_malformed()
    {
        Refusal refusal = ReadRefused(new byte[512]);

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("does not begin with", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MUCM")]
    [InlineData("MCMP")]
    public void The_other_two_containers_refuse_for_what_they_are(string magic)
    {
        // Both ship. A scan never had to know which container it was inside, so
        // neither had been noticed; a reader that walks the file must say so
        // rather than failing to find anything.
        Refusal refusal = ReadRefused(new MmbFileBuilder
        {
            Magic = Encoding.ASCII.GetBytes(magic),
        });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains(magic, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unprintable_label_is_read_rather_than_rejected()
    {
        // The scan required every label byte to be printable ASCII, because a
        // printable run was how it recognised a record at all. Nothing in the
        // format says so, and a reader that walks the table has already been
        // told where the name is and how long it is.
        MmbModel model = ReadOk(new MmbFileBuilder { Label = "a\u0001b" });

        Assert.Equal(3, model.Parts[0].LabelBytes.Length);
    }

    [Fact]
    public void The_bytes_the_scan_called_a_zero_prefix_are_read_and_ignored()
    {
        // They are two version-gated flag bytes. The scan needed them zero;
        // the loader reads them and this reader skips them, so a nonzero value
        // must change nothing at all.
        MmbModel plain = ReadOk(new MmbFileBuilder());
        MmbModel flagged = ReadOk(new MmbFileBuilder { ZeroPrefix = 0x0101 });

        Assert.Equal(plain.Parts[0].Label, flagged.Parts[0].Label);
        Assert.Equal(plain.Parts[0].Descriptor, flagged.Parts[0].Descriptor);
    }

    [Fact]
    public void More_than_one_level_of_detail_refuses_rather_than_taking_the_first()
    {
        // The scan's "exact seven-byte suffix" was a zero matrix count, a LOD
        // count of one, and a flags word. Every one of 441,865 measured parts
        // declares one level of detail, so a second is a shape this build has
        // never seen -- and silently keeping the first would export a model at
        // whichever detail happened to come first.
        Refusal refusal = ReadRefused(new MmbFileBuilder
        {
            Suffix = [0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0xF0],
        });

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("levels of detail", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_part_carrying_matrices_is_read_and_its_bytes_kept()
    {
        // This is the shape the scan could not match, and it did not lose the
        // part quietly: it reported the whole file as holding no records. One
        // real file does this, a debug object.
        byte[] matrix = new byte[66];
        matrix[0] = 0x3F;
        MmbFileBuilder builder = new()
        {
            Suffix = [0x01, 0x00, .. matrix, 0x01, 0x00, 0x00, 0x00, 0xF0],
        };

        MmbModel model = ReadOk(builder);

        MmbModelPart part = Assert.Single(model.Parts);
        Assert.Equal(1, part.MatrixCount);
        Assert.Equal(matrix, part.MatrixBytes.ToArray());
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1e6f)]
    [InlineData(-1e6f)]
    public void A_transform_value_outside_the_scan_range_is_read_rather_than_rejected(float value)
    {
        // The scan rejected these to keep a false match unlikely. They are just
        // the part's transform, and the format places no range on it.
        float[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, value];

        MmbModel model = ReadOk(new MmbFileBuilder { Values = values });

        Assert.Equal(values, model.Parts[0].Values);
    }

    private MmbModel ReadOk(MmbFileBuilder builder) => ReadOk(builder.Build());

    private MmbModel ReadOk(byte[] bytes)
    {
        Result<MmbModel> result = MmbReader.Read(Load(bytes));
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal ReadRefused(MmbFileBuilder builder) => ReadRefused(builder.Build());

    private Refusal ReadRefused(byte[] bytes)
    {
        Result<MmbModel> result = MmbReader.Read(Load(bytes));
        Assert.True(result.IsRefused);
        return result.Refusal;
    }

    private SourceFile Load(byte[] bytes)
    {
        string path = Path.Combine(_directory.FullName, $"model-{Guid.NewGuid():N}.mmb");
        File.WriteAllBytes(path, bytes);
        return SourceFileReader.Read(path).Value;
    }
}
