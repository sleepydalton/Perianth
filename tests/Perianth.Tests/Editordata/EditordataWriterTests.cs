using System;
using System.Collections.Immutable;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Editordata;

/// <summary>
/// Checks that a file written back is the file that was read.
/// </summary>
/// <remarks>
/// Byte equality is the whole standard here. A writer that emits something
/// plausible rather than something identical is a writer nobody can check:
/// the game loads the result either way, and the difference shows up as art
/// that looks slightly wrong with no message attached. These are the synthetic
/// half; <see cref="EditordataCorpusTests"/> runs the same property over real
/// files, which is where a grammar assumption gets tested rather than restated.
/// </remarks>
public sealed class EditordataWriterTests
{
    private static byte[] RoundTrip(byte[] original)
    {
        Result<EditordataFile> read = EditordataReader.Read(SourceFile.FromMemory("f.editordata", original));
        Assert.False(read.IsRefused, read.IsRefused ? read.Refusal.Message : null);

        Result<byte[]> written = EditordataWriter.Write(read.Value);
        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : null);

        return written.Value;
    }

    private static void RoundTrips(byte[] original) => Assert.Equal(original, RoundTrip(original));

    [Fact]
    public void An_ordinary_file_is_written_back_byte_for_byte()
    {
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom([MaterialSpec.Standard()], new CustomSpec())
            .Build());
    }

    [Fact]
    public void Several_sections_and_several_records_keep_their_order()
    {
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom(
                [MaterialSpec.Standard("head", diffuse: "tex/head.dds")],
                new CustomSpec { Slot10 = (0.5f, 0.25f, 1f, 1f) },
                new CustomSpec { UvRepeat = (2f, 4f) })
            .SectionWithCustom(
                [MaterialSpec.Standard("body", "CamelDefaultShader_Trans", transparent: "tex/a.dds")],
                new CustomSpec { Slot60 = (1f, 0f, 0f, 1f) })
            .Build());
    }

    [Fact]
    public void A_file_with_no_custom_tail_gains_none()
    {
        // The tail is optional in the grammar, and inventing a default one
        // would be the most tempting improvement available here: it would make
        // every file uniform and every round-trip fail.
        RoundTrips(new EditordataBuilder { CustomVersion = null }
            .Section(MaterialSpec.Standard())
            .Build());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Each_custom_version_is_written_at_its_own_length(int version)
    {
        // Versions 1 and 2 hold fewer fields, and the reader supplies defaults
        // for the absent ones. Those defaults must not be written back, or a
        // version 1 file grows by 72 bytes per record and still parses.
        RoundTrips(new EditordataBuilder { CustomVersion = version }
            .SectionWithCustom([MaterialSpec.Standard()], new CustomSpec())
            .Build());
    }

    [Fact]
    public void A_section_authored_with_no_material_stays_empty()
    {
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom([], new CustomSpec())
            .Build());
    }

    [Fact]
    public void A_section_with_no_custom_record_keeps_its_zero_count()
    {
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom([MaterialSpec.Standard()])
            .Build());
    }

    [Fact]
    public void A_packed_bitfield_that_reads_as_nan_survives()
    {
        // slot_50's W is a UInt32 feature bitfield, and 52 distinct raw
        // patterns occur across the corpus. Some of them are NaN when read as
        // a float. Writing through an arithmetic path would quiet a signalling
        // NaN and change the bits of a field nothing reads as a number, so the
        // writer reinterprets rather than converts — this is that guard.
        //
        // The pattern goes in W alone. Its RGB is the ambient term of the
        // brightness scale and is read as a number, so the reader requires it to
        // be finite; putting a NaN there would be testing the writer against a
        // file the reader now refuses, which is a fixture claiming more than the
        // field it names.
        float payload = BitConverter.Int32BitsToSingle(unchecked((int)0x7FA00001));

        byte[] original = new EditordataBuilder()
            .SectionWithCustom(
                [MaterialSpec.Standard()],
                new CustomSpec { Slot50 = (0.25f, 0f, 0f, payload) })
            .Build();

        RoundTrips(original);
    }

    [Fact]
    public void A_path_outside_ascii_keeps_its_bytes()
    {
        // Latin-1 round-trips every byte value. A path spelled with 0xE9 is a
        // byte string that happens not to be ASCII, and decoding then encoding
        // it through UTF-8 would silently rewrite it.
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom(
                [MaterialSpec.Standard(name: "café", diffuse: "tex/café.dds")],
                new CustomSpec())
            .Build());
    }

    [Fact]
    public void An_empty_string_keeps_its_length_prefix()
    {
        RoundTrips(new EditordataBuilder()
            .SectionWithCustom([new MaterialSpec("", "", [("", "")])], new CustomSpec())
            .Build());
    }

    [Fact]
    public void A_file_with_no_sections_at_all_is_four_bytes()
    {
        byte[] original = new EditordataBuilder { CustomVersion = null }.Build();

        Assert.Equal(4, original.Length);
        RoundTrips(original);
    }

    // --- What it refuses. None of these can come from a file this reader
    // decoded; all of them can come from a record assembled in code, which is
    // what import will do.

    private static EditordataSection Section(
        int ordinal,
        ImmutableArray<EditordataCustomRecord> custom = default) =>
        new(ordinal,
            [new EditordataMaterial("mat", "CamelDefaultShader", [])],
            "intermediate",
            [.. new byte[12]],
            custom.IsDefault ? [] : custom);

    private static EditordataCustomRecord Record(int version) =>
        new(version, default, default, default, default, default, default, default);

    [Fact]
    public void A_section_whose_ordinal_disagrees_with_its_position_is_refused()
    {
        // The ordinal is not written — it is where the section sits. Writing a
        // mismatched one would silently adopt the position and discard the
        // claim, and the ordinal must equal the model-part ordinal to mean
        // anything at all.
        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [Section(0), Section(7)], CustomVersion: null));

        Assert.True(written.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, written.Refusal.Kind);
        Assert.Contains("ordinal 7", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_intermediate_record_of_the_wrong_length_is_refused()
    {
        EditordataSection section = new(0, [], "intermediate", [.. new byte[11]], []);

        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [section], CustomVersion: null));

        Assert.True(written.IsRefused);
        Assert.Contains("11 bytes", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_custom_record_from_a_different_version_is_refused()
    {
        // Versions differ in which fields exist. Writing a version 1 record
        // into a version 3 file would invent four slots; the reverse would drop
        // them. Both produce a file that loads.
        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [Section(0, [Record(1)])], CustomVersion: 3));

        Assert.True(written.IsRefused);
        Assert.Contains("version 1 custom record", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_records_with_no_declared_version_are_refused()
    {
        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [Section(0, [Record(3)])], CustomVersion: null));

        Assert.True(written.IsRefused);
        Assert.Contains("no custom-data version", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unimplemented_custom_version_is_refused()
    {
        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [Section(0)], CustomVersion: 4));

        Assert.True(written.IsRefused);
        Assert.Contains("version 4", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_character_latin1_cannot_spell_is_refused()
    {
        // Encoding.Latin1 substitutes '?' silently, which would write a valid
        // file naming a different texture. Refusing beats a working file that
        // binds the wrong path.
        EditordataSection section = new(
            0,
            [new EditordataMaterial("中", "CamelDefaultShader", [])],
            "intermediate",
            [.. new byte[12]],
            []);

        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [section], CustomVersion: null));

        Assert.True(written.IsRefused);
        Assert.Contains("U+4E2D", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_longer_than_its_length_prefix_is_refused()
    {
        EditordataSection section = new(
            0,
            [new EditordataMaterial(new string('a', ushort.MaxValue + 1), "s", [])],
            "intermediate",
            [.. new byte[12]],
            []);

        Result<byte[]> written = EditordataWriter.Write(
            new EditordataFile("f", [section], CustomVersion: null));

        Assert.True(written.IsRefused);
        Assert.Contains("65536 characters", written.Refusal.Message, StringComparison.Ordinal);
    }
}
