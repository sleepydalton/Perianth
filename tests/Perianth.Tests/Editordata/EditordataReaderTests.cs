using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Editordata;

public sealed class EditordataReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-editor-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_section_yields_its_material_its_channels_and_its_custom_record()
    {
        EditordataFile file = ReadOk(new EditordataBuilder()
            .SectionWithCustom(
                [MaterialSpec.Standard(name: "lambert1", diffuse: "tex/a.dds")],
                new CustomSpec { UvRepeat = (2f, 3f) }));

        EditordataSection section = Assert.Single(file.Sections);
        Assert.Equal(0, section.Ordinal);
        Assert.Equal(3, file.CustomVersion);

        EditordataMaterial material = Assert.Single(section.Materials);
        Assert.Equal("lambert1", material.Name);
        Assert.Equal("CamelDefaultShader", material.Shader);

        // All five channels are kept, including the two nothing reads.
        Assert.Equal(
            ["DiffuseColor", "NormalMap", "SpecularColor", "TransparentColor", "EmissiveColor"],
            material.Channels.Select(c => c.Channel));
        Assert.Equal("tex/a.dds", material.Channels[0].TexturePath);

        EditordataCustomRecord custom = Assert.Single(section.CustomRecords);
        Assert.Equal(new Float2(2f, 3f), custom.UvRepeat);
    }

    [Fact]
    public void Every_material_record_is_kept_not_only_the_selected_one()
    {
        // Only record 0 is selected downstream, but the later records are what
        // keep the cursor correct, and a writer will need them. In the surveyed
        // corpus every section carries exactly one, so this path is structural.
        EditordataFile file = ReadOk(new EditordataBuilder().Section(
            MaterialSpec.Standard(name: "first"),
            MaterialSpec.Standard(name: "second"),
            MaterialSpec.Standard(name: "third")));

        Assert.Equal(["first", "second", "third"], file.Sections[0].Materials.Select(m => m.Name));
    }

    [Fact]
    public void A_section_authored_with_no_material_is_empty_rather_than_a_refusal()
    {
        // A real authored state, distinct from a truncation.
        EditordataFile file = ReadOk(new EditordataBuilder()
            .Section()
            .Section(MaterialSpec.Standard()));

        Assert.Empty(file.Sections[0].Materials);
        Assert.Single(file.Sections[1].Materials);
        Assert.Equal([0, 1], file.Sections.Select(s => s.Ordinal));
    }

    [Fact]
    public void The_shader_name_is_kept_verbatim_rather_than_judged_here()
    {
        // Deciding which families are supported is reconstruction, not grammar.
        // This reader must not refuse a shader it does not recognise, or the
        // layer above cannot report which family a section actually used.
        EditordataFile file = ReadOk(new EditordataBuilder().Section(
            MaterialSpec.Standard(shader: "SomeOtherShader")));

        Assert.Equal("SomeOtherShader", file.Sections[0].Materials[0].Shader);
    }

    [Theory]
    [InlineData(1, 32)]
    [InlineData(2, 40)]
    [InlineData(3, 104)]
    public void Each_custom_version_consumes_its_own_record_size(int version, int recordBytes)
    {
        EditordataBuilder builder = new() { CustomVersion = version };
        builder.SectionWithCustom([MaterialSpec.Standard()], new CustomSpec());

        byte[] bytes = builder.Build();
        EditordataFile file = ReadOk(builder);

        Assert.Equal(version, file.CustomVersion);
        Assert.Single(file.Sections[0].CustomRecords);

        // The whole file is consumed, so the record really was that wide: a
        // reader using the wrong size leaves trailing bytes or overruns.
        EditordataBuilder second = new() { CustomVersion = version };
        second.SectionWithCustom([MaterialSpec.Standard()], new CustomSpec(), new CustomSpec());
        Assert.Equal(bytes.Length + recordBytes, second.Build().Length);
        ReadOk(second);
    }

    [Fact]
    public void Version_one_and_two_leave_the_later_slots_at_their_defaults()
    {
        // Only version 3 carries the repeat, gain, offset and emissive slots.
        // Neither 1 nor 2 occurs anywhere in the surveyed corpus; they are
        // implemented because the specification names them.
        EditordataBuilder v1 = new() { CustomVersion = 1 };
        v1.SectionWithCustom([MaterialSpec.Standard()], new CustomSpec { UvRepeat = (9f, 9f) });

        EditordataCustomRecord record = ReadOk(v1).Sections[0].CustomRecords[0];

        Assert.Equal(new Float2(1f, 1f), record.UvRepeat);
        Assert.Equal(new Float4(1f, 1f, 1f, 1f), record.Slot30);
        Assert.Equal(default, record.Slot40);
    }

    [Fact]
    public void A_file_with_no_custom_tail_reports_no_version()
    {
        EditordataBuilder builder = new() { CustomVersion = null };
        builder.Section(MaterialSpec.Standard());

        EditordataFile file = ReadOk(builder);

        Assert.Null(file.CustomVersion);
        Assert.Empty(file.Sections[0].CustomRecords);
    }

    [Fact]
    public void An_unsupported_custom_version_refuses()
    {
        EditordataBuilder builder = new() { CustomVersion = 4 };
        builder.Section(MaterialSpec.Standard());

        Refusal refusal = ReadRefused(builder);
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("version 4", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unconsumed_trailing_bytes_refuse()
    {
        // The condition a shifted cursor produces. Exporting from a reading
        // that does not account for every byte is how a wrong answer looks
        // right.
        EditordataBuilder builder = new() { Trailing = [1, 2, 3, 4] };
        builder.Section(MaterialSpec.Standard());

        Refusal refusal = ReadRefused(builder);
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("4 unconsumed trailing bytes", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_truncated_file_refuses_and_names_the_section()
    {
        EditordataBuilder builder = new();
        builder.Section(MaterialSpec.Standard()).Section(MaterialSpec.Standard());
        byte[] bytes = builder.Build();

        Refusal refusal = ReadRefused(bytes[..(bytes.Length / 2)]);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("editordata", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_finite_value_that_reaches_an_export_refuses()
    {
        EditordataBuilder builder = new();
        builder.SectionWithCustom(
            [MaterialSpec.Standard()],
            new CustomSpec { Slot30 = (1f, float.NaN, 1f, 1f) });

        Refusal refusal = ReadRefused(builder);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("non-finite", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_finite_slot_fifty_is_not_a_refusal()
    {
        // slot_50.w is a packed bitfield of raw UInt32 patterns, not a float.
        // Testing it for finiteness would refuse well-formed files over a value
        // nothing ever reads as a number.
        EditordataBuilder builder = new();
        builder.SectionWithCustom(
            [MaterialSpec.Standard()],
            new CustomSpec { Slot50 = (0f, 0f, 0f, BitConverter.UInt32BitsToSingle(0x7FC0_0003)) });

        EditordataFile file = ReadOk(builder);
        Assert.Single(file.Sections[0].CustomRecords);
    }

    [Fact]
    public void Texture_paths_round_trip_every_byte()
    {
        // Latin-1 maps all 256 byte values, so a path spelled outside ASCII
        // survives instead of becoming a replacement character.
        string path = "tex/éÿ.dds";
        EditordataFile file = ReadOk(new EditordataBuilder().Section(
            MaterialSpec.Standard(diffuse: path)));

        Assert.Equal(path, file.Sections[0].Materials[0].Channels[0].TexturePath);
    }

    [Fact]
    public void The_intermediate_record_is_consumed_and_kept()
    {
        EditordataFile file = ReadOk(new EditordataBuilder().Section(MaterialSpec.Standard()));

        Assert.Equal("intermediate", file.Sections[0].IntermediateName);
        Assert.Equal(12, file.Sections[0].IntermediateData.Length);
    }

    private EditordataFile ReadOk(EditordataBuilder builder)
    {
        Result<EditordataFile> result = EditordataReader.Read(Write(builder.Build()));
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal ReadRefused(EditordataBuilder builder) => ReadRefused(builder.Build());

    private Refusal ReadRefused(byte[] bytes)
    {
        Result<EditordataFile> result = EditordataReader.Read(Write(bytes));
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }

    private SourceFile Write(byte[] bytes)
    {
        string path = Path.Combine(_directory.FullName, $"m{Guid.NewGuid():N}.editordata");
        File.WriteAllBytes(path, bytes);
        Result<SourceFile> file = SourceFileReader.Read(path);
        Assert.True(file.IsSuccess, "the fixture could not be read back");
        return file.Value;
    }
}
