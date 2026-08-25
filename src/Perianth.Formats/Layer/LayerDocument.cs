using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Formats.Layer;

/// <summary>
/// One chunk of a layer's body, as the header declares it.
/// </summary>
/// <remarks>
/// <para>
/// The key is a quad-tree cell number, not a position, which is why
/// <see cref="LayerDocument"/> sorts by <see cref="Offset"/> everywhere it
/// matters: 204 of 5,386 shipped layers declare their chunks out of offset
/// order, and laying the body out in declaration order would corrupt every one
/// of them (Roadmap §10.97).
/// </para>
/// <para>
/// <b>And a chunk need not have a key at all.</b> 1,093 entries across 898
/// layers are written bare, as a positional member of the table rather than a
/// keyed one, so a chunk is addressed by where it is declared and the cell is
/// information rather than an identity.
/// </para>
/// </remarks>
/// <param name="Cell">The quad-tree cell, where the entry names one.</param>
/// <param name="Offset">Where it begins, measured from the start of the body.</param>
/// <param name="Size">How many bytes it occupies.</param>
/// <param name="OffsetDigits">Where the offset is spelled in the header.</param>
/// <param name="SizeDigits">Where the size is spelled in the header.</param>
public readonly record struct LayerChunk(
    int? Cell, int Offset, int Size, ByteRange OffsetDigits, ByteRange SizeDigits);

/// <summary>
/// A map layer — the file that says which props stand where.
/// </summary>
/// <remarks>
/// <para>
/// A <c>.mlayer</c> is <b>a text header, a NUL, then a body of text chunks</b>,
/// and the header's <c>quadTreeHeader</c> gives each chunk an offset and a size
/// into that body. Each chunk is a brace list of entity records, and an entity
/// of <c>type = "Prop"</c> carries a 4x4 matrix and names its graph object
/// outright. That is the whole chain from a map to a model (Roadmap §10.97).
/// </para>
/// <para>
/// Like <c>JuiceDocument</c>, this is <b>a span index, not a parse</b>: it
/// records where the header, each chunk and each declared number sit, and an
/// edit is a splice. The file is never reassembled from a model of itself, so
/// every byte outside an edit survives — which is what lets a reader that
/// understands four numbers work on a format holding twenty-five entity types
/// and hundreds of fields it has never heard of.
/// </para>
/// <para>
/// <b>The offsets are derived fields.</b> Inserting an entity lengthens one
/// chunk, so that chunk's size and every later chunk's offset have to move with
/// it. This is the locpack's row count and the MMB tail's restated vertex count
/// again, and the failure is the same shape: a file that disagrees with itself,
/// which nothing notices until something reads the part that moved.
/// </para>
/// </remarks>
public sealed class LayerDocument
{
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly ByteRange _entityDigits;

    private LayerDocument(
        string path,
        ReadOnlyMemory<byte> bytes,
        int bodyStart,
        ImmutableArray<LayerChunk> chunks,
        int declaredEntities,
        ByteRange entityDigits)
    {
        Path = path;
        _bytes = bytes;
        BodyStart = bodyStart;
        Chunks = chunks;
        DeclaredEntities = declaredEntities;
        _entityDigits = entityDigits;
    }

    /// <summary>The path as the caller supplied it.</summary>
    public string Path { get; }

    /// <summary>The bytes as they stand, edits included.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>
    /// Where the body begins — just past the NUL, or the end of the file for a
    /// layer that declares no chunks at all.
    /// </summary>
    /// <remarks>
    /// 618 of 6,004 shipped layers are header-only: no NUL, no chunks, nothing
    /// placed. They are read rather than refused, because "this layer holds no
    /// entities" is an answer and not a fault.
    /// </remarks>
    public int BodyStart { get; }

    /// <summary>The chunks, in the order the header declares them.</summary>
    public ImmutableArray<LayerChunk> Chunks { get; }

