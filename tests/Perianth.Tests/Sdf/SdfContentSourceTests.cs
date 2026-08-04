using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Sdf;

public sealed class SdfContentSourceTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-sdf-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_single_uncompressed_chunk_round_trips()
    {
        byte[] payload = Pattern(4096);
        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(payload);

        container.Index = new SdfIndexBuilder()
            .Literal("tex/one.dds")
            .Terminal(chunkCount: 1)
            .Chunk(decodedSize: payload.Length, archiveOffset: offset)
            .Build();

        Assert.Equal(payload, Read(container, "tex/one.dds"));
    }

    [Fact]
    public void A_resident_prefix_is_prepended_once_ahead_of_every_chunk()
    {
        // The prefix goes on once, before the first chunk, however many
        // follow, and the entry's declared total counts only the chunks.
        byte[] prefix = Pattern(0x80, seed: 7);
        byte[] first = Pattern(600);
        byte[] second = Pattern(400, seed: 3);

        SdfContainerBuilder container = new();
        container.ResidentPrefixes.Add(prefix);
        long a = container.AppendToArchive(first);
        long b = container.AppendToArchive(second);

        container.Index = new SdfIndexBuilder()
            .Literal("tex/two.dds")
            .Terminal(chunkCount: 2, residentIndex: 0)
            .Chunk(decodedSize: first.Length, archiveOffset: a)
            .Chunk(decodedSize: second.Length, archiveOffset: b)
            .Build();

        Assert.Equal([.. prefix, .. first, .. second], Read(container, "tex/two.dds"));
    }

    [Fact]
    public void A_multi_page_chunk_handles_a_full_stored_page_and_an_uncompressed_one()
    {
        // Three pages exercising both encoding traps at once. Page 0 is
        // compressed. Page 1 was written through uncompressed and fills a
        // whole page, so its stored size is a full 0x10000 and the 16-bit
        // field cannot say so: it is written as zero, which also happens to
        // make stored equal decoded. Page 2 is the partial remainder.
        byte[] page0 = Repeated(SdfContainerBuilder.PageBytes, 0xAB);
        byte[] page1 = Pattern(SdfContainerBuilder.PageBytes, seed: 11);
        byte[] page2 = Pattern(1234, seed: 5);

        byte[] stored0 = SdfContainerBuilder.Deflate(page0);
        Assert.True(stored0.Length < SdfContainerBuilder.PageBytes, "page 0 must actually compress");

        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(stored0);
        container.AppendToArchive(page1);
        byte[] stored2 = SdfContainerBuilder.Deflate(page2);
        container.AppendToArchive(stored2);

        int decoded = page0.Length + page1.Length + page2.Length;
        long storedTotal = stored0.Length + page1.Length + stored2.Length;

        container.Index = new SdfIndexBuilder()
            .Literal("tex/paged.dds")
            .Terminal(chunkCount: 1)
            .Chunk(
                decodedSize: decoded,
                archiveOffset: offset,
                storedSize: storedTotal,
                pageStoredSizes: [stored0.Length, 0, stored2.Length])
            .Build();

        Assert.Equal([.. page0, .. page1, .. page2], Read(container, "tex/paged.dds"));
    }

    [Fact]
    public void A_page_vector_that_does_not_sum_to_the_stored_size_refuses()
    {
        // Each page's position is the running total of the sizes before it,
        // so one wrong size displaces every page after it. The aggregate is
        // checked before any bytes are read.
        byte[] page0 = Pattern(SdfContainerBuilder.PageBytes);
        byte[] page1 = Pattern(64, seed: 2);
        byte[] stored0 = SdfContainerBuilder.Deflate(page0);
        byte[] stored1 = SdfContainerBuilder.Deflate(page1);

        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(stored0);
        container.AppendToArchive(stored1);

        container.Index = new SdfIndexBuilder()
            .Literal("tex/bad.dds")
            .Terminal(chunkCount: 1)
            .Chunk(
                decodedSize: page0.Length + page1.Length,
                archiveOffset: offset,
                storedSize: stored0.Length + stored1.Length + 9,
                pageStoredSizes: [stored0.Length, stored1.Length])
            .Build();

        Refusal refusal = ReadRefused(container, "tex/bad.dds");
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("pages account for", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_branch_rule_orders_rather_than_matching()
    {
        // Key 'z' with the alternate holding the path that starts with 'z'.
        // Ordering sends 'z' to the alternate because z >= z; an equality or
        // less-than-or-equal reading sends it inline instead and the lookup
        // misses. A path whose character is merely below the key cannot tell
        // the two apart, which is why one of each is checked.
        byte[] left = Pattern(32, seed: 1);
        byte[] right = Pattern(48, seed: 2);

        SdfContainerBuilder container = new();
        long a = container.AppendToArchive(left);
        long b = container.AppendToArchive(right);

        SdfIndexBuilder index = new();
        index.Literal("tex/");
        int patch = index.Branch('z');

        // Inline child sits immediately after the branch record.
        index.Literal("b.dds").Terminal(chunkCount: 1).Chunk(left.Length, a);

        int alternate = index.Position;
        index.Literal("z.dds").Terminal(chunkCount: 1).Chunk(right.Length, b);
        index.PatchBranch(patch, alternate);

        container.Index = index.Build();

        Assert.Equal(left, Read(container, "tex/b.dds"));
        Assert.Equal(right, Read(container, "tex/z.dds"));
    }

    [Fact]
    public void An_entry_whose_path_is_a_prefix_of_another_entry_still_resolves()
    {
        // The shape the container actually ships: barks.locpack sits behind a
        // branch because barks.locpackbin shares its spelling, so the query runs
        // out with the branch still undecided. An exhausted query compares as
        // its terminator and takes the inline child; a reader that answers
        // "absent" there loses 211 real files, and every one of them is a file
        // whose name another file merely extends.
        byte[] shorter = Pattern(32, seed: 1);
        byte[] longer = Pattern(48, seed: 2);

        SdfContainerBuilder container = new();
        long a = container.AppendToArchive(shorter);
        long b = container.AppendToArchive(longer);

        SdfIndexBuilder index = new();
        index.Literal("loc/barks.locpack");
        int patch = index.Branch('b');

        // Inline: the query ended here, so nothing more is spelled.
        index.Terminal(chunkCount: 1).Chunk(shorter.Length, a);

        int alternate = index.Position;
        index.Literal("bin").Terminal(chunkCount: 1).Chunk(longer.Length, b);
        index.PatchBranch(patch, alternate);

        container.Index = index.Build();

        Assert.Equal(shorter, Read(container, "loc/barks.locpack"));
        Assert.Equal(longer, Read(container, "loc/barks.locpackbin"));

        // …and consuming the whole query is still required, so the prefix of a
        // prefix stays absent rather than riding in on the same relaxation.
        Assert.False(Lookup(container, "loc/barks.locpac").IsPresent);
    }

    [Fact]
    public void A_path_that_is_only_a_prefix_of_an_entry_is_absent()
    {
        // The descent must consume the whole query. A reader that accepted the
        // terminal it landed nearest would return the real file for this.
        SdfContainerBuilder container = Simple("tex/one.dds", Pattern(64));

        Assert.False(Lookup(container, "tex/one").IsPresent);
        Assert.False(Lookup(container, "tex/").IsPresent);
        Assert.False(Lookup(container, "tex/one.dd").IsPresent);
    }

    [Fact]
    public void Absence_is_reported_as_a_value_rather_than_a_refusal()
    {
        // The resolution order tries loose content first and falls back to the
        // archives only when the exact path is absent, so "not here" has to be
        // answerable without failing.
        SdfContainerBuilder container = Simple("tex/one.dds", Pattern(64));

        Result<SdfContent> result = Source(container).Read("tex/missing.dds");

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsPresent);
    }

    [Fact]
    public void Lookup_is_case_insensitive_and_accepts_either_separator()
    {
        byte[] payload = Pattern(64);
        SdfContainerBuilder container = Simple("tex/one.dds", payload);

        Assert.Equal(payload, Read(container, "TEX/ONE.DDS"));
        Assert.Equal(payload, Read(container, @"tex\one.dds"));
    }

    [Fact]
    public void A_path_substitution_terminal_refuses_rather_than_answering()
    {
        // Such a terminal names a path derived from, but not equal to, the one
        // the descent matched, so the match that reached it cannot be trusted.
        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(Pattern(32));

        container.Index = new SdfIndexBuilder()
            .Literal("tex/one.dds")
            .Terminal(chunkCount: 1, pathPatch: true)
            .Chunk(32, offset)
            .Build();

        Refusal refusal = ReadRefused(container, "tex/one.dds");
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("path-substitution", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_width_above_four_refuses()
    {
        // The runtime's destination is a 32-bit offset, so wider codes have
        // nowhere to go. Where they have turned up in practice they meant a
        // cursor had drifted.
        SdfContainerBuilder container = new();
        container.AppendToArchive(Pattern(32));

        container.Index = new SdfIndexBuilder()
            .Literal("tex/one.dds")
            .Terminal(chunkCount: 1)
            .Chunk(32, 0, offsetWidth: 5)
            .Build();

        Refusal refusal = ReadRefused(container, "tex/one.dds");
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("no representable destination", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_chunk_naming_an_archive_beyond_the_named_families_refuses()
    {
        SdfContainerBuilder container = new();
        container.AppendToArchive(Pattern(32));

        container.Index = new SdfIndexBuilder()
            .Literal("tex/one.dds")
            .Terminal(chunkCount: 1)
            .Chunk(32, 0, archiveId: 3000)
            .Build();

        Refusal refusal = ReadRefused(container, "tex/one.dds");
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("families", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_range_beyond_the_archive_refuses_rather_than_returning_what_is_there()
    {
        SdfContainerBuilder container = new();
        container.AppendToArchive(Pattern(32));

        container.Index = new SdfIndexBuilder()
            .Literal("tex/one.dds")
            .Terminal(chunkCount: 1)
            .Chunk(decodedSize: 4096, archiveOffset: 0)
            .Build();

        Refusal refusal = ReadRefused(container, "tex/one.dds");
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("lies outside its", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_layout_flag_is_read_rather_than_assumed()
    {
        // A nonzero flag introduces a 0x140 block. Assuming either case
        // shifts every table after it, including the compressed index.
        byte[] payload = Pattern(64);

        SdfContainerBuilder present = Simple("tex/one.dds", payload);
        present.LayoutFlag = 1;
        Assert.Equal(payload, Read(present, "tex/one.dds"));

        SdfContainerBuilder absent = Simple("tex/one.dds", payload);
        absent.LayoutFlag = 0;
        Assert.Equal(payload, Read(absent, "tex/one.dds"));
    }

    [Fact]
    public void The_index_position_is_derived_from_the_declared_counts()
    {
        // Install parts and resident prefixes both push the compressed index
        // along. A hardcoded position works for one container only.
        byte[] payload = Pattern(64);

        SdfContainerBuilder container = Simple("tex/one.dds", payload);
        container.InstallPartCount = 3;
        container.ResidentPrefixes.Add(Pattern(0x40, seed: 9));

        // The entry names no prefix, so the table's only job here is to move
        // the index along by its own length.
        Assert.Equal(payload, Read(container, "tex/one.dds"));
    }

    [Fact]
    public void A_container_that_is_not_an_sdf_table_of_contents_refuses()
    {
        SdfContainerBuilder container = Simple("tex/one.dds", Pattern(32));
        container.Magic = "EAST";

        Refusal refusal = TocRefused(container);
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("WEST", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unverified_container_version_refuses()
    {
        // Later versions are reported to insert a header field, which would
        // silently shift every derived offset below it.
        SdfContainerBuilder container = Simple("tex/one.dds", Pattern(32));
        container.Version = 0x17;

        Refusal refusal = TocRefused(container);
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("0x17", refusal.Message, StringComparison.Ordinal);
    }

    private static SdfContainerBuilder Simple(string path, byte[] payload)
    {
        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(payload);
        container.Index = new SdfIndexBuilder()
            .Literal(path)
            .Terminal(chunkCount: 1)
            .Chunk(payload.Length, offset)
            .Build();
        return container;
    }

    private static byte[] Pattern(int length, int seed = 0)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(((i * 31) + (seed * 17) + (i / 251)) & 0xFF);
        }

        return bytes;
    }

    private static byte[] Repeated(int length, byte value)
    {
        byte[] bytes = new byte[length];
        Array.Fill(bytes, value);
        return bytes;
    }

    private SdfContentSource Source(SdfContainerBuilder container)
    {
        container.Write(_directory.FullName);
        return new SdfContentSource(_directory.FullName);
    }

    private byte[] Read(SdfContainerBuilder container, string path)
    {
        SdfContent content = Lookup(container, path);
        Assert.True(content.IsPresent, $"{path} was reported absent");
        return content.Bytes.ToArray();
    }

    private SdfContent Lookup(SdfContainerBuilder container, string path)
    {
        using SdfContentSource source = Source(container);
        Result<SdfContent> result = source.Read(path);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        return result.Value;
    }

    private Refusal ReadRefused(SdfContainerBuilder container, string path)
    {
        using SdfContentSource source = Source(container);
        Result<SdfContent> result = source.Read(path);
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }

    private Refusal TocRefused(SdfContainerBuilder container)
    {
        using SdfContentSource source = Source(container);
        Result<SdfToc> result = source.Toc();
        Assert.True(result.IsRefused, "expected a refusal");
        return result.Refusal;
    }
}
