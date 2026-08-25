using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Reads every cameldata file under a corpus root, and writes each one back.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_CORPUS</c> is set, so the default suite stays
/// asset-free. There is no recorded manifest for cameldata, so this is a breadth
/// check rather than a conformance one, and it asserts the property Roadmap §6.3
/// asks import to establish before anything depends on it: <strong>read a real
/// file, write it back, and the bytes match</strong>.
/// </para>
/// <para>
/// That is stronger than "it parses", and stronger in the direction that
/// matters. A reader that quietly discards a field parses every file and only
/// fails when a writer tries to put the field back — and this reader keeps four
/// blocks it never interprets (the Bezier words, each constant's data indices and
/// optional tail, and any trailing bytes) precisely so a writer could. Nothing
/// but a round trip over real files can show they survived.
/// </para>
/// <para>
/// The census it prints is the evidence for the geometry stage, so it counts what
/// that stage depends on rather than only what failed: how many files account for
/// every byte, the Z bit widths in use, and how many constants derive UV0 from
/// position rather than carrying it.
/// </para>
/// </remarks>
public sealed class CameldataCorpusTests(ITestOutputHelper output)
{
    private const string CorpusVariable = "PERIANTH_CORPUS";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_corpus_file_parses_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus cameldata files");
            return;
        }

        List<string> failures = [];
        Dictionary<int, int> modes = [];
        Dictionary<int, int> zBitWidths = [];
        int files = 0;
        int rewritten = 0;
        int accountedForEveryByte = 0;
        int constants = 0;
        int surfaceUv0 = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cameldata", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (source.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<CameldataFile> result = CameldataReader.Read(source.Value);
            if (result.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Refusal.Message}");
                continue;
            }

            files++;
            CameldataFile file = result.Value;
            modes[file.Mode] = modes.GetValueOrDefault(file.Mode) + 1;

            // The grammar accounting for every byte is what makes a writer
            // possible at all, so it is counted rather than assumed. A file with
            // trailing bytes still round-trips -- they are preserved -- but the
            // count says how much of the format is understood.
            if (file.TrailingBytes.Length == 0)
            {
                accountedForEveryByte++;
            }

            if (file is Mode3Cameldata mode3)
            {
                constants += mode3.Constants.Length;
                foreach (Mode3Constant constant in mode3.Constants)
                {
                    zBitWidths[constant.ZBitWidth] = zBitWidths.GetValueOrDefault(constant.ZBitWidth) + 1;
                    if (!constant.UsesUnifiedUv0)
                    {
                        surfaceUv0++;
                    }
                }
            }

            // Compared against the bytes on disk rather than against anything the
            // reader kept, so the two halves cannot agree by sharing a misreading.
            byte[] original = File.ReadAllBytes(path);
            Result<byte[]> written = CameldataWriter.Write(file);

            if (written.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: refused writing back: {written.Refusal.Message}");
            }
            else if (!written.Value.AsSpan().SequenceEqual(original))
            {
                // Where, not just whether: the first differing offset says which
                // constant or array the writer got wrong, and a length difference
                // is a different bug from a single wrong byte.
                failures.Add(
                    $"{Path.GetFileName(path)}: wrote {written.Value.Length} bytes for " +
                    $"{original.Length}, {Difference(original, written.Value)}");
            }
            else
            {
                rewritten++;
            }
        }

        StringBuilder census = new();
        census.Append(CultureInfo.InvariantCulture, $"{files} files, {constants} mode-3 constants");
        census.Append(CultureInfo.InvariantCulture, $"; {rewritten} written back byte-identically");
        census.Append(CultureInfo.InvariantCulture, $"; {accountedForEveryByte} account for every byte");
        census.Append(CultureInfo.InvariantCulture, $"; modes {Render(modes)}");
        census.Append(CultureInfo.InvariantCulture, $"; Z bit widths {Render(zBitWidths)}");
        census.Append(CultureInfo.InvariantCulture, $"; {surfaceUv0} constants derive UV0 from position");
        _output.WriteLine(census.ToString());

        Assert.True(files > 0, $"no cameldata files found under {root}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} cameldata files did not survive a round trip" +
                string.Concat(failures.Take(20).Select(f => Environment.NewLine + "  " + f)));
        }

        // Every file that parsed must also have been written back, or a writer
        // that silently produced nothing would leave this reporting success over
        // a census it no longer covers.
        Assert.Equal(files, rewritten);
    }

    /// <summary>
    /// Each record's XY and Z pool slices are its own, which is what makes a
    /// geometry edit safe.
    /// </summary>
    /// <remarks>
    /// The measurement the whole reshape stage rests on: if two records shared a
    /// pool slot, editing one part would silently move another, and no refusal in
    /// the edit could detect it. Asserted over the corpus rather than recorded in
    /// a document, so a file that breaks the assumption fails loudly instead of
    /// producing a mod that draws the wrong thing.
    /// <para>
    /// Only the XY spans are checked here, because a span is what a record claims
    /// from its base; the Z slots a record actually reaches need its MMB, which
    /// this test does not read. The stronger per-slot check lives with the edit.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_two_records_claim_the_same_stretch_of_the_XY_pool()
    {
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus cameldata files");
            return;
        }

        List<string> overlapping = [];
        int files = 0;
        int checkedConstants = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.cameldata", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (source.IsRefused || CameldataReader.Read(source.Value) is not { IsSuccess: true } read)
            {
                continue;
            }

            if (read.Value is not Mode3Cameldata file)
            {
                continue;
            }

            files++;

            // Bases sorted, then each compared with the next: a record's own
            // vertex count is in its MMB, so the strongest statement available
            // from the cameldata alone is that no two constants start at the same
            // slot. Two sharing a base share every vertex.
            ImmutableArray<uint> bases = [.. file.Constants.Select(c => c.XyBase).OrderBy(b => b)];
            checkedConstants += bases.Length;

            for (int i = 1; i < bases.Length; i++)
            {
                if (bases[i] == bases[i - 1])
                {
                    overlapping.Add($"{Path.GetFileName(path)}: two constants share XY base {bases[i]}");
                    break;
                }
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} mode-3 files, {checkedConstants} constants, {overlapping.Count} with a shared XY base"));

        Assert.True(files > 0, $"no mode-3 cameldata files found under {root}");
        Assert.Empty(overlapping);
    }

    /// <summary>Where two buffers first disagree, and what they hold there.</summary>
    private static string Difference(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual)
    {
        int shared = Math.Min(expected.Length, actual.Length);

        for (int offset = 0; offset < shared; offset++)
        {
            if (expected[offset] != actual[offset])
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"first differing at 0x{offset:X}: expected 0x{expected[offset]:X2}, wrote 0x{actual[offset]:X2}");
            }
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"identical for the first {shared} bytes, then one ends");
    }

    private static string Render<T>(Dictionary<T, int> counts)
        where T : notnull =>
        "{" + string.Join(", ", counts
            .OrderBy(pair => pair.Key.ToString(), StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}: {pair.Value}")) + "}";
}