    /// <summary>
    /// The <c>entities</c> count the header declares, or -1 where it declares none.
    /// </summary>
    /// <remarks>
    /// <b>A derived field, and the third of its kind.</b> It is a real count and
    /// not editor bookkeeping: over 250 shipped layers, all 210 that hold no
    /// <c>Entity Template</c> declare exactly the number of records their chunks
    /// contain, with no exceptions. The other 40 declare more, by an amount that
    /// scales with how many templates they hold — a template names a
    /// <c>.mleveltemplate</c> with an entities tree of its own (§10.97), so the
    /// count includes what the templates bring in.
    /// <para>
    /// That distinction is why a first census read it as unreliable: counting
    /// records alone shows 40 shipped layers "disagreeing with themselves", and
    /// concluding the field was ignorable would have been wrong. Separate the
    /// population that has the feature from the one that does not before
    /// calling a field noisy.
    /// </para>
    /// </remarks>
    public int DeclaredEntities { get; }

    /// <summary>Reads a layer and checks its chunks account for the whole body.</summary>
    /// <remarks>
    /// The accounting is the guard, and it is what makes an edit safe: if the
    /// declared chunks tile the body exactly, then moving one and shifting the
    /// rest is a complete description of the file. All 6,004 shipped layers pass
    /// it — 618 header-only and 5,386 tiling exactly, with no gaps and nothing
    /// left over.
    /// </remarks>
    public static Result<LayerDocument> Read(SourceFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Byte per char, so a range is a byte offset. Everything this looks for
        // is ASCII, and an edit splices bytes rather than re-encoding them.
        string text = Encoding.Latin1.GetString(file.Bytes);

        int nul = text.IndexOf('\0', StringComparison.Ordinal);
        if (nul < 0)
        {
            // Header-only. The body is empty and there is nothing to tile.
            return Chunked(text, 0, out ImmutableArray<LayerChunk> declared, out Refusal? headerRefusal)
                ? declared.Length == 0
                    ? Result.Ok(new LayerDocument(
                        file.Path, file.Bytes.ToArray(), text.Length, [],
                        DeclaredCount(text, out int headerOnlyCount, out ByteRange headerOnlyDigits)
                            ? headerOnlyCount : -1,
                        headerOnlyDigits))
                    : Refusal.Malformed(string.Create(
                        CultureInfo.InvariantCulture,
                        $"The layer declares {declared.Length} chunks and has no body to hold them."))
                : headerRefusal!;
        }

        if (!Chunked(text[..nul], nul + 1, out ImmutableArray<LayerChunk> chunks, out Refusal? refusal))
        {
            return refusal!;
        }

        int body = text.Length - (nul + 1);
        Refusal? tiling = Tiles(chunks, body);
        return tiling is null
            ? Result.Ok(new LayerDocument(
                file.Path, file.Bytes.ToArray(), nul + 1, chunks,
                DeclaredCount(text[..nul], out int count, out ByteRange digits) ? count : -1,
                digits))
            : tiling;
    }


    /// <summary>
    /// The same layer with one more entity record at the end of a chunk, and
    /// every offset the insertion moved brought up to date.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record's text is the caller's, because what an entity holds belongs
    /// to its type — a prop, a waypoint, a spawn point — and this deliberately
    /// knows none of them. It is inserted immediately before the chunk's closing
    /// brace, so the entities already there are untouched.
    /// </para>
    /// <para>
    /// <b>Later means later in the body, not later in the header.</b> The chunks
    /// are keyed by cell number and 204 shipped layers declare theirs out of
    /// order, so shifting "the ones after this in the list" would move the wrong
    /// ones and produce a file that still parses.
    /// </para>
    /// </remarks>
    public Result<LayerDocument> WithEntity(int chunk, string record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (chunk < 0 || chunk >= Chunks.Length)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer declares {Chunks.Length} chunks, so there is no chunk {chunk}."));
        }

        LayerChunk target = Chunks[chunk];

