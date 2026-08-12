using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Bvm;

/// <summary>
/// The BVM string table, against synthetic containers.
/// </summary>
/// <remarks>
/// Every string here is invented. A real animation system's table carries the
/// game's asset paths and, for a character, its spoken lines — so a fixture cut
/// from one would bring both into this repository. The grammar is what these
/// exercise, and the grammar does not care what the strings say.
/// </remarks>
public sealed class BvmReaderTests
{
    [Fact]
    public void Reads_a_table_of_strings_in_file_order()
    {
        BvmFile file = Read(Container("first", "second", "third"));

        Assert.Equal(["first", "second", "third"], file.Strings);
    }

    [Fact]
    public void Keeps_an_empty_string_rather_than_dropping_it()
    {
        // An absent bone name is written as an empty string and is meaningful:
        // dropping it would renumber every entry after it, and the graph
        // addresses entries by ordinal.
        BvmFile file = Read(Container("before", "", "after"));

        Assert.Equal(["before", "", "after"], file.Strings);
        Assert.Equal(3, file.Strings.Length);
    }

    [Theory]
    [InlineData(63)]    // the last length a plain byte read gets right
    [InlineData(64)]    // the first it gets wrong
    [InlineData(65)]
    [InlineData(300)]   // wide enough to need the three-byte form
    [InlineData(20000)]
    public void Reads_a_string_whose_length_needs_the_wider_integer(int length)
    {
        string value = new('x', length);

        BvmFile file = Read(Container("before", value, "after"));

        // The point is the neighbours as much as the value: a mis-sized length
        // reads this string correctly and desynchronises everything after it.
        Assert.Equal(["before", value, "after"], file.Strings);
    }

    [Fact]
    public void Refuses_a_file_that_is_not_a_container()
    {
        Refusal refusal = Failure(SourceFile.FromMemory("x.manimsys", new byte[] { 0xFF, (byte)'B', (byte)'V', (byte)'X', 0x00, 0x01 }));

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
    }

    [Fact]
    public void Refuses_a_table_that_does_not_end_at_the_graph()
    {
        // One byte inserted before the graph tag: every string still reads, and
        // the file is still wrong. Without the tag check this passes silently,
        // which is the failure this guard exists for.
        List<byte> bytes = [.. Container("only")];
        bytes.Insert(bytes.Count - 1, 0x00);

        Refusal refusal = Failure(SourceFile.FromMemory("x.manimsys", bytes.ToArray()));

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("without reaching the graph", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_count_larger_than_the_file()
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        bytes.AddRange(Compact(1_000_000));

        Refusal refusal = Failure(SourceFile.FromMemory("x.manimsys", bytes.ToArray()));

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("more than its", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_string_that_is_not_valid_utf8()
    {
        // A path repaired into replacement characters names a file that does not
        // exist, and would be indistinguishable from one that does.
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        bytes.AddRange(Compact(1));
        bytes.AddRange(Compact(2));
        bytes.AddRange([0xC3, 0x28]);
        bytes.Add(0x01);

        Refusal refusal = Failure(SourceFile.FromMemory("x.manimsys", bytes.ToArray()));

        Assert.Equal(RefusalKind.Malformed, refusal.Kind);
        Assert.Contains("valid UTF-8", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reports_the_graph_as_the_remainder_of_the_file()
    {
        byte[] table = Container("one");
        int tag = table.Length - 1;           // Container ends with the graph tag
        List<byte> bytes = [.. table, 0x00, 0x02, 0x0D];   // arbitrary unread graph bytes

        BvmFile file = Read([.. bytes]);

        // The graph begins AT the tag, not after it: the tag is the first byte of
        // the root container, and a later parser needs it.
        Assert.Equal(tag, file.Graph.Offset);
        Assert.Equal(bytes.Count, file.Graph.End);
        Assert.Equal(bytes.Count - tag, file.Graph.Length);
    }

    private static BvmFile Read(byte[] bytes)
    {
        Result<BvmFile> result = BvmReader.Read(SourceFile.FromMemory("x.manimsys", bytes));
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal!.Message);
        return result.Value;
    }

    private static Refusal Failure(SourceFile file)
    {
        Result<BvmFile> result = BvmReader.Read(file);
        Assert.False(result.IsSuccess);
        return result.Refusal!;
    }

    /// <summary>A container holding exactly these strings, then one graph tag.</summary>
    private static byte[] Container(params string[] strings)
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        bytes.AddRange(Compact(strings.Length));
        foreach (string s in strings)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(s);
            bytes.AddRange(Compact(utf8.Length));
            bytes.AddRange(utf8);
        }

        bytes.Add(0x01);
        return [.. bytes];
    }

    /// <summary>
    /// The encoder for the reader's integer, written out longhand.
    /// </summary>
    /// <remarks>
    /// Deliberately not the reader's own code inverted: a fixture built by the
    /// implementation under test agrees with it however wrong both are. This is
    /// written from the format's description — six bits, then a width chosen by
    /// the top two bits.
    /// </remarks>
    private static IEnumerable<byte> Compact(int value)
    {
        ulong high = (ulong)value >> 6;
        int extra = high == 0 ? 0 : high <= byte.MaxValue ? 1 : high <= 0xFFFFFF ? 3 : 7;
        byte selector = (byte)(extra switch { 0 => 0, 1 => 1, 3 => 2, _ => 3 } << 6);

        yield return (byte)(selector | (value & 0x3F));
        for (int i = 0; i < extra; i++)
        {
            yield return (byte)(high >> (8 * i));
        }
    }
}
