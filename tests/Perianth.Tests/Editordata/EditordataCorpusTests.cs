using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Editordata;

/// <summary>
/// Reads every editordata file under a corpus root, and writes each one back.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_CORPUS</c> is set, so the default suite stays
/// asset-free. There is no recorded manifest for editordata, so this is a
/// breadth check rather than a conformance one: it asserts that the grammar
/// accounts for every byte of every real file, which is the property a shifted
/// cursor breaks and a synthetic fixture cannot demonstrate.
/// </para>
/// <para>
/// It also asserts the property Roadmap §6.3 asks import to establish before
/// anything depends on it: <strong>read a real file, write it back, and the
/// bytes match</strong>. That is a stronger statement than "it parses" and it
/// is stronger in the direction that matters — a reader that quietly discards a
/// field still parses every file, and only fails when something tries to write
/// the field back. Corpus files are the only place the writer meets a
/// combination nobody thought to build a fixture for.
/// </para>
/// <para>
/// The census it reproduces, taken from the reference over the same corpus:
/// 317 files, every one declaring custom-data version 3, every section holding
/// exactly one material record, no section holding zero, and five channel names
/// throughout. 49 files carry an empty shader name in section 0 and are
/// unexportable for that reason — but they must still <em>parse</em>, because
/// judging the shader family is reconstruction, not grammar.
/// </para>
/// </remarks>
public sealed class EditordataCorpusTests(ITestOutputHelper output)
{
    private const string CorpusVariable = "PERIANTH_CORPUS";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_corpus_file_parses_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus editordata files");
            return;
        }

        List<string> failures = [];
        Dictionary<string, int> versions = [];
        Dictionary<string, int> shaders = [];
        Dictionary<int, int> materialCounts = [];
        HashSet<string> channels = [];
        int files = 0;
        int sections = 0;
        int rewritten = 0;
        int unboundChannels = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.editordata", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (source.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<EditordataFile> result = EditordataReader.Read(source.Value);
            if (result.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Refusal.Message}");
                continue;
            }

            files++;
            EditordataFile file = result.Value;

            // Compared against the bytes on disk rather than against anything
            // the reader kept, so the check cannot be satisfied by the two
            // halves sharing a misreading.
            byte[] original = File.ReadAllBytes(path);
            Result<byte[]> written = EditordataWriter.Write(file);

            if (written.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: refused writing back: {written.Refusal.Message}");
            }
            else if (!written.Value.AsSpan().SequenceEqual(original))
            {
                // Where, not just whether: a length difference and a single
                // differing byte are different bugs, and the first differing
                // offset says which record the writer got wrong.
                failures.Add(
                    $"{Path.GetFileName(path)}: wrote {written.Value.Length} bytes for " +
                    $"{original.Length}, {Difference(original, written.Value)}");
            }
            else
            {
                rewritten++;
            }

            string version = file.CustomVersion?.ToString(CultureInfo.InvariantCulture) ?? "none";
            versions[version] = versions.GetValueOrDefault(version) + 1;

            foreach (EditordataSection section in file.Sections)
            {
                sections++;
                materialCounts[section.Materials.Length] = materialCounts.GetValueOrDefault(section.Materials.Length) + 1;

                foreach (EditordataMaterial material in section.Materials)
                {
                    string name = material.Shader.Length == 0 ? "<empty>" : material.Shader;
                    shaders[name] = shaders.GetValueOrDefault(name) + 1;

                    foreach (EditordataChannel channel in material.Channels)
                    {
                        channels.Add(channel.Channel);

                        // Counted because a mutation test found the corpus
                        // cannot see it. Dropping every channel with an empty
                        // path — the most plausible tidy-up a writer could
                        // make — leaves all 317 files byte-identical, so this
                        // number says how much of the writer these files are
                        // able to check, and the synthetic fixture covers the
                        // rest rather than duplicating them.
                        if (channel.TexturePath.Length == 0)
                        {
                            unboundChannels++;
                        }
                    }
                }
            }
        }

        StringBuilder census = new();
        census.Append(CultureInfo.InvariantCulture, $"{files} files, {sections} sections");
        census.Append(CultureInfo.InvariantCulture, $"; {rewritten} written back byte-identically");
        census.Append(CultureInfo.InvariantCulture, $"; versions {Render(versions)}");
        census.Append(CultureInfo.InvariantCulture, $"; materials per section {Render(materialCounts)}");
        census.Append(CultureInfo.InvariantCulture, $"; shaders {Render(shaders)}");
        census.Append(CultureInfo.InvariantCulture, $"; channels [{string.Join(", ", channels.OrderBy(c => c, StringComparer.Ordinal))}]");
        census.Append(CultureInfo.InvariantCulture, $"; {unboundChannels} channels bind an empty path");
        _output.WriteLine(census.ToString());

        Assert.True(files > 0, $"no editordata files found under {root}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} editordata files did not survive a round trip" +
                string.Concat(failures.Take(20).Select(f => Environment.NewLine + "  " + f)));
        }

        // Every file that parsed must also have been written back, or a writer
        // that silently produced nothing would leave this test reporting
        // success over a census it no longer covers.
        Assert.Equal(files, rewritten);
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
