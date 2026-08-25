using System;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Layer;
using Xunit;

namespace Perianth.Tests.Layer;

/// <summary>
/// The span index over a map layer.
/// </summary>
/// <remarks>
/// The claim is narrow and load-bearing: an insertion changes the chunk it names
/// and the numbers that describe the body, and nothing else. So most of these
/// assert what is <em>preserved</em>. The fixtures are invented, as the
/// repository requires; the shapes are the measured ones — a keyed entry, a
/// bare one, and a declaration order that is not the body order.
/// </remarks>
public sealed class LayerDocumentTests
{
    /// <summary>The two chunks of the fixture, each a brace list of entities.</summary>
    private const string ChunkA = "{\n\t{\n\t\tA\n\t},\n}";

    private const string ChunkB = "{\n\t{\n\t\tB\n\t},\n}";

    /// <summary>An entity record, as a caller supplies one.</summary>
    private const string Record = "\t{\n\t\tC\n\t},\n";

    private const string Body = ChunkA + ChunkB;

    /// <summary>A layer with two chunks, declared in the order they sit.</summary>
    private static readonly string Header = HeaderFor(
        (12, 0, ChunkA.Length), (34, ChunkA.Length, ChunkB.Length));

    /// <summary>Builds a header declaring the given cells, offsets and sizes.</summary>
    private static string HeaderFor(params (int? Cell, int Offset, int Size)[] chunks) =>
        HeaderFor(2, chunks);

    /// <summary>The same, with the declared entity count named.</summary>
    private static string HeaderFor(int? entities, params (int? Cell, int Offset, int Size)[] chunks)
    {
        StringBuilder built = new("{\n\tcontent = {\n\t\tmapData = {},\n\t\tquadTreeHeader = {\n");
        foreach ((int? cell, int offset, int size) in chunks)
        {
            built.Append(cell is null ? "\t\t\t{\n" : $"\t\t\t[{cell}] = {{\n");
            built.Append(System.Globalization.CultureInfo.InvariantCulture, $"\t\t\t\toffset = {offset},\n");
            built.Append(System.Globalization.CultureInfo.InvariantCulture, $"\t\t\t\tsize = {size},\n\t\t\t}},\n");
        }

        built.Append("\t\t},\n\t\tspawnPointData = {},\n\t},\n\theader = {\n");
        if (entities is int declared)
        {
            built.Append(System.Globalization.CultureInfo.InvariantCulture, $"\t\tentities = {declared},\n");
        }

        built.Append("\t\tname = \"Made Up\",\n\t},\n}\n");
        return built.ToString();
    }

    private static SourceFile File(string text) =>
        SourceFile.FromMemory("layerdata.mlayer", Encoding.Latin1.GetBytes(text));

    private static LayerDocument Read(string text) => LayerDocument.Read(File(text)).Value;

    private static string Text(LayerDocument document) =>
        Encoding.Latin1.GetString(document.Bytes.Span);

    [Fact]
    public void A_layer_is_a_header_a_nul_and_chunks_that_tile_the_body()
    {
        LayerDocument layer = Read(Header + "\0" + Body);

        Assert.Equal(2, layer.Chunks.Length);
        Assert.Equal(12, layer.Chunks[0].Cell);
        Assert.Equal(0, layer.Chunks[0].Offset);
        Assert.Equal(ChunkA.Length, layer.Chunks[0].Size);
        Assert.Equal(34, layer.Chunks[1].Cell);
        Assert.Equal(ChunkA.Length, layer.Chunks[1].Offset);
        Assert.Equal(ChunkB.Length, layer.Chunks[1].Size);
        Assert.Equal(Header.Length + 1, layer.BodyStart);
    }

    [Fact]
    public void Reading_and_writing_back_changes_nothing()
    {
        string text = Header + "\0" + Body;

        Assert.Equal(text, Text(Read(text)));
    }

