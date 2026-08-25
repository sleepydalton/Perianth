using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Perianth.Formats.Binary;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Bvm;

/// <summary>
/// The BVM graph reader and writer, over invented containers.
/// </summary>
/// <remarks>
/// The corpus oracle proves agreement on 15,399 real files and cannot reach two
/// things: the forms no shipped file uses, and the refusals — a corpus of valid
/// files never exercises what happens to an invalid one. Both are here, and the
/// wide compact-integer form is the reason this file exists at all: it is the
/// same latent hazard as the shader's Z-mask table, right on every input anyone
/// has and wrong on the first input nobody had.
/// </remarks>
public sealed class BvmGraphTests
{
    private static SourceFile File(List<byte> bytes) =>
        SourceFile.FromMemory("made-up.mgraphobject", bytes.ToArray());

    /// <summary>Builds a container: magic, string table, then a graph.</summary>
    private static List<byte> Container(string[] strings, params byte[] graph)
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        CompactInteger.Write(bytes, (uint)strings.Length);
        foreach (string text in strings)
        {
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(text);
            CompactInteger.Write(bytes, (uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.AddRange(graph);
        return bytes;
    }

    /// <summary>An empty container value — no array entries and no map entries.</summary>
    private static readonly byte[] EmptyGraph = [0x01, 0x00, 0x00];

    [Fact]
    public void A_container_holds_its_array_and_its_map_in_file_order()
    {
        // One array entry (true), then two map pairs keyed by strings 0 and 1.
        BvmDocument document = Read(Container(
            ["alpha", "beta"],
            0x01, 0x01, 0x02,
            0x02,
            0x0d, 0x00, 0x03,
            0x0d, 0x01, 0x04, 0x07));

        BvmContainer root = Assert.IsType<BvmContainer>(document.Graph);
        Assert.Equal(BvmMarker.True, Assert.IsType<BvmMarker>(Assert.Single(root.Items)).Tag);
        Assert.Equal(2, root.Entries.Length);

        Assert.Equal(0, Assert.IsType<BvmString>(root.Entries[0].Key).Index);
        Assert.Equal(BvmMarker.False, Assert.IsType<BvmMarker>(root.Entries[0].Value).Tag);
        Assert.Equal(1, Assert.IsType<BvmString>(root.Entries[1].Key).Index);
        Assert.Equal(7, Assert.IsType<BvmNumbers>(root.Entries[1].Value).Values[0]);
    }

    [Fact]
    public void The_two_string_tags_are_kept_apart()
    {
        // Decoded identically and written apart. Merging them reads every file
        // correctly and writes none of them back, which is the fault this whole
        // value shape exists to prevent.
        List<byte> bytes = Container(["alpha"], 0x01, 0x02, 0x00, 0x0d, 0x00, 0x0e, 0x00);
        BvmDocument document = Read(bytes);

        BvmContainer root = Assert.IsType<BvmContainer>(document.Graph);
        Assert.Equal(BvmString.StringA, root.Items[0].Tag);
        Assert.Equal(BvmString.StringB, root.Items[1].Tag);

        Assert.Equal(bytes, BvmWriter.Write(document).Value);
    }

    [Theory]
    [InlineData(0x05, 8)]
    [InlineData(0x0b, 8)]
    [InlineData(0x11, 8)]
    [InlineData(0x07, 16)]
    [InlineData(0x0f, 16)]
    [InlineData(0x10, 16)]
    public void Tags_of_equal_width_survive_their_own_tag(byte tag, int width)
    {
        // Three tags carry eight bytes and three carry sixteen. A reader that
        // decoded by width rather than by tag would round-trip the payload and
        // lose which value the engine builds from it.
        List<byte> graph = [0x01, 0x01, 0x00, tag];
        for (int i = 0; i < width; i++)
        {
            graph.Add((byte)(0xA0 + i));
        }

        List<byte> bytes = Container([], [.. graph]);
        BvmDocument document = Read(bytes);

        BvmRaw raw = Assert.IsType<BvmRaw>(Assert.IsType<BvmContainer>(document.Graph).Items[0]);
        Assert.Equal(tag, raw.Tag);
        Assert.Equal(width, raw.Bytes.Length);
        Assert.Equal(bytes, BvmWriter.Write(document).Value);
    }

    [Fact]
    public void A_payload_that_is_not_a_number_keeps_its_bit_pattern()
    {
        // Tag 0x06 is four bytes and is a float everywhere it is used. Decoding
        // it as one would collapse the many bit patterns of NaN onto whichever
        // the runtime prefers, and the file would come back different.
        byte[] signalling = [0x01, 0x00, 0xC0, 0x7F];
        List<byte> bytes = Container([], [0x01, 0x01, 0x00, 0x06, .. signalling]);

        Assert.Equal(bytes, BvmWriter.Write(Read(bytes)).Value);
    }

    [Fact]
    public void A_negative_number_round_trips_through_the_signed_encoding()
    {
        // Sign-extended from bit 6, 14 or 30 — not zero-extended. Reading the
        // signed form as the unsigned one is right for every non-negative value,
        // so only a negative can tell them apart.
        foreach (int value in new[] { -1, -32, -33, -8192, -8193, -536870912, int.MinValue, 0, 31, 32, int.MaxValue })
        {
            List<byte> encoded = [];
            CompactInteger.WriteSigned(encoded, value);

            SpanReader reader = new([.. encoded]);
            Assert.True(CompactInteger.TryReadSigned(ref reader, out int read));
            Assert.Equal(value, read);
            Assert.Equal(0, reader.Remaining);
        }
    }

    [Fact]
    public void The_widest_unsigned_form_replaces_the_value_rather_than_extending_it()
    {
        // No shipped file uses it, so the corpus oracle cannot see this. The
        // first byte's six bits are discarded and a raw uint32 follows.
        SpanReader reader = new([0xFF, 0x01, 0x00, 0x00, 0x40]);

        Assert.True(CompactInteger.TryRead(ref reader, out ulong value));
        Assert.Equal(0x40000001u, value);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Every_unsigned_width_round_trips_at_its_boundary()
    {
        foreach (uint value in new uint[]
                 { 0, 63, 64, 16383, 16384, 1073741823, 1073741824, uint.MaxValue })
        {
            List<byte> encoded = [];
            CompactInteger.Write(encoded, value);

            SpanReader reader = new([.. encoded]);
            Assert.True(CompactInteger.TryRead(ref reader, out ulong read));
            Assert.Equal(value, read);
            Assert.Equal(0, reader.Remaining);
        }
    }

    [Fact]
    public void A_graph_that_ends_before_the_file_does_is_refused()
    {
        // The graph is the rest of the file. A decoder stopping early produces a
        // plausible tree over half a file, and only counting bytes tells that
        // from a correct read.
        List<byte> bytes = Container([], EmptyGraph);
        bytes.Add(0x02);

        Result<BvmDocument> result = BvmReader.ReadDocument(File(bytes));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Contains("left in the file", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_tag_is_refused_rather_than_skipped()
    {
        Result<BvmDocument> result = BvmReader.ReadDocument(File(Container([], 0x01, 0x01, 0x00, 0x7F)));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Contains("0x7f", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_reference_past_the_table_is_refused()
    {
        // The shape a desynchronised read takes, so catching it names the byte
        // where the two disagreed rather than producing a plausible tree.
        Result<BvmDocument> result = BvmReader.ReadDocument(
            File(Container(["alpha"], 0x01, 0x01, 0x00, 0x0d, 0x09)));

        Assert.False(result.IsSuccess);
        Assert.Contains("string 9 of 1", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_declaring_more_entries_than_the_file_holds_is_refused()
    {
        // Refused before allocating, so a malformed header cannot ask for
        // gigabytes.
        Result<BvmDocument> result = BvmReader.ReadDocument(
            File(Container([], 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 0x00)));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_truncated_payload_is_refused_rather_than_padded()
    {
        Result<BvmDocument> result = BvmReader.ReadDocument(
            File(Container([], 0x01, 0x01, 0x00, 0x07, 0x01, 0x02, 0x03)));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_value_whose_payload_does_not_match_its_tag_refuses_to_be_written()
    {
        // Not reachable from this project's reader, which is the point: the
        // writer checks rather than trusting, because a count that disagrees with
        // its tag produces a file whose reader stops in the wrong place and
        // nothing downstream could recover.
        BvmDocument document = new(
            ["alpha"],
            new BvmContainer([new BvmNumbers(0x08, [1, 2])], []));

        Result<byte[]> result = BvmWriter.Write(document);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
        Assert.Contains("4 integers", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_string_reference_past_the_table_refuses_to_be_written()
    {
        Result<byte[]> result = BvmWriter.Write(new BvmDocument(
            ["alpha"], new BvmContainer([new BvmString(BvmString.StringA, 4)], [])));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void The_string_table_is_written_back_as_it_was_read()
    {
        // Duplicates and unreferenced entries included. Dropping an entry
        // nothing points at renumbers every reference after it, and compacting a
        // table is not what "write this file back" means.
        List<byte> bytes = Container(["same", "same", "unused"], 0x01, 0x01, 0x00, 0x0d, 0x00);
        BvmDocument document = Read(bytes);

        Assert.Equal(3, document.Strings.Length);
        Assert.Equal(bytes, BvmWriter.Write(document).Value);
    }

    [Fact]
    public void An_empty_string_survives_the_round_trip()
    {
        List<byte> bytes = Container(["", "after"], EmptyGraph);

        Assert.Equal(bytes, BvmWriter.Write(Read(bytes)).Value);
    }

    [Fact]
    public void A_path_outside_ascii_is_counted_in_bytes()
    {
        // The length precedes the bytes, so counting characters would declare a
        // length shorter than it writes and desynchronise everything after it.
        List<byte> bytes = Container(["café/naïve.mmb"], EmptyGraph);
        BvmDocument document = Read(bytes);

        Assert.Equal("café/naïve.mmb", document.Strings[0]);
        Assert.Equal(bytes, BvmWriter.Write(document).Value);
    }

    [Fact]
    public void Nesting_past_the_limit_is_refused_rather_than_overflowing_the_stack()
    {
        List<byte> graph = [];
        for (int i = 0; i < 200; i++)
        {
            graph.AddRange([0x01, 0x01, 0x00]);
        }

        graph.Add(0x02);

        Result<BvmDocument> result = BvmReader.ReadDocument(File(Container([], [.. graph])));

        Assert.False(result.IsSuccess);
        Assert.Contains("nests deeper", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_only_the_table_still_succeeds_where_the_graph_would_refuse()
    {
        // The two readings are separate questions, not two paths to one answer.
        // "Which assets does this mention" is all the exporter ever needed and
        // must not start refusing on a graph it never looked at.
        SourceFile file = File(Container(["made/up/asset.mmb"], 0x01, 0x01, 0x00, 0x7F));

        Assert.True(BvmReader.Read(file).IsSuccess);
        Assert.False(BvmReader.ReadDocument(file).IsSuccess);
    }

    private static BvmDocument Read(List<byte> bytes) => BvmReader.ReadDocument(File(bytes)).Value;
}
