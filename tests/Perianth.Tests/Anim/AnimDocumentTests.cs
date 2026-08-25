using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Anim;

/// <summary>
/// The container reading and its writer, over invented files.
/// </summary>
/// <remarks>
/// The corpus oracle proves agreement on what shipped; these prove the things it
/// cannot see. A shipped animation never has a chunk in the wrong place, never
/// disagrees with its own counts, and — the one that matters most — never
/// exercises the choice between a flat channel and a compressed one at a size
/// where both readings would tile the file. Every fixture here is invented, as
/// the repository requires: the grammar is what a test exercises.
/// </remarks>
public sealed class AnimDocumentTests
{
    [Fact]
    public void A_compressed_animation_writes_back_byte_for_byte()
    {
        byte[] bytes = Build();
        AnimDocument document = Read(bytes);

        Assert.Equal(2, document.NodeCount);
        Assert.Equal(["root", "hand"], document.Names);
        Assert.All(document.Channels, channel => Assert.True(channel.Compressed));

        Assert.Equal(bytes, AnimWriter.Write(document).Value);
    }

    [Fact]
    public void A_flat_channel_writes_back_byte_for_byte()
    {
        // Two animated channels over three samples, stored densely: no change
        // table follows, so the values run straight to the next chunk's tag.
        byte[] bytes = Build(translation: Flat(animated: 2, samples: 3, stride: 8));
        AnimDocument document = Read(bytes);

        Assert.False(document.Channels[0].Compressed);
        Assert.True(document.Channels[1].Compressed);
        Assert.Equal(48, document.Channels[0].Values.Length);
        Assert.Equal(bytes, AnimWriter.Write(document).Value);
    }

    [Fact]
    public void A_static_value_is_sized_by_the_stream_that_selects_it()
    {
        // A blob states no length of its own; the count of selectors at or above
        // 0x8000 is what says how long it is.
        byte[] bytes = Build(
            scaleSelectors: [0x8000, 0x8001],
            scaleStatics: new byte[16],
            scale: Flat(animated: 0, samples: 3, stride: 8));

        AnimDocument document = Read(bytes);

        Assert.Equal(16, document.Channels[2].Statics.Length);
        Assert.Equal(bytes, AnimWriter.Write(document).Value);
    }