    [Fact]
    public void A_header_only_layer_reads_as_holding_nothing()
    {
        // 618 of 6,004 are this shape. "No entities here" is an answer, not a
        // fault, so it is read rather than refused.
        LayerDocument layer = Read("{\n\tcontent = {\n\t\tmapData = {},\n\t},\n}\n");

        Assert.Empty(layer.Chunks);
        Assert.Equal("{\n\tcontent = {\n\t\tmapData = {},\n\t},\n}\n", Text(layer));
    }

    [Fact]
    public void A_chunk_entry_without_a_key_is_read_like_any_other()
    {
        // 1,093 entries across 898 layers are written bare. A reader that
        // required the key silently skipped them, and the file then looked as
        // though its chunks did not account for the body.
        LayerDocument layer = Read(
            Header.Replace("[34] = {", "{", StringComparison.Ordinal) + "\0" + Body);

        Assert.Equal(2, layer.Chunks.Length);
        Assert.Equal(12, layer.Chunks[0].Cell);
        Assert.Null(layer.Chunks[1].Cell);
        Assert.Equal(ChunkA.Length, layer.Chunks[1].Offset);
    }

    [Fact]
    public void An_entity_is_inserted_before_the_chunks_closing_brace()
    {
        LayerDocument layer = Read(Header + "\0" + Body).WithEntity(0, Record).Value;

        Assert.Contains("{\n\t{\n\t\tA\n\t},\n\t{\n\t\tC\n\t},\n}", Text(layer), StringComparison.Ordinal);
        Assert.Equal(ChunkA.Length + Record.Length, layer.Chunks[0].Size);
        Assert.Equal(ChunkA.Length + Record.Length, layer.Chunks[1].Offset);
        Assert.Equal(ChunkB.Length, layer.Chunks[1].Size);
    }

