using System;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using Perianth.Core.Content;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Tests.Cameldata;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Adding a part across the three files it lives in.
/// </summary>
/// <remarks>
/// <para>
/// The pairing operation. A part is paired with its coordinates and its material
/// <b>by ordinal and by nothing else</b>, so adding one is a single operation
/// over three files rather than three operations — and the reason this type
/// exists is that a caller doing half of it produces a model that loads and
/// draws wrongly.
/// </para>
/// <para>
/// Byte identity cannot be the oracle here: the whole point is a file that is
/// larger than it was. The substitute is the property that makes a duplicate
/// meaningful — <b>the copy owns what the original owns</b>, every earlier part
/// is untouched, and the three files still agree on how many parts there are.
/// </para>
/// </remarks>
public sealed class PartAddTests
{
    [Fact]
    public void The_three_files_still_agree_on_the_count()
    {
        // The invariant every other stage assumes. An operation that grew two
        // files and forgot the third would pass every geometry test here and
        // refuse at material assembly, a long way from the cause.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));

        Assert.Equal(3, grown.Model.Parts.Length);
        Assert.Equal(3, grown.Cameldata.Constants.Length);
        Assert.Equal(grown.Model.Parts.Length, grown.Cameldata.Constants.Length);
        Assert.Equal(2, grown.Ordinal);
    }

    [Fact]
    public void The_copy_owns_the_coordinates_the_original_owns()
    {
        // The property that makes a duplicate a duplicate, and the one a base
        // written from the wrong cursor breaks. Read through the same slice
        // derivation every other stage uses rather than by trusting the bases.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));

        AssertSameSlice(SliceOf(cameldata, 0), SliceOf(grown.Cameldata, 2));
    }

    [Fact]
    public void Copying_the_last_part_reads_its_slice_to_the_pool_end()
    {
        // The last record's slice is derived from the pool length rather than
        // from a following base, so it is the case an off-by-one in the slice
        // derivation gets wrong while every earlier record still looks right.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 1, "joint|copy"));

        AssertSameSlice(SliceOf(cameldata, 1), SliceOf(grown.Cameldata, 2));
    }

    [Fact]
    public void Every_part_that_was_there_reads_what_it_read()
    {
        // The silent failure this whole area guards: a file that loads while an
        // earlier part quietly reads the new part's coordinates.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));

        for (int record = 0; record < cameldata.Constants.Length; record++)
        {
            AssertSameSlice(SliceOf(cameldata, record), SliceOf(grown.Cameldata, record));
            Assert.Equal(model.Parts[record].SourceOrdinal, grown.Model.Parts[record].SourceOrdinal);
            Assert.Equal(model.Parts[record].Label, grown.Model.Parts[record].Label);
        }
    }

