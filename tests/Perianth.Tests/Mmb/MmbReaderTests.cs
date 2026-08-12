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
        byte[] lead = new byte[37];
        MmbModel model = ReadOk(new MmbFileBuilder { Lead = lead });

        Assert.Equal(37, model.Parts[0].Envelope.Offset);
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
        byte[] first = new MmbFileBuilder { Label = "alpha" }.Build();
        byte[] second = new MmbFileBuilder { Label = "beta", Lead = first }.Build();

        MmbModel model = ReadOk(second);

        Assert.Equal(2, model.Parts.Length);
        Assert.Equal("alpha", model.Parts[0].Label);
        Assert.Equal("beta", model.Parts[1].Label);
        Assert.Equal([0, 1], (ImmutableArray<int>)[model.Parts[0].SourceOrdinal, model.Parts[1].SourceOrdinal]);
        Assert.True(model.Parts[0].Envelope.Offset < model.Parts[1].Envelope.Offset);
    }

    [Fact]
    public void Two_candidates_reaching_one_descriptor_keep_the_later_start()
    {
        // A real nested coincidence, not a contrived one. The outer record has a
        // four-byte label and 200 bytes of declarations; a second complete
        // envelope is planted inside those declaration bytes, positioned so that
        // its suffix and descriptor are the very same bytes as the outer
        // record's. Both offsets validate, and section 5.1 says the later start
        // is the one to keep.
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
        Assert.Equal(100, part.Envelope.Offset);
        Assert.Equal(new string('x', 104), part.Label);
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

    // Everything below is a structural mismatch rather than a broken record.
    // The reader does not claim to parse the container, so an offset that does
    // not match the envelope is simply not a record, and a file made only of
    // those refuses for having none rather than for being corrupt.

    [Fact]
    public void A_file_containing_no_envelope_refuses_for_having_no_records()
    {
        Refusal refusal = ReadRefused(new byte[512]);

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("No model-part records", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_with_an_unprintable_byte_is_not_a_record()
    {
        byte[] file = new MmbFileBuilder { Label = "part" }.Build();
        file[3] = 0x01;

        Assert.Contains("No model-part records", ReadRefused(file).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nonzero_prefix_is_not_a_record()
    {
        Assert.Contains(
            "No model-part records",
            ReadRefused(new MmbFileBuilder { ZeroPrefix = 1 }).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_wrong_suffix_byte_is_not_a_record()
    {
        Assert.Contains(
            "No model-part records",
            ReadRefused(new MmbFileBuilder { Suffix = [0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0xF1] }).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(1e6f)]
    [InlineData(-1e6f)]
    public void An_envelope_value_outside_the_accepted_range_is_not_a_record(float value)
    {
        float[] values = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, value];

        Assert.Contains(
            "No model-part records",
            ReadRefused(new MmbFileBuilder { Values = values }).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_label_is_not_a_record()
    {
        Assert.Contains(
            "No model-part records",
            ReadRefused(new MmbFileBuilder { Label = string.Empty }).Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(241)]
    [InlineData(249)]
    [InlineData(447)]
    [InlineData(1000)]
    public void A_label_longer_than_the_reference_cap_is_still_a_record(int length)
    {
        // The reference capped the label at 240 bytes and this suite asserted
        // that boundary rather than questioning it. The cap hid ten of the
        // corpus's models: every record/constant count mismatch in all 14,503
        // pairs was a part whose name ran past 240, and the longest real label
        // is 447. Nothing anywhere justified the number.
        //
        // 249 and 447 are real lengths that were being dropped. 1000 is here
        // because no bound replaced the old one: the label is limited by the
        // file, and the printable-ASCII rule is what makes a long false match
        // vanishingly unlikely.
        MmbModel model = ReadOk(new MmbFileBuilder { Label = new string('a', length) });

        Assert.Equal(length, model.Parts[0].Label.Length);
    }

    [Fact]
    public void Reading_a_null_file_is_a_fault()
    {
        Assert.Throws<ArgumentNullException>(() => MmbReader.Read(null!));
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
