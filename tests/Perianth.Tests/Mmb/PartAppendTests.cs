using System;
using System.Buffers.Binary;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Xunit;

namespace Perianth.Tests.Mmb;

/// <summary>
/// Giving a model a part it did not have.
/// </summary>
/// <remarks>
/// <para>
/// The second half of "add a part". The cameldata half appends a constant; this
/// appends the record paired with it, and the two are only correct together.
/// </para>
/// <para>
/// The field that matters most here is <see cref="MmbModelPart.SourceOrdinal"/>,
/// which reads like provenance and is not: it indexes the cameldata constant and
/// the editordata section this part is paired with. A part appended without
/// being renumbered draws another part's geometry with another part's material,
/// and does it in a file that loads.
/// </para>
/// </remarks>
public sealed class PartAppendTests
{
    [Fact]
    public void An_appended_part_takes_the_next_ordinal()
    {
        // The pairing key. Everything else in this file is a guard; this is the
        // operation's whole purpose.
        MmbModel model = Read(Two());

        MmbModel grown = Ok(model.WithAppendedPart(model.Parts[0]));

        Assert.Equal(3, grown.Parts.Length);
        Assert.Equal([0, 1, 2], grown.Parts.Select(p => p.SourceOrdinal));
    }

    [Fact]
    public void An_appended_part_writes_1_point_0_into_the_field_at_0xa0()
    {
        // Settled by decision, Roadmap §10.78: the float is computed rather than
        // defaulted, and 1.0 is a common legal value, never reaches the GPU, and
        // is the identity or the conservative choice under every surviving
        // reading. A part carried from a template would otherwise inherit a
        // value describing a shape it no longer is.
        MmbModel model = Read(Two());
        Assert.NotEqual(1f, TailField(model.Parts[0]));

        MmbModel grown = Ok(model.WithAppendedPart(model.Parts[0]));

        Assert.Equal(1f, TailField(grown.Parts[^1]));
        Assert.Equal(TailField(model.Parts[0]), TailField(grown.Parts[0]));
    }

    [Fact]
    public void An_appended_part_keeps_everything_that_makes_the_record_legal()
    {
        // A template carries declarations, flag bytes and LOD flags that are
        // constants of the format (Roadmap §10.67). None of them is this
        // operation's to invent, and a writer with opinions is the failure this
        // project is careful about.
        MmbModel model = Read(Two());
        MmbModelPart source = model.Parts[0];

        MmbModelPart added = Ok(model.WithAppendedPart(source)).Parts[^1];

        Assert.Equal(source.DeclarationCount, added.DeclarationCount);
        Assert.Equal(source.DeclarationBytes.ToArray(), added.DeclarationBytes.ToArray());
        Assert.Equal(source.FlagBytes.ToArray(), added.FlagBytes.ToArray());
        Assert.Equal(source.LodFlags, added.LodFlags);
        Assert.Equal(source.MatrixCount, added.MatrixCount);
        Assert.Equal(source.Payload.ToArray(), added.Payload.ToArray());
        Assert.Equal(source.Values, added.Values);
    }

    [Fact]
    public void The_parts_already_there_are_untouched()
    {
        MmbModel model = Read(Two());

        MmbModel grown = Ok(model.WithAppendedPart(model.Parts[0]));

        for (int part = 0; part < model.Parts.Length; part++)
        {
            Assert.Equal(model.Parts[part].SourceOrdinal, grown.Parts[part].SourceOrdinal);
            Assert.Equal(model.Parts[part].Label, grown.Parts[part].Label);
            Assert.Equal(
                model.Parts[part].TailBytes.ToArray(), grown.Parts[part].TailBytes.ToArray());
        }
    }