    [Fact]
    public void The_copy_takes_the_next_ordinal_in_both_files()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));

        Assert.Equal([0, 1, 2], grown.Model.Parts.Select(p => p.SourceOrdinal));
        Assert.Equal(grown.Ordinal, grown.Model.Parts[^1].SourceOrdinal);
    }

    [Fact]
    public void The_copy_takes_the_label_it_was_given()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));

        Assert.Equal("joint|copy", grown.Model.Parts[^1].Label);
        Assert.Equal("joint|shape1", grown.Model.Parts[0].Label);
    }

    [Fact]
    public void Adding_twice_keeps_the_files_in_step()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome once = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|a"));
        PartAddOutcome twice = Ok(PartAdd.Duplicate(
            once.Model, once.Cameldata, 1, "joint|b"));

        Assert.Equal(4, twice.Model.Parts.Length);
        Assert.Equal(4, twice.Cameldata.Constants.Length);
        Assert.Equal([0, 1, 2, 3], twice.Model.Parts.Select(p => p.SourceOrdinal));
        AssertSameSlice(SliceOf(cameldata, 0), SliceOf(twice.Cameldata, 2));
        AssertSameSlice(SliceOf(cameldata, 1), SliceOf(twice.Cameldata, 3));
    }

    [Fact]
    public void A_grown_pair_writes_back_and_reads_as_what_was_built()
    {
        // Both writers must accept what this produced. Neither was changed for
        // it, which is a claim rather than a fact until something checks.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        PartAddOutcome grown = Ok(PartAdd.Duplicate(model, cameldata, 0, "joint|copy"));
        MmbModel reread = ReadModel(OkBytes(MmbContainerWriter.Write(grown.Model)));
        Mode3Cameldata rereadPool = ReadPool(OkBytes(CameldataWriter.Write(grown.Cameldata)));

        Assert.Equal(3, reread.Parts.Length);
        Assert.Equal(3, rereadPool.Constants.Length);
        AssertSameSlice(SliceOf(grown.Cameldata, 2), SliceOf(rereadPool, 2));
        Assert.Equal("joint|copy", reread.Parts[^1].Label);
    }

    [Fact]
    public void A_model_and_a_cameldata_that_already_disagree_refuse()
    {
        // Appending to both would preserve the disagreement while looking like
        // an edit that worked.
        (MmbModel model, _) = Pair();
        Mode3Cameldata odd = ReadPool(new CameldataBuilder
        {
            Mode = 3,
            ConstantCount = 1,
            Xy = [new(1, 1)],
            Z = [10f],
            Uv0 = [0],
            PackedZ = [0u],
        }.Build());

        Assert.Contains("pair by ordinal", Refused(
            PartAdd.Duplicate(model, odd, 0, "joint|copy")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_part_that_is_not_there_refuses()
    {
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Assert.Contains("was asked for", Refused(
            PartAdd.Duplicate(model, cameldata, 7, "joint|copy")).Message,
            StringComparison.Ordinal);
        Assert.False(PartAdd.Duplicate(model, cameldata, -1, "joint|copy").IsSuccess);
    }

    [Fact]
    public void A_label_binding_to_an_undeclared_node_refuses_before_anything_is_built()
    {
        // The commonest mistake, and it must not leave a half-grown pair behind.
        // Nothing here mutates, so the check is that the originals are intact.
        (MmbModel model, Mode3Cameldata cameldata) = Pair();

        Assert.False(PartAdd.Duplicate(model, cameldata, 0, "nowhere|copy").IsSuccess);

        Assert.Equal(2, model.Parts.Length);
        Assert.Equal(2, cameldata.Constants.Length);
    }

    /// <summary>
    /// Asserts two slices hold the same values.
    /// </summary>
    /// <remarks>
    /// Componentwise, because <c>ImmutableArray</c> compares by reference inside
    /// a tuple: asserting on the tuples reports "Expected X, Actual X" and
    /// fails, which reads as a mystery rather than as the wrong comparer.
    /// </remarks>
    private static void AssertSameSlice(
        (ImmutableArray<Vector2> Xy, ImmutableArray<float> Z, ImmutableArray<uint> Indices) expected,
        (ImmutableArray<Vector2> Xy, ImmutableArray<float> Z, ImmutableArray<uint> Indices) actual)
    {
        Assert.Equal(expected.Xy, actual.Xy);
        Assert.Equal(expected.Z, actual.Z);
        Assert.Equal(expected.Indices, actual.Indices);
    }

    /// <summary>Everything one record owns, as values, for comparing two files.</summary>
    private static (ImmutableArray<Vector2> Xy, ImmutableArray<float> Z, ImmutableArray<uint> Indices)
        SliceOf(Mode3Cameldata file, int record)
    {
        Mode3Constant constant = file.Constants[record];
        int xyStart = (int)constant.XyBase;
        int xyEnd = record + 1 < file.Constants.Length
            ? (int)file.Constants[record + 1].XyBase
            : file.Xy.Length;
        int zStart = (int)constant.ZBase;
        int zEnd = record + 1 < file.Constants.Length
            ? (int)file.Constants[record + 1].ZBase
            : file.Z.Length;

        int width = constant.ZBitWidth;
        ImmutableArray<uint>.Builder indices = ImmutableArray.CreateBuilder<uint>();
        for (int slot = xyStart; slot < xyEnd; slot++)
        {
            uint value = 0;
            for (int bit = 0; bit < width; bit++)
            {
                long at = ((long)slot * width) + bit;
                value |= ((file.PackedZ[(int)(at / 32)] >> (int)(at % 32)) & 1u) << bit;
            }

            indices.Add(value);
        }

        return ([.. file.Xy[xyStart..xyEnd]], [.. file.Z[zStart..zEnd]], indices.ToImmutable());
    }

    /// <summary>A two-part model and the two-constant cameldata paired with it.</summary>
    private static (MmbModel Model, Mode3Cameldata Cameldata) Pair()
    {
        MmbModel model = ReadModel(new MmbFileBuilder
        {
            Repeat = 2,
            Label = "joint|shape1",
            NodeNames = ["joint"],
            PositionEntries = [0, 1, 2],
            EntrySize = sizeof(ushort),
            VertexCount = 3,
        }.Build());

        // Three vertices a record, because a direct part's vertex count is a
        // whole number of triangles and the reader says so.
        Mode3Cameldata cameldata = ReadPool(new CameldataBuilder
        {
            Mode = 3,
            ConstantCount = 2,
            XyBases = [0, 3],
            ZBases = [0, 2],
            Xy = [new(1, 1), new(2, 2), new(3, 3), new(4, 4), new(5, 5), new(6, 6)],

            // Two depths each, so the packed indices vary. With one apiece every
            // index would have to be zero, and a test where the only legal value
            // is zero cannot show that the indices were carried across at all.
            Z = [10f, 20f, 30f, 40f],
            Uv0 = [0, 0, 0, 0, 0, 0],
            PackedZ = [0b010110u],
        }.Build());

        return (model, cameldata);
    }

    private static MmbModel ReadModel(byte[] bytes)
    {
        Result<MmbModel> read = MmbReader.Read(new SourceFile("test.mmb", bytes));
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal.Message);
        return read.Value;
    }

    private static Mode3Cameldata ReadPool(byte[] bytes)
    {
        Result<CameldataFile> read = CameldataReader.Read(
            new SourceFile("test.cameldata", bytes));
        Assert.True(read.IsSuccess, read.IsSuccess ? "" : read.Refusal.Message);
        return Assert.IsType<Mode3Cameldata>(read.Value);
    }

    private static PartAddOutcome Ok(Result<PartAddOutcome> result)
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