        if (record.Length == 0)
        {
            return Refusal.Unsupported("An entity record cannot be empty.");
        }

        string text = Encoding.Latin1.GetString(_bytes.Span);
        int close = BodyStart + target.Offset + target.Size - 1;
        if (text[close] != '}')
        {
            return Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"Chunk {chunk} does not end with a brace, so it is not a list of entities."));
        }

        if (DeclaredEntities < 0)
        {
            return Refusal.Unsupported(
                "This layer's header declares no 'entities' count, so adding one would "
                + "leave the file disagreeing with itself. Every shipped layer declares it.");
        }

        byte[] inserted = Encoding.Latin1.GetBytes(record);
        StringBuilder built = new(text.Length + inserted.Length + 64);

        // The header first, with each declared number rewritten as it is passed.
        // Ordered by where the digits sit rather than by chunk, since a chunk's
        // offset and size are two separate literals in no fixed relation.
        List<(ByteRange Range, int Value)> numbers = [];
        for (int i = 0; i < Chunks.Length; i++)
        {
            LayerChunk each = Chunks[i];
            int offset = each.Offset > target.Offset ? each.Offset + inserted.Length : each.Offset;
            int size = i == chunk ? each.Size + inserted.Length : each.Size;
            numbers.Add((each.OffsetDigits, offset));
            numbers.Add((each.SizeDigits, size));
        }

        // The declared count moves with the insertion, exactly as the chunk
        // sizes and offsets do. It sits in the header alongside them, so it
        // rewrites through the same pass.
        numbers.Add((_entityDigits, DeclaredEntities + 1));

        numbers.Sort((a, b) => a.Range.Offset.CompareTo(b.Range.Offset));

        int at = 0;
        foreach ((ByteRange range, int value) in numbers)
        {
            built.Append(text, at, range.Offset - at);
            built.Append(value.ToString(CultureInfo.InvariantCulture));
            at = range.Offset + range.Length;
        }

        built.Append(text, at, close - at);
        built.Append(record);
        built.Append(text, close, text.Length - close);

        // Re-read rather than adjusting the ranges by hand. The spans are the
        // whole correctness of this type, and a rebuilt index is right by
        // construction where a shifted one has to be right about every entry.
        return Read(SourceFile.FromMemory(Path, Encoding.Latin1.GetBytes(built.ToString())));
    }

    private static Refusal? Tiles(ImmutableArray<LayerChunk> chunks, int body)
    {
        if (chunks.Length == 0)
        {
            return body == 0
                ? null
                : Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The layer declares no chunks but carries {body} bytes of body."));
        }

        LayerChunk[] ordered = [.. chunks];
        Array.Sort(ordered, (a, b) => a.Offset.CompareTo(b.Offset));

        int at = 0;
        foreach (LayerChunk chunk in ordered)
        {
            if (chunk.Offset != at)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A chunk begins at {chunk.Offset} where the one before it ends at {at}."));
            }

            at += chunk.Size;
        }

        return at == body
            ? null
            : Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer's chunks account for {at} bytes of a {body}-byte body."));
    }

    /// <summary>
    /// Indexes the header's <c>quadTreeHeader</c>, which is the only section
    /// that ever declares an offset.
    /// </summary>
    /// <remarks>
    /// <c>mapData</c> is empty on all 5,386 layers that have a body and
    /// <c>spawnPointData</c> on 4,998 of them; neither declares an offset
    /// anywhere in the corpus. Reading only the section that does is what keeps
    /// this from indexing something it has never seen — and an early census that
    /// swept the whole header instead read the same bytes twice.
    /// </remarks>
    /// <summary>
    /// The <c>entities</c> count declared inside the header's own
    /// <c>header = {</c> block.
    /// </summary>
    /// <remarks>
    /// Scoped to that block rather than searched for across the whole header,
    /// because the block's <c>cell</c> value is a quoted string carrying its own
    /// <c>=</c> assignments. A loose search is how a brace inside a quoted value
    /// once truncated the juice index.
    /// </remarks>
    private static bool DeclaredCount(string header, out int value, out ByteRange digits)
    {
        value = -1;
        digits = default;

        const string Section = "header = {";
        int at = header.IndexOf(Section, StringComparison.Ordinal);
        return at >= 0
            && Field(header, at + Section.Length, header.Length, "entities = ", out value, out digits);
    }

    private static bool Chunked(
        string header, int bodyStart, out ImmutableArray<LayerChunk> chunks, out Refusal? refusal)
    {
        chunks = [];
        refusal = null;
        _ = bodyStart;

        const string Section = "quadTreeHeader = {";
        int at = header.IndexOf(Section, StringComparison.Ordinal);
        if (at < 0)
        {
            return true;
        }

        ImmutableArray<LayerChunk>.Builder found = ImmutableArray.CreateBuilder<LayerChunk>();
        int depth = 1;
        int cursor = at + Section.Length;
        int opened = -1;

        // Each entry is a brace block one level inside the section. Its key, if
        // it has one, sits before that brace — and 1,093 entries across 898
        // layers have none, being positional members of the table rather than
        // keyed ones. Looking for the block rather than the key is what reads
        // both forms with one rule.
        while (cursor < header.Length && depth > 0)
        {
            char c = header[cursor];
            if (c == '{')
            {
                if (depth == 1)
                {
                    opened = cursor;
                }

                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 1 && opened >= 0)
                {
                    if (!Entry(header, opened, cursor, out LayerChunk chunk))
                    {
                        refusal = Refusal.Malformed(string.Create(
                            CultureInfo.InvariantCulture,
                            $"A quadTreeHeader entry at byte {opened} declares no offset and size."));
                        return false;
                    }

                    found.Add(chunk);
                    opened = -1;
                }
            }

            cursor++;
        }

        chunks = found.ToImmutable();
        return true;
    }

    /// <summary>Reads one entry's offset, size and optional cell key.</summary>
    private static bool Entry(string header, int open, int end, out LayerChunk chunk)
    {
        chunk = default;

        if (!Field(header, open, end, "offset = ", out int offset, out ByteRange offsetDigits)
            || !Field(header, open, end, "size = ", out int size, out ByteRange sizeDigits))
        {
            return false;
        }

        chunk = new LayerChunk(KeyBefore(header, open), offset, size, offsetDigits, sizeDigits);
        return true;
    }

    /// <summary>
    /// The <c>[n] =</c> immediately before an entry's brace, where there is one.
    /// </summary>
    private static int? KeyBefore(string header, int open)
    {
        int at = open - 1;
        while (at >= 0 && header[at] is ' ' or '\t')
        {
            at--;
        }

        if (at < 0 || header[at] != '=')
        {
            return null;
        }

        at--;
        while (at >= 0 && header[at] is ' ' or '\t')
        {
            at--;
        }

        if (at < 0 || header[at] != ']')
        {
            return null;
        }

        int close = at;
        at--;
        while (at >= 0 && char.IsAsciiDigit(header[at]))
        {
            at--;
        }

        return at >= 0 && header[at] == '[' && close > at + 1 && Number(header, at + 1, close, out int cell)
            ? cell
            : null;
    }

    private static bool Field(
        string header, int from, int to, string name, out int value, out ByteRange digits)
    {
        value = 0;
        digits = default;

        int at = header.IndexOf(name, from, to - from, StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        int start = at + name.Length;
        int end = start;
        while (end < to && char.IsAsciiDigit(header[end]))
        {
            end++;
        }

        if (end == start || !Number(header, start, end, out value))
        {
            return false;
        }

        digits = new ByteRange(start, end - start);
        return true;
    }

    private static bool Number(string text, int from, int to, out int value) =>
        int.TryParse(
            text.AsSpan(from, Math.Max(0, to - from)),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
}
