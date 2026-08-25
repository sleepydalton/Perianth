using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Anim;

/// <summary>
/// Reads every shipped animation whole and writes it back.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_ANIMS</c> names a directory of extracted
/// <c>.anim</c> files, so the default suite stays asset-free. Point it at an
/// extraction of <c>camel/baked/snowdrop/animation</c>, which is where all
/// 68,561 of them ship.
/// </para>
/// <para>
/// The oracle every writer here keeps, and it is worth more than "it parses" for
/// the reason import rests on: a reader that quietly discards a chunk parses
/// everything and only fails when a writer tries to put the chunk back. This one
/// also asserts that the read accounted for every byte, which is the claim the
/// specification's §6 could not make — that section describes locating chunks by
/// a bounded tag search, and a search is a reader strategy rather than a layout.
/// A writer cannot use one, so the layout had to be derived, and this is what
/// says the derivation is right.
/// </para>
/// <para>
/// <b>Coverage is asserted, not just agreement.</b> Five format versions ship and
/// they do not hold the same file: the version word's low half moves the header
/// by thirteen bytes and its high half decides whether there is a tail at all, so
/// a run over version <c>0x0003000e</c> alone — 99.2% of the corpus — would pass
/// while leaving three tail shapes and one header length the writer has never
/// been asked to spell. Both channel shapes are counted for the same reason: a
/// flat channel and a compressed one are laid out differently, and 96.7% of
/// channels are compressed.
/// </para>
/// </remarks>
public sealed class AnimCorpusTests(ITestOutputHelper output)
{
    private const string RootVariable = "PERIANTH_ANIMS";

    /// <summary>The five format versions the archives hold.</summary>
    private static readonly uint[] Expected = [0x0003000e, 0x0000000c, 0x0000000d, 0x0001000d, 0x0001000e];

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_animation_reads_whole_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to a directory of extracted .anim files");
            return;
        }

        List<string> failures = [];
        Dictionary<uint, int> versions = [];
        int files = 0;
        int identical = 0;
        long nodes = 0;
        long flat = 0;
        long compressed = 0;
        int hierarchies = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.anim", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (!source.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<AnimDocument> read = AnimReader.ReadDocument(source.Value);
            if (!read.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: {read.Refusal.Message}");
                continue;
            }

            files++;
            AnimDocument document = read.Value;
            uint version = Version(document);
            versions[version] = versions.GetValueOrDefault(version) + 1;
            nodes += document.NodeCount;
            hierarchies += document.Parents.IsEmpty ? 0 : 1;
            foreach (AnimChannelBlock block in document.Channels)
            {
                if (block.Compressed)
                {
                    compressed++;
                }
                else
                {
                    flat++;
                }
            }

            Result<byte[]> written = AnimWriter.Write(document);
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
            $"{files} animations, {identical} byte-identical, {nodes} nodes, {hierarchies} with a hierarchy"));
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"  channels: {compressed} compressed, {flat} flat"));

        foreach ((uint version, int count) in versions.OrderByDescending(p => p.Value))
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  version 0x{version:x8}: {count}"));
        }

        foreach (string failure in failures.Take(10))
        {
            _output.WriteLine("  " + failure);
        }

        Assert.True(files > 0, $"{RootVariable} named a root holding no animations");
        Assert.Empty(failures);
        Assert.Equal(files, identical);

        // Coverage, not agreement. A writer proven only on the version 99.2% of
        // the corpus uses would pass everything and fail on the first of the
        // other four, whose headers and tails are different lengths.
        foreach (uint version in Expected)
        {
            Assert.True(
                versions.GetValueOrDefault(version) > 0,
                string.Create(CultureInfo.InvariantCulture, $"no shipped animation exercised format version 0x{version:x8}"));
        }

        Assert.True(flat > 0, "no shipped animation exercised a flat channel");
        Assert.True(compressed > 0, "no shipped animation exercised a compressed channel");
        Assert.True(hierarchies > 0, "no shipped animation carried a PRNT hierarchy");
    }

    [Fact]
    public void Every_animation_takes_a_new_joint_and_still_reads_back()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {RootVariable} to a directory of extracted .anim files");
            return;
        }

        List<string> failures = [];
        int files = 0;
        int grown = 0;
        int bitsGrew = 0;
        int withoutHierarchy = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.anim", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            Result<AnimDocument> read = source.IsSuccess
                ? AnimReader.ReadDocument(source.Value)
                : source.Refusal;
            if (!read.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: {read.Refusal.Message}");
                continue;
            }

            files++;
            AnimDocument before = read.Value;
            withoutHierarchy += before.Parents.IsEmpty ? 1 : 0;

            Result<AnimDocument> appended = before.WithAppendedNode("perianth_probe_joint", parent: 0);
            if (!appended.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: refused a new joint: {appended.Refusal.Message}");
                continue;
            }

            if (appended.Value.NodeBits.Length > before.NodeBits.Length)
            {
                bitsGrew++;
            }

            Result<byte[]> written = AnimWriter.Write(appended.Value);
            if (!written.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: refused to write: {written.Refusal.Message}");
                continue;
            }

            // The reader takes every chunk's length from the header and requires
            // the chunks to account for the file exactly, so a file it accepts is
            // one whose counts all still agree with its body.
            Result<AnimDocument> reread = AnimReader.ReadDocument(
                SourceFile.FromMemory(path, written.Value));
            if (!reread.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: would not read back: {reread.Refusal.Message}");
                continue;
            }

            if (reread.Value.NodeCount != before.NodeCount + 1 ||
                !string.Equals(reread.Value.Names[^1], "perianth_probe_joint", StringComparison.Ordinal))
            {
                failures.Add($"{Path.GetFileName(path)}: read back {reread.Value.NodeCount} nodes against {before.NodeCount + 1}");
                continue;
            }

            grown++;
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} animations, {grown} grew a joint and read back, {bitsGrew} whose bit array grew, {withoutHierarchy} with no hierarchy"));

        foreach (string failure in failures.Take(10))
        {
            _output.WriteLine("  " + failure);
        }

        Assert.True(files > 0, $"{RootVariable} named a root holding no animations");
        Assert.Empty(failures);
        Assert.Equal(files, grown);

        // Coverage again, and both of these are a different rule rather than more
        // of the same one: a node count that crosses a byte grows the tail's bit
        // array, and a file with no hierarchy has no parent entry to add.
        Assert.True(bitsGrew > 0, "no shipped animation had a node count on a byte boundary");
        Assert.True(withoutHierarchy > 0, "no shipped animation lacked a hierarchy");
    }

    private static uint Version(AnimDocument document) =>
        (uint)(document.Header[4]
            | (document.Header[5] << 8)
            | (document.Header[6] << 16)
            | (document.Header[7] << 24));

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
