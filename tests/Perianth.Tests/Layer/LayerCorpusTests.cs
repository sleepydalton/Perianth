using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Layer;
using Xunit;

namespace Perianth.Tests.Layer;

/// <summary>
/// Reads every shipped map layer, writes it back, and places an entity in one.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_MAPS</c> names an extraction of
/// <c>camel/maps</c>, so the default suite stays asset-free.
/// </para>
/// <para>
/// Two claims, and the second is the one a span index needs. Byte identity says
/// the ranges agree with each other. It cannot say the <em>accounting</em> is
/// right, because a layer whose chunks were never checked against its body
/// rewrites perfectly too — so the read itself asserts that the declared chunks
/// tile the body exactly, and a root of 6,004 files where none refuses is that
/// assertion holding 6,004 times.
/// </para>
/// <para>
/// Then one file is actually edited, because inserting an entity is where the
/// derived fields are: the chunk's size and every later chunk's offset. A run
/// that only rewrote files unchanged would never touch a single number.
/// </para>
/// </remarks>
public sealed class LayerCorpusTests(ITestOutputHelper output)
{
    private const string MapsVariable = "PERIANTH_MAPS";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_layer_accounts_for_its_body_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(MapsVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {MapsVariable} to an extraction of 'camel/maps'");
            return;
        }

        List<string> failures = [];
        int files = 0;
        int identical = 0;
        int headerOnly = 0;
        int outOfOrder = 0;
        long chunks = 0;
        string? placeable = null;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mlayer", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (!source.IsSuccess)
            {
                failures.Add($"{Name(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<LayerDocument> read = LayerDocument.Read(source.Value);
            if (!read.IsSuccess)
            {
                failures.Add($"{Name(path)}: {read.Refusal.Message}");
                continue;
            }

            files++;
            LayerDocument layer = read.Value;
            chunks += layer.Chunks.Length;

            if (layer.Chunks.Length == 0)
            {
                headerOnly++;
            }
            else
            {
                // The trap this type exists to avoid: the key is a quad-tree
                // cell, not a position, so declaration order is not offset order.
                int[] offsets = [.. layer.Chunks.Select(c => c.Offset)];
                if (!offsets.SequenceEqual(offsets.Order()))
                {
                    outOfOrder++;

                    // Deliberately the hardest layer available: several chunks,
                    // declared in an order that is not their order in the body.
                    // A single-chunk layer never shifts an offset at all, so
                    // placing into one would exercise none of the arithmetic.
                    placeable ??= path;
                }
            }

            if (layer.Bytes.Span.SequenceEqual(source.Value.Bytes))
            {
                identical++;
            }
            else
            {
                failures.Add($"{Name(path)}: rewrote to different bytes");
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} layers, {identical} byte-identical, {headerOnly} header-only, {chunks} chunks, {outOfOrder} declared out of offset order"));

        foreach (string failure in failures.Take(10))
        {
            _output.WriteLine("  " + failure);
        }

        Assert.True(files > 0, $"{MapsVariable} named a root holding no .mlayer files");
        Assert.Empty(failures);
        Assert.Equal(files, identical);

        // Both shapes must be present, or the run proves less than it looks:
        // a root of only header-only layers never exercises the accounting.
        Assert.True(headerOnly > 0, "no header-only layer was seen");
        Assert.NotNull(placeable);

        // And the out-of-order case must be present, since it is the one an
        // edit gets wrong silently.
        Assert.True(outOfOrder > 0, "no layer declared its chunks out of offset order");

        Placing(placeable!);
    }

    /// <summary>
    /// Puts an entity into a real layer and checks the numbers moved with it.
    /// </summary>
    private void Placing(string path)
    {
        LayerDocument before = LayerDocument.Read(SourceFileReader.Read(path).Value).Value;

        // The chunk that sits first in the body, so every other chunk's offset
        // has to move. Choosing the last one would shift nothing.
        int index = 0;
        for (int i = 1; i < before.Chunks.Length; i++)
        {
            if (before.Chunks[i].Offset < before.Chunks[index].Offset)
            {
                index = i;
            }
        }

        LayerChunk target = before.Chunks[index];
        Assert.True(before.Chunks.Length > 1, "the chosen layer has only one chunk");
        Assert.Equal(0, target.Offset);

        const string Record = "\t{\n\t\tname = \"perianth corpus test\",\n\t\ttype = \"Prop\",\n\t},\n";
        Result<LayerDocument> edited = before.WithEntity(index, Record);
        if (!edited.IsSuccess)
        {
            Assert.Fail($"{Name(path)}: {edited.Refusal.Message}");
        }

        LayerDocument after = edited.Value;

        // Re-reading it succeeded, which already means the chunks still tile the
        // body — the accounting is checked on every read, so an offset left
        // stale would have refused here rather than produced this document.
        Assert.Equal(before.Chunks.Length, after.Chunks.Length);
        LayerChunk grown = after.Chunks[index];
        Assert.Equal(target.Size + Record.Length, grown.Size);

        // The header's own count of entities is a derived field too, and the
        // only one that is invisible in the body. Leaving it stale shipped in
        // probe batch 3 and the layer stopped drawing most of what it held, so
        // this asserts on the real file rather than on a fixture.
        Assert.True(before.DeclaredEntities >= 0, $"{Name(path)} declares no entity count");
        Assert.Equal(before.DeclaredEntities + 1, after.DeclaredEntities);

        // Every other chunk keeps its own contents, whatever the offsets did.
        string was = Encoding.Latin1.GetString(before.Bytes.Span);
        string now = Encoding.Latin1.GetString(after.Bytes.Span);
        for (int i = 0; i < before.Chunks.Length; i++)
        {
            if (i == index)
            {
                continue;
            }

            LayerChunk chunk = before.Chunks[i];
            LayerChunk moved = after.Chunks[i];
            Assert.Equal(chunk.Size, moved.Size);
            Assert.True(
                was.AsSpan(before.BodyStart + chunk.Offset, chunk.Size)
                    .SequenceEqual(now.AsSpan(after.BodyStart + moved.Offset, moved.Size)),
                string.Create(CultureInfo.InvariantCulture, $"chunk {i} changed contents"));
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"placed into {Name(path)}: chunk {index} grew {target.Size} -> {grown.Size}, file {before.Bytes.Length} -> {after.Bytes.Length}"));

        Assert.Equal(before.Bytes.Length + Record.Length, after.Bytes.Length - HeaderGrowth(was, now));
    }

    /// <summary>
    /// How many bytes the header gained, which is how many digits the rewritten
    /// numbers added.
    /// </summary>
    private static int HeaderGrowth(string before, string after) =>
        after.IndexOf('\0', StringComparison.Ordinal) - before.IndexOf('\0', StringComparison.Ordinal);

    private static string Name(string path) =>
        Path.GetFileName(Path.GetDirectoryName(path)) ?? Path.GetFileName(path);
}