    [Fact]
    public void A_part_binding_to_a_node_the_model_does_not_declare_refuses()
    {
        // A part's label names a node of its own model on 64,103 of 64,103. One
        // naming a node that is not there would hang off nothing, which is a
        // request the data cannot satisfy rather than something to guess about.
        MmbModel model = Read(Two());
        MmbModelPart stranger = Ok(model.Parts[0].WithLabel("elsewhere|shape1"));

        Refusal refusal = Refused(model.WithAppendedPart(stranger));

        Assert.Equal(RefusalKind.Unsupported, refusal.Kind);
        Assert.Contains("elsewhere", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_label_with_no_bar_is_read_whole_as_the_binding()
    {
        // Most labels are a Maya path, and some are a bare name. Splitting on a
        // separator that is not there must not produce an empty binding.
        MmbModel model = Read(Two());

        Assert.True(model.WithAppendedPart(Ok(model.Parts[0].WithLabel("joint"))).IsSuccess);
        Assert.False(model.WithAppendedPart(Ok(model.Parts[0].WithLabel("other"))).IsSuccess);
    }

    [Fact]
    public void Only_the_first_segment_of_a_label_binds()
    {
        // The first segment on 64,103 of 64,103; the last matched once and the
        // whole string never. Reading the wrong segment would refuse every real
        // part, so this pins which one.
        MmbModel model = Read(Two());

        Assert.True(model.WithAppendedPart(
            Ok(model.Parts[0].WithLabel("joint|anything|at|all"))).IsSuccess);
        Assert.False(model.WithAppendedPart(
            Ok(model.Parts[0].WithLabel("anything|joint"))).IsSuccess);
    }

    [Fact]
    public void Appending_twice_numbers_both()
    {
        MmbModel model = Read(Two());

        MmbModel twice = Ok(Ok(model.WithAppendedPart(model.Parts[0]))
            .WithAppendedPart(model.Parts[1]));

        Assert.Equal([0, 1, 2, 3], twice.Parts.Select(p => p.SourceOrdinal));
    }

    [Fact]
    public void WithParts_still_refuses_a_changed_count()
    {
        // Growing a model is a different operation, not a relaxation of this
        // one, and every existing caller of WithParts means one-for-one.
        MmbModel model = Read(Two());

        Assert.False(model.WithParts([model.Parts[0]]).IsSuccess);
        Assert.False(model.WithParts([.. model.Parts, model.Parts[0]]).IsSuccess);
    }

    [Fact]
    public void A_label_that_is_not_ASCII_refuses()
    {
        MmbModel model = Read(Two());

        Assert.Contains("ASCII", Refused(model.Parts[0].WithLabel("joint|é")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_label_refuses()
    {
        MmbModel model = Read(Two());

        Assert.Contains("cannot be empty", Refused(model.Parts[0].WithLabel("")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_grown_model_writes_back_and_reads_as_what_was_built()
    {
        // The container writer derives the part count from the array and lays
        // payloads out in order, so it needs no change -- but "needs no change"
        // is a claim, and this is what makes it one that has been checked.
        MmbModel model = Read(Two());

        MmbModel grown = Ok(model.WithAppendedPart(model.Parts[0]));
        byte[] bytes = OkBytes(MmbContainerWriter.Write(grown));
        MmbModel reread = Read(bytes);

        Assert.Equal(3, reread.Parts.Length);
        Assert.Equal([0, 1, 2], reread.Parts.Select(p => p.SourceOrdinal));
        Assert.Equal(1f, TailField(reread.Parts[^1]));
        Assert.Equal(
            grown.Parts.Select(p => p.Payload.ToArray()),
            reread.Parts.Select(p => p.Payload.ToArray()));
    }

    [Fact]
    public void A_new_node_can_be_appended_and_a_part_bound_to_it()
    {
        // The rung's whole point: a part on a joint the model did not have.
        // Adding the node first is what makes the part's binding legal.
        MmbModel model = Read(Two());
        MmbModelPart onNewJoint = Ok(model.Parts[0].WithLabel("elbow|shape1"));
        Assert.False(model.WithAppendedPart(onNewJoint).IsSuccess);

        MmbModel withNode = Ok(model.WithAppendedNode("elbow", parent: 0));

        Assert.Equal(2, withNode.Nodes.Length);
        Assert.True(withNode.WithAppendedPart(onNewJoint).IsSuccess);
    }

    [Fact]
    public void A_new_node_is_identity_unless_one_is_given()
    {
        // Measured rather than assumed: 98.8% of shipped nodes are within 1e-6
        // of identity, because the hierarchy is the parent index and the pose
        // comes from the setup.
        MmbModel model = Read(Two());

        MmbNode added = Ok(model.WithAppendedNode("elbow", parent: 0)).Nodes[^1];

        Assert.Equal(64, added.MatrixBytes.Length);
        for (int cell = 0; cell < 16; cell++)
        {
            float value = BinaryPrimitives.ReadSingleLittleEndian(
                added.MatrixBytes.Span[(cell * 4)..]);
            Assert.Equal(cell % 5 == 0 ? 1f : 0f, value);
        }
    }

    [Fact]
    public void A_negative_parent_writes_a_root()
    {
        MmbModel model = Read(Two());

        Assert.Equal(MmbNode.NoParent, Ok(model.WithAppendedNode("top", -1)).Nodes[^1].Trailer);
        Assert.Equal(0, Ok(model.WithAppendedNode("elbow", 0)).Nodes[^1].Trailer);
    }

    [Fact]
    public void A_repeated_node_name_refuses()
    {
        // A part binds to a node by name, so two of them make the binding
        // ambiguous -- and every one of 2,283 models has unique names.
        MmbModel model = Read(Two());

        Assert.Contains("already declares", Refused(model.WithAppendedNode("joint", 0)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_parent_outside_the_table_refuses()
    {
        // The trailer is a parent index and is in range on all 6.58 million
        // nodes measured. A node cannot be its own parent either: the new one
        // would sit at Nodes.Length.
        MmbModel model = Read(Two());

        Assert.Contains("index into the table", Refused(model.WithAppendedNode("elbow", 1)).Message,
            StringComparison.Ordinal);
        Assert.Contains("index into the table", Refused(model.WithAppendedNode("elbow", 9)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_node_name_that_is_empty_or_not_ASCII_refuses()
    {
        MmbModel model = Read(Two());

        Assert.False(model.WithAppendedNode("", 0).IsSuccess);
        Assert.Contains("ASCII", Refused(model.WithAppendedNode("elbowé", 0)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_matrix_of_the_wrong_size_refuses()
    {
        MmbModel model = Read(Two());

        Assert.Contains("64 bytes", Refused(model.WithAppendedNode("elbow", 0, new byte[32])).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_with_a_new_node_writes_back_and_reads_as_what_was_built()
    {
        MmbModel model = Read(Two());

        MmbModel grown = Ok(model.WithAppendedNode("elbow", parent: 0));
        MmbModel reread = Read(OkBytes(MmbContainerWriter.Write(grown)));

        Assert.Equal(2, reread.Nodes.Length);
        Assert.Equal("elbow", System.Text.Encoding.ASCII.GetString(reread.Nodes[^1].NameBytes.Span));
        Assert.Equal(0, reread.Nodes[^1].Trailer);
        Assert.Equal(grown.Nodes[^1].MatrixBytes.ToArray(), reread.Nodes[^1].MatrixBytes.ToArray());
    }

    [Fact]
    public void A_parts_binding_node_is_its_labels_first_segment()
    {
        // One rule, one place. The window shows a part's binding node too, and a
        // second copy of the split is how the two come to disagree -- so the
        // rule lives on the part and this is what pins it.
        MmbModel model = Read(Two());
        MmbModelPart part = model.Parts[0];

        Assert.Equal("joint", part.BindingNode);
        Assert.Equal("joint", Ok(part.WithLabel("joint|a|b")).BindingNode);
        Assert.Equal("bare", Ok(part.WithLabel("bare")).BindingNode);
    }

    /// <summary>The float at part offset <c>+0xa0</c>: the tail's first word.</summary>
    private static float TailField(MmbModelPart part) =>
        BinaryPrimitives.ReadSingleLittleEndian(part.TailBytes.Span);

    /// <summary>Two parts, both binding to a node the model declares.</summary>
    private static MmbFileBuilder Two() => new()
    {
        Repeat = 2,
        Label = "joint|shape1",
        NodeNames = ["joint"],
        PositionEntries = [0, 1, 2],
        EntrySize = sizeof(ushort),
        VertexCount = 3,
    };

    private static MmbModel Read(MmbFileBuilder builder) => Read(builder.Build());

    private static MmbModel Read(byte[] bytes)
    {
        Result<MmbModel> read = MmbReader.Read(new SourceFile("test.mmb", bytes));
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal.Message);
        return read.Value;
    }

    private static MmbModel Ok(Result<MmbModel> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.Message);
        return result.Value;
    }

    private static MmbModelPart Ok(Result<MmbModelPart> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.Message);
        return result.Value;
    }

    private static byte[] OkBytes(Result<byte[]> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.Message);
        return result.Value;
    }

    private static Refusal Refused<T>(Result<T> result)
    {
        Assert.False(result.IsSuccess);
        return result.Refusal;
    }
}