    [Fact]
    public void The_chunks_that_did_not_grow_keep_their_contents()
    {
        LayerDocument layer = Read(Header + "\0" + Body).WithEntity(0, Record).Value;
        string text = Text(layer);

        Assert.Contains("{\n\t{\n\t\tB\n\t},\n}", text, StringComparison.Ordinal);
        Assert.Contains("name = \"Made Up\"", text, StringComparison.Ordinal);
        Assert.EndsWith("{\n\t{\n\t\tB\n\t},\n}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Offsets_shift_by_where_a_chunk_sits_not_by_where_it_is_declared()
    {
        // The trap. The key is a quad-tree cell, so a layer may declare the
        // chunk that sits second in the body first — 204 shipped layers do.
        // Shifting "the ones after this in the list" moves the wrong ones and
        // produces a file that still parses.
        string swapped = HeaderFor(
            (34, ChunkA.Length, ChunkB.Length), (12, 0, ChunkA.Length));

        LayerDocument layer = Read(swapped + "\0" + Body);
        Assert.Equal(34, layer.Chunks[0].Cell);

        // Growing the chunk declared second, which sits first, must move the
        // chunk declared first, which sits second.
        LayerDocument grown = layer.WithEntity(1, Record).Value;

        Assert.Equal(ChunkA.Length + Record.Length, grown.Chunks[1].Size);
        Assert.Equal(0, grown.Chunks[1].Offset);
        Assert.Equal(ChunkA.Length + Record.Length, grown.Chunks[0].Offset);
        Assert.Equal(ChunkB.Length, grown.Chunks[0].Size);
        Assert.EndsWith("{\n\t{\n\t\tB\n\t},\n}", Text(grown), StringComparison.Ordinal);
    }

    [Fact]
    public void A_number_that_grows_a_digit_does_not_disturb_the_body()
    {
        // The insertion pushes the size into three digits, so the header itself
        // gets longer. The offsets are measured from the body rather than from
        // the file, so nothing else has to move — a reader that measured from
        // the file would break exactly here.
        LayerDocument layer = Read(Header + "\0" + Body)
            .WithEntity(0, new string('x', 100 - ChunkA.Length)).Value;

        Assert.Equal(100, layer.Chunks[0].Size);
        Assert.Equal(100, layer.Chunks[1].Offset);
        Assert.EndsWith("{\n\t{\n\t\tB\n\t},\n}", Text(layer), StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_the_chunks_do_not_account_for_is_refused()
    {
        // The accounting is what makes an edit safe: if the chunks tile the body
        // exactly, shifting them is a complete description of the file.
        Result<LayerDocument> result = LayerDocument.Read(File(Header + "\0" + Body + "extra"));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Contains("account for", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_between_chunks_is_refused()
    {
        Result<LayerDocument> result = LayerDocument.Read(File(
            HeaderFor((12, 0, ChunkA.Length), (34, ChunkA.Length + 1, ChunkB.Length)) + "\0" + Body));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_header_declaring_chunks_with_no_body_is_refused()
    {
        Result<LayerDocument> result = LayerDocument.Read(File(Header));

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_chunk_the_layer_does_not_have_is_refused()
    {
        Result<LayerDocument> result = Read(Header + "\0" + Body).WithEntity(5, "\t{\n\t},\n");

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void An_empty_record_is_refused_rather_than_written()
    {
        Assert.False(Read(Header + "\0" + Body).WithEntity(0, string.Empty).IsSuccess);
    }

    /// <summary>
    /// The header's own count of entities, which an insertion has to move.
    /// </summary>
    /// <remarks>
    /// It is a real count, not editor bookkeeping. Over 250 shipped layers, all
    /// 210 holding no <c>Entity Template</c> declare exactly the records their
    /// chunks contain. This shipped stale in probe batch 3 and the layer it was
    /// written into stopped drawing most of what it held.
    /// </remarks>
    [Fact]
    public void The_declared_entity_count_is_read_from_the_header()
    {
        Assert.Equal(2, Read(Header + "\0" + Body).DeclaredEntities);
    }

    [Fact]
    public void Adding_an_entity_raises_the_declared_count_by_one()
    {
        LayerDocument grown = Read(Header + "\0" + Body).WithEntity(0, Record).Value;

        Assert.Equal(3, grown.DeclaredEntities);
        Assert.Contains("entities = 3,", Text(grown), StringComparison.Ordinal);
        Assert.DoesNotContain("entities = 2,", Text(grown), StringComparison.Ordinal);
    }

    /// <summary>
    /// A count whose digits grow moves every byte after it, and the chunk
    /// offsets are measured from the body rather than the file, so they stay
    /// true. Nine to ten is the case that would betray an index rebuilt by
    /// arithmetic instead of by re-reading.
    /// </summary>
    [Fact]
    public void A_count_that_gains_a_digit_still_leaves_the_chunks_tiling()
    {
        string header = HeaderFor(9, (12, 0, ChunkA.Length), (34, ChunkA.Length, ChunkB.Length));
        LayerDocument grown = Read(header + "\0" + Body).WithEntity(0, Record).Value;

        Assert.Equal(10, grown.DeclaredEntities);
        Assert.Equal(ChunkA.Length + Record.Length, grown.Chunks[0].Size);
        Assert.Equal(ChunkA.Length + Record.Length, grown.Chunks[1].Offset);
        Assert.Equal(ChunkB.Length, grown.Chunks[1].Size);
    }

    /// <summary>
    /// A layer that declares no count is refused rather than written stale.
    /// </summary>
    /// <remarks>
    /// No shipped layer is like this — all 250 sampled declare it — so this is
    /// the guard for a file the corpus has never shown, and refusing is the only
    /// answer that cannot produce a file disagreeing with itself.
    /// </remarks>
    [Fact]
    public void A_layer_declaring_no_count_refuses_an_insertion()
    {
        string header = HeaderFor(null, (12, 0, ChunkA.Length), (34, ChunkA.Length, ChunkB.Length));
        Result<LayerDocument> grown = Read(header + "\0" + Body).WithEntity(0, Record);

        Assert.False(grown.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, grown.Refusal.Kind);
        Assert.Contains("entities", grown.Refusal.Message, StringComparison.Ordinal);
    }
}