    [Fact]
    public void A_chunk_that_is_not_where_the_counts_put_it_refuses()
    {
        // One more node than the file holds names for: every per-node chunk is
        // then the wrong length and NAME lands somewhere that is not a tag.
        byte[] bytes = Build();
        bytes[0x24] = 3;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void A_tail_restating_a_different_node_count_refuses()
    {
        byte[] bytes = Build();
        int at = bytes.Length - 4 - 4 - 1;
        bytes[at] = 9;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("node count", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_count_that_disagrees_with_its_stream_refuses()
    {
        // The header says how many nodes a channel animates and the stream says
        // it again. They are independent, so checking one against the other
        // catches a file whose chunks are not the lengths it claims.
        byte[] bytes = Build();
        bytes[0x28] = 1;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("animates 1 nodes and TRAI selects 2", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_static_count_that_disagrees_with_its_stream_refuses()
    {
        // Changed in the stream rather than the header, because the header's copy
        // is what sizes the blob: lowering it there shortens a chunk and the next
        // tag moves, which a different check catches first. Turning a sentinel
        // into a static selector moves nothing and leaves only the disagreement.
        byte[] bytes = Build();
        int roti = Locate(bytes, "ROTI");
        bytes[roti + 6] = 0x00;
        bytes[roti + 7] = 0x80;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("DROT holds 0 values and ROTI selects 1", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_says_whether_a_hierarchy_follows()
    {
        // Not the tag: version 14 carries a flag byte, and before 14 a hierarchy
        // is always present. Clearing the flag leaves PRNT where DTRA should be.
        byte[] bytes = Build();
        bytes[0x40] = 0;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("DTRA", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_whose_channel_has_moved_away_from_its_header_refuses_to_write()
    {
        // The header is never quietly recomputed: a count restated in two places
        // is what went stale four times in one day on the MMB.
        AnimDocument document = Read(Build());
        AnimChannelBlock block = document.Channels[0];
        AnimDocument grown = document with
        {
            Channels = document.Channels.SetItem(0, block with { Changes = block.Changes.Add(2) }),
        };

        Result<byte[]> written = AnimWriter.Write(grown);
        Assert.False(written.IsSuccess);
        Assert.Contains("entries and the channel holds", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_channel_holding_the_wrong_number_of_entries_refuses()
    {
        // A flat channel is every animated channel at every sample, so its entry
        // count is fixed by the header's own sample count. Raising the sample
        // count leaves every chunk where it was and only that relation broken.
        byte[] bytes = Build(translation: Flat(animated: 2, samples: 3, stride: 8));
        bytes[0x10] = 4;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("stored flat with 6 entries", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_document_that_has_lost_its_hierarchy_refuses_to_write()
    {
        AnimDocument document = Read(Build());
        AnimDocument orphaned = document with { Parents = [] };

        Result<byte[]> written = AnimWriter.Write(orphaned);
        Assert.False(written.IsSuccess);
        Assert.Contains("no hierarchy", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_byte_past_the_tail_refuses_rather_than_being_ignored()
    {
        // What a reader that stops early looks like from the outside, and the
        // only check that can tell it from a correct one.
        byte[] bytes = [.. Build(), 0];

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("account for", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_this_build_has_no_header_length_for_refuses()
    {
        byte[] bytes = Build();
        bytes[4] = 11;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
    }

    [Fact]
    public void A_document_whose_selectors_disagree_with_its_names_refuses_to_write()
    {
        // The scale channel, whose selectors are both sentinels: dropping one
        // shortens the stream without changing how many channels are animated,
        // so this reaches the length check rather than the offset table's.
        AnimDocument document = Read(Build());
        AnimChannelBlock block = document.Channels[2];
        AnimDocument shortened = document with
        {
            Channels = document.Channels.SetItem(2, block with { Selectors = block.Selectors.RemoveAt(0) }),
        };

        Result<byte[]> written = AnimWriter.Write(shortened);
        Assert.False(written.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, written.Refusal.Kind);
        Assert.Contains("selectors against", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_offset_table_that_does_not_end_at_its_change_count_refuses_to_read()
    {
        // The change count is derived from where CHAK sits, and the offset table
        // restates it. A file where the two disagree is one whose values this
        // does not know the length of, so it refuses rather than reading on.
        byte[] bytes = Build();
        // The translation channel animates both nodes, so its offset table holds
        // three entries and the last of them is the change count.
        int caks = Locate(bytes, "CAKS");
        bytes[caks + 4 + (2 * 2)] = 9;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("offset table", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Where a tag first sits, so a fixture can be corrupted at one.</summary>
    private static int Locate(byte[] bytes, string tag)
    {
        int at = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(tag));
        Assert.True(at >= 0, $"the fixture has no {tag} chunk");
        return at;
    }

    [Fact]
    public void A_document_whose_offset_table_does_not_end_at_its_change_count_refuses_to_write()
    {
        // The one relation a reader checks and a writer must not paper over: the
        // offset table's last entry is how many changes there are.
        AnimDocument document = Read(Build());
        AnimChannelBlock block = document.Channels[0];
        AnimDocument wrong = document with
        {
            Channels = document.Channels.SetItem(0, block with { Offsets = block.Offsets.SetItem(block.Offsets.Length - 1, 7) }),
        };

        Result<byte[]> written = AnimWriter.Write(wrong);
        Assert.False(written.IsSuccess);
        Assert.Contains("offset table", written.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_declaring_more_nodes_than_the_file_holds_refuses_rather_than_allocating_them()
    {
        byte[] bytes = Build();
        bytes[0x24] = 0xFF;
        bytes[0x25] = 0xFF;
        bytes[0x26] = 0xFF;
        bytes[0x27] = 0x7F;

        Refusal refusal = Refuse(bytes);
        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void An_appended_node_moves_every_count_that_says_how_many_there_are()
    {
        AnimDocument document = Read(Build());
        uint before = Word(document, 0x14);

        AnimDocument grown = Append(document, "newJoint", parent: 0);

        Assert.Equal(3, grown.NodeCount);
        Assert.Equal(3u, Word(grown, 0x24));
        Assert.Equal(["root", "hand", "newJoint"], grown.Names);
        Assert.Equal(3, grown.Types.Length);
        Assert.Equal(AnimDocument.DefaultType, grown.Types[^1]);
        Assert.Equal([0xFFFF, 0, 0], grown.Parents);
        Assert.All(grown.Channels, channel =>
        {
            Assert.Equal(3, channel.Selectors.Length);
            Assert.Equal(AnimDocument.Silent, channel.Selectors[^1]);
        });

        // Its parent entry, one selector in each of three streams, its type byte,
        // and its name with the NUL that ends it.
        Assert.Equal(before + 2 + 6 + 1 + 9, Word(grown, 0x14));
    }

    [Fact]
    public void An_appended_node_writes_a_file_that_reads_back()
    {
        // The strongest check available without a game: the reader takes every
        // length from the header and requires the chunks to account for the file
        // exactly, so a file it accepts is one whose counts all agree.
        AnimDocument grown = Append(Read(Build()), "newJoint", parent: 1);

        Result<byte[]> written = AnimWriter.Write(grown);
        Assert.True(written.IsSuccess, written.IsSuccess ? string.Empty : written.Refusal.Message);

        // Compared by writing the re-read document rather than by value: an
        // ImmutableArray compares by reference, so a record holding eight of them
        // would report two identical documents as different.
        AnimDocument reread = Read(written.Value);
        Assert.Equal(["root", "hand", "newJoint"], reread.Names);
        Assert.Equal(written.Value, AnimWriter.Write(reread).Value);
    }

    [Fact]
    public void The_tail_bit_array_grows_when_the_node_count_crosses_a_byte()
    {
        AnimDocument document = Read(Build());
        Assert.Single(document.NodeBits);

        for (int i = 0; i < 6; i++)
        {
            document = Append(document, $"joint{i}", parent: 0);
        }

        Assert.Equal(8, document.NodeCount);
        Assert.Single(document.NodeBits);

        document = Append(document, "ninth", parent: 0);
        Assert.Equal(2, document.NodeBits.Length);

        Result<byte[]> written = AnimWriter.Write(document);
        Assert.True(written.IsSuccess, written.IsSuccess ? string.Empty : written.Refusal.Message);

        AnimDocument reread = Read(written.Value);
        Assert.Equal(9, reread.NodeCount);
        Assert.Equal(2, reread.NodeBits.Length);
        Assert.Equal(written.Value, AnimWriter.Write(reread).Value);
    }

    [Fact]
    public void A_node_whose_name_is_already_taken_refuses()
    {
        Result<AnimDocument> grown = Read(Build()).WithAppendedNode("hand", parent: 0);

        Assert.False(grown.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, grown.Refusal.Kind);
        Assert.Contains("already declares", grown.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_that_would_be_its_own_parent_refuses()
    {
        Result<AnimDocument> grown = Read(Build()).WithAppendedNode("newJoint", parent: 2);

        Assert.False(grown.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, grown.Refusal.Kind);
    }

    private static AnimDocument Append(AnimDocument document, string name, int parent)
    {
        Result<AnimDocument> grown = document.WithAppendedNode(name, parent);
        Assert.True(grown.IsSuccess, grown.IsSuccess ? string.Empty : grown.Refusal.Message);
        return grown.Value;
    }

    private static uint Word(AnimDocument document, int at) =>
        (uint)(document.Header[at]
            | (document.Header[at + 1] << 8)
            | (document.Header[at + 2] << 16)
            | (document.Header[at + 3] << 24));

    private static AnimDocument Read(byte[] bytes)
    {
        Result<AnimDocument> result = AnimReader.ReadDocument(SourceFile.FromMemory("invented.anim", bytes));
        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : result.Refusal.Message);
        return result.Value;
    }

    private static Refusal Refuse(byte[] bytes)
    {
        Result<AnimDocument> result = AnimReader.ReadDocument(SourceFile.FromMemory("invented.anim", bytes));
        Assert.False(result.IsSuccess);
        return result.Refusal;
    }

    /// <summary>A channel stored densely: values, and no change table at all.</summary>
    private static byte[] Flat(int animated, int samples, int stride) => new byte[animated * samples * stride];

    /// <summary>
    /// A two-node animation with all three channels compressed, unless a caller
    /// replaces one.
    /// </summary>
    /// <remarks>
    /// The header's counts are computed here rather than written out by hand,
    /// because the reader takes every chunk's length from them and checks each
    /// against the stream it describes. A fixture with a header that disagrees
    /// with its own body is not a simpler fixture; it is an invalid file.
    /// </remarks>
    private static byte[] Build(
        byte[]? translation = null,
        byte[]? scale = null,
        ushort[]? scaleSelectors = null,
        byte[]? scaleStatics = null)
    {
        const int nodes = 2;
        const int samples = 3;

        ushort[][] selectors = [[0, 1], [0, 0xFFFF], scaleSelectors ?? [0xFFFF, 0xFFFF]];
        byte[]?[] flat = [translation, null, scale];
        int[] stride = [8, 3, 8];
        byte[][] statics = [[], [], scaleStatics ?? []];

        int[] animated = new int[3];
        int[] staticCount = new int[3];
        int[] entries = new int[3];
        for (int channel = 0; channel < 3; channel++)
        {
            foreach (ushort selector in selectors[channel])
            {
                if (selector < 0x8000)
                {
                    animated[channel]++;
                }
                else if (selector < 0xFFFE)
                {
                    staticCount[channel]++;
                }
            }

            // Compressed channels here carry one change per animated channel, so
            // the value array holds two entries each.
            entries[channel] = flat[channel] is null
                ? animated[channel] * 2
                : animated[channel] * samples;
        }

        List<byte> bytes = [];
        bytes.AddRange("ANIM"u8);
        AddU32(bytes, 0x0003000e);
        AddU32(bytes, 0x41C00000);          // 24 fps
        AddU32(bytes, 0x3E2AAAAB);          // and its reciprocal
        AddU32(bytes, samples);
        AddU32(bytes, 0);                   // the loader's working-buffer size
        AddU32(bytes, 2);
        AddU32(bytes, 5);                   // three-byte packed rotations
        AddU32(bytes, 2);
        AddU32(bytes, nodes);
        foreach (int count in animated)
        {
            AddU32(bytes, (uint)count);
        }

        foreach (int count in entries)
        {
            AddU32(bytes, (uint)count);
        }

        bytes.Add(1);                       // a PRNT chunk follows
        foreach (int count in staticCount)
        {
            AddU32(bytes, (uint)count);
        }

        AddU32(bytes, 0);
        Assert.Equal(0x51, bytes.Count);

        Chunk(bytes, "TYPE", [5, 5]);
        Chunk(bytes, "PRNT", U16(0xFFFF, 0));

        for (int channel = 0; channel < 3; channel++)
        {
            Chunk(bytes, StaticTags[channel], statics[channel]);
        }

        for (int channel = 0; channel < 3; channel++)
        {
            Chunk(bytes, SelectorTags[channel], U16(selectors[channel]));
            Chunk(bytes, ValueTags[channel], flat[channel] ?? new byte[entries[channel] * stride[channel]]);
            if (flat[channel] is not null)
            {
                continue;
            }

            int changes = entries[channel] - animated[channel];
            Chunk(bytes, "CHAK", U16([.. Enumerable.Repeat((ushort)1, changes)]));
            Chunk(bytes, "CAKS", U16([.. Enumerable.Range(0, animated[channel] + 1).Select(i => (ushort)i)]));
        }

        Chunk(bytes, "NAME", [.. Encoding.Latin1.GetBytes("root"), 0, .. Encoding.Latin1.GetBytes("hand"), 0]);
        foreach (string tag in new[] { "PART", "IKEF", "IKEA" })
        {
            Chunk(bytes, tag, U32(0));
        }

        byte[] path = [.. Encoding.Latin1.GetBytes("invented/anm_test.anim"), 0];
        AddU32(bytes, (uint)path.Length);
        bytes.AddRange(path);

        bytes.Add(0);
        AddU32(bytes, 1);
        AddU32(bytes, 0xFFFFFFFF);
        AddU32(bytes, nodes);
        AddU32(bytes, 1);
        bytes.Add(0b11);

        return [.. bytes];
    }

    private static readonly string[] StaticTags = ["DTRA", "DROT", "DSCA"];
    private static readonly string[] SelectorTags = ["TRAI", "ROTI", "SCAI"];
    private static readonly string[] ValueTags = ["TRAD", "ROTD", "SCAD"];

    private static void Chunk(List<byte> bytes, string tag, byte[] payload)
    {
        bytes.AddRange(Encoding.ASCII.GetBytes(tag));
        bytes.AddRange(payload);
    }

    private static byte[] U16(params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)values[i];
            bytes[(i * 2) + 1] = (byte)(values[i] >> 8);
        }

        return bytes;
    }

    private static byte[] U32(uint value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    private static void AddU32(List<byte> bytes, uint value) => bytes.AddRange(U32(value));

    private static void AddU32(List<byte> bytes, int value) => bytes.AddRange(U32((uint)value));
}
