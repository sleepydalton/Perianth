using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Formats.Bvm;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Bvm;

/// <summary>
/// Reads every shipped BVM container and writes it back.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_BVM</c> names a directory holding extracted
/// <c>.mgraphobject</c> and <c>.manimsys</c> files, so the default suite stays
/// asset-free. Point it at an extraction of <c>camel/graph objects</c> and
/// <c>camel/animation</c>.
/// </para>
/// <para>
/// The oracle every writer here keeps, and the reason it is worth more than "it
/// parses": a reader that quietly merged two tags decoding to the same payload
/// reads everything and only fails when a writer tries to put them back apart.
/// It also asserts that the decode consumed the file exactly — a decoder
/// stopping early produces a plausible tree over half a file, and only counting
/// bytes tells that from a correct read.
/// </para>
/// <para>
/// <b>Both extensions must be present, and that is a coverage claim rather than
/// tidiness.</b> The two populations exercise different tags: graph objects
/// carry <c>0x05</c>, <c>0x07</c> and <c>0x0b</c>, and only animation systems
/// carry <c>0x11</c>. A run over graph objects alone would pass while leaving a
/// tag the writer has never been asked to spell, so the tags actually seen are
/// counted and asserted.
/// </para>
/// </remarks>
public sealed class BvmCorpusTests(ITestOutputHelper output)
{
    private const string RootVariable = "PERIANTH_BVM";

    /// <summary>
    /// The tags the shipped corpus exercises. All eighteen the grammar knows,
    /// less <c>0x11</c>… which animation systems do carry, so all eighteen.
    /// </summary>
    private static readonly byte[] Expected =
        [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
         0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f, 0x10, 0x11];

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_container_reads_whole_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to a directory of extracted .mgraphobject and .manimsys files");
            return;
        }

        List<string> failures = [];
        Dictionary<byte, long> tags = [];
        Dictionary<string, int> kinds = [];
        int files = 0;
        int identical = 0;
        long strings = 0;
        long values = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(Container)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (!source.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<BvmDocument> read = BvmReader.ReadDocument(source.Value);
            if (!read.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: {read.Refusal.Message}");
                continue;
            }

            files++;
            kinds[Path.GetExtension(path)] = kinds.GetValueOrDefault(Path.GetExtension(path)) + 1;
            strings += read.Value.Strings.Length;
            values += Tally(read.Value.Graph, tags);

            Result<byte[]> written = BvmWriter.Write(read.Value);
            if (!written.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: refused to write back: {written.Refusal.Message}");
                continue;
            }

            if (written.Value.AsSpan().SequenceEqual(source.Value.Bytes))
            {
                identical++;
            }
            else
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)}: rewrote {written.Value.Length} bytes against {source.Value.Bytes.Length}, at {FirstDifference(written.Value, source.Value.Bytes)}"));
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} containers, {identical} byte-identical, {strings} strings, {values} values"));

        foreach ((string extension, int count) in kinds.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count,6} {extension}"));
        }

        foreach (byte tag in Expected)
        {
            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  tag 0x{tag:x2}: {tags.GetValueOrDefault(tag)}"));
        }

        foreach (string failure in failures.Take(10))
        {
            _output.WriteLine("  " + failure);
        }

        Assert.True(files > 0, $"{RootVariable} named a root holding no BVM containers");
        Assert.Empty(failures);
        Assert.Equal(files, identical);

        // Coverage, not agreement. A writer proven only on the tags one
        // extension happens to use would pass everything and fail on the first
        // file of the other kind.
        Assert.Equal(2, kinds.Count);
        foreach (byte tag in Expected)
        {
            Assert.True(
                tags.GetValueOrDefault(tag) > 0,
                string.Create(CultureInfo.InvariantCulture, $"no shipped container exercised tag 0x{tag:x2}"));
        }
    }

    private static bool Container(string path) =>
        path.EndsWith(".mgraphobject", StringComparison.Ordinal)
        || path.EndsWith(".manimsys", StringComparison.Ordinal);

    private static long Tally(BvmValue value, Dictionary<byte, long> tags)
    {
        tags[value.Tag] = tags.GetValueOrDefault(value.Tag) + 1;
        long count = 1;

        if (value is BvmContainer container)
        {
            foreach (BvmValue item in container.Items)
            {
                count += Tally(item, tags);
            }

            foreach (BvmPair pair in container.Entries)
            {
                count += Tally(pair.Key, tags);
                count += Tally(pair.Value, tags);
            }
        }

        return count;
    }

    /// <summary>Where two buffers first differ, so a failure names a byte.</summary>
    private static string FirstDifference(ReadOnlySpan<byte> written, ReadOnlySpan<byte> original)
    {
        int shared = Math.Min(written.Length, original.Length);
        for (int at = 0; at < shared; at++)
        {
            if (written[at] != original[at])
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"byte {at} (wrote 0x{written[at]:x2}, file has 0x{original[at]:x2})");
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"byte {shared} (one ends)");
    }
}
