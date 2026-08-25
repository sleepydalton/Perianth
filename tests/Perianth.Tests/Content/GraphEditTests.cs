using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Formats.Binary;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Pointing a graph object at different assets.
/// </summary>
/// <remarks>
/// The operation is a string-table substitution, so what these assert is mostly
/// that the <em>graph</em> is untouched: a string's content changes while its
/// index does not, and that is the whole reason making a new graph object needs
/// no graph editing.
/// </remarks>
public sealed class GraphEditTests
{
    /// <summary>
    /// A container whose table holds a model, an animation system, a shader and
    /// two entries that are not paths — one of them the bare asset name that
    /// sits beside the model in every shipped actor.
    /// </summary>
    private static readonly string[] Table =
    [
        "MMAFile",
        "made/up/model.mmb",
        "made/up/anim.manimsys",
        "made/up/shader.mshader",
        "made_up_asset",
    ];

    private static SourceFile Graph(params byte[] graph)
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        CompactInteger.Write(bytes, (uint)Table.Length);
        foreach (string text in Table)
        {
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(text);
            CompactInteger.Write(bytes, (uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        bytes.AddRange(graph);
        return SourceFile.FromMemory("made-up.mgraphobject", bytes.ToArray());
    }

    /// <summary>
    /// One container: an array of the five string references, and a map pair
    /// keyed by a string, so both places a reference can sit are exercised.
    /// </summary>
    private static SourceFile Actor() => Graph(
        0x01, 0x05, 0x01,
        0x0d, 0x00, 0x0d, 0x01, 0x0d, 0x01, 0x0e, 0x02, 0x0d, 0x04,
        0x0d, 0x03, 0x02);

    [Fact]
    public void Every_entry_is_listed_with_how_often_the_graph_uses_it()
    {
        ImmutableArray<GraphString> strings = GraphEdit.List(Actor()).Value;

        Assert.Equal(5, strings.Length);
        Assert.Equal("made/up/model.mmb", strings[1].Value);
        Assert.Equal(2, strings[1].Uses);
        Assert.Equal(1, strings[3].Uses);
    }

    [Fact]
    public void Repointing_changes_the_entry_and_leaves_the_graph_alone()
    {
        SourceFile before = Actor();
        GraphEdited edited = GraphEdit.Repoint(
            before, [("made/up/model.mmb", "brand/new/model.mmb")]).Value;

        BvmDocument document = BvmReader.ReadDocument(
            SourceFile.FromMemory("made-up.mgraphobject", edited.Bytes.ToArray())).Value;

        Assert.Equal("brand/new/model.mmb", document.Strings[1]);

        // The graph is the same tree, referencing the same indices. That is what
        // makes this a substitution rather than an edit.
        BvmDocument was = BvmReader.ReadDocument(before).Value;
        Assert.Equal(Indices(was.Graph), Indices(document.Graph));
        Assert.Equal(was.Strings.Length, document.Strings.Length);
    }

    [Fact]
    public void The_entries_not_named_are_carried_over_exactly()
    {
        GraphEdited edited = GraphEdit.Repoint(
            Actor(), [("made/up/model.mmb", "brand/new/model.mmb")]).Value;

        BvmDocument document = BvmReader.ReadDocument(
            SourceFile.FromMemory("made-up.mgraphobject", edited.Bytes.ToArray())).Value;

        Assert.Equal("MMAFile", document.Strings[0]);
        Assert.Equal("made/up/anim.manimsys", document.Strings[2]);
        Assert.Equal("made_up_asset", document.Strings[4]);
    }

    [Fact]
    public void A_whole_entry_is_matched_and_never_a_substring()
    {
        // The table holds bare names beside paths — an actor's assetname sits
        // next to its .mmb — so a substring rule would rewrite a name while
        // meaning to rewrite a path.
        Result<GraphEdited> result = GraphEdit.Repoint(Actor(), [("made/up", "brand/new")]);

        Assert.False(result.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_move_that_matched_nothing_is_refused()
    {
        // A mistyped path that quietly wrote an unchanged file would produce a
        // mod indistinguishable from a working one.
        Result<GraphEdited> result = GraphEdit.Repoint(
            Actor(), [("made/up/model.mmb", "a"), ("no/such/path.mmb", "b")]);

        Assert.False(result.IsSuccess);
        Assert.Contains("no/such/path.mmb", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_moves_all_apply()
    {
        GraphEdited edited = GraphEdit.Repoint(Actor(),
        [
            ("made/up/model.mmb", "brand/new/model.mmb"),
            ("made/up/anim.manimsys", "brand/new/anim.manimsys"),
        ]).Value;

        BvmDocument document = BvmReader.ReadDocument(
            SourceFile.FromMemory("made-up.mgraphobject", edited.Bytes.ToArray())).Value;

        Assert.Equal(2, edited.Repointed.Length);
        Assert.Equal("brand/new/model.mmb", document.Strings[1]);
        Assert.Equal("brand/new/anim.manimsys", document.Strings[2]);
    }

    [Fact]
    public void Repointing_nothing_is_refused() =>
        Assert.False(GraphEdit.Repoint(Actor(), []).IsSuccess);

    [Fact]
    public void The_sole_entry_of_an_extension_is_found()
    {
        Assert.Equal("made/up/model.mmb", GraphEdit.Sole(Actor(), ".mmb").Value);
        Assert.Equal("made/up/anim.manimsys", GraphEdit.Sole(Actor(), ".manimsys").Value);
    }

    [Fact]
    public void An_extension_the_graph_does_not_name_is_refused()
    {
        Result<string> result = GraphEdit.Sole(Actor(), ".editordata");

        Assert.False(result.IsSuccess);
        Assert.Contains("names no", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_entries_of_one_extension_refuse_rather_than_choosing()
    {
        // Eleven shipped actors name two models. Picking either would be a coin
        // toss producing a character that draws half of what was meant.
        SourceFile two = Graph(
            0x01, 0x02, 0x00, 0x0d, 0x01, 0x0d, 0x03);

        Result<string> result = GraphEdit.Sole(
            SourceFile.FromMemory("made-up.mgraphobject", Rename(two, 3, "made/up/second.mmb")), ".mmb");

        Assert.False(result.IsSuccess);
        Assert.Contains("would be a guess", result.Refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Rewrites one table entry, to build a fixture the tables cannot.</summary>
    private static byte[] Rename(SourceFile file, int index, string value)
    {
        BvmDocument document = BvmReader.ReadDocument(file).Value;
        string[] table = [.. document.Strings];
        table[index] = value;
        return BvmWriter.Write(new BvmDocument([.. table], document.Graph)).Value;
    }

    /// <summary>Every string index the graph references, in order.</summary>
    private static ImmutableArray<int> Indices(BvmValue value)
    {
        List<int> found = [];
        Walk(value, found);
        return [.. found];
    }

    private static void Walk(BvmValue value, List<int> found)
    {
        if (value is BvmString reference)
        {
            found.Add(reference.Index);
        }
        else if (value is BvmContainer container)
        {
            foreach (BvmValue item in container.Items)
            {
                Walk(item, found);
            }

            foreach (BvmPair pair in container.Entries)
            {
                Walk(pair.Key, found);
                Walk(pair.Value, found);
            }
        }
    }
}
