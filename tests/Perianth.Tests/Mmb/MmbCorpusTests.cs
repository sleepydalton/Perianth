using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Xunit;

namespace Perianth.Tests.Mmb;

/// <summary>
/// Reads every real MMB and writes it back, requiring the bytes to match.
/// </summary>
/// <remarks>
/// <para>
/// This is import's oracle (§6.3) and the only test capable of showing the
/// writer works. It is a stronger claim than "it parses": a reader that quietly
/// discards a field parses everything and fails only when a writer tries to put
/// the field back. Every field the reader keeps but never reads — the node
/// table, the part flag bytes, the level-of-detail flags, the tail block — is
/// here because this check demanded it.
/// </para>
/// <para>
/// It asserts <b>coverage</b> as well as agreement, which matters more here than
/// for the editordata and cameldata suites. Three container magics ship and at
/// least two versions, so a writer proven on version 11 plain <c>MMB</c> would
/// pass everything it was shown and fail on the first <c>MUCM</c> it met. A run
/// that met only one version fails rather than reporting success over a subset.
/// </para>
/// <para>
/// Gated on <c>PERIANTH_CORPUS</c>, so <c>dotnet test</c> stays asset-free.
/// </para>
/// </remarks>
public sealed class MmbCorpusTests
{
    private static string? Corpus => Environment.GetEnvironmentVariable("PERIANTH_CORPUS");

    [Fact]
    public void Every_model_is_written_back_byte_identically()
    {
        if (string.IsNullOrWhiteSpace(Corpus))
        {
            Assert.Skip("set PERIANTH_CORPUS to read the corpus model files");
        }

        List<string> files = [.. Directory.EnumerateFiles(Corpus!, "*.mmb", SearchOption.AllDirectories).Order()];
        Assert.True(files.Count > 0, $"No .mmb files under {Corpus}.");

        HashSet<int> versions = [];
        List<string> mismatched = [];
        List<string> refused = [];
        int written = 0;

        foreach (string path in files)
        {
            byte[] original = File.ReadAllBytes(path);
            Result<MmbModel> read = MmbReader.Read(new SourceFile(path, original));
            if (!read.IsSuccess)
            {
                // A refusal is data, not a failure: the alternative containers
                // refuse for what they are and are counted rather than hidden.
                refused.Add($"{Path.GetFileName(path)}: {read.Refusal.Message}");
                continue;
            }

            versions.Add(read.Value.Version);
            Result<byte[]> back = MmbContainerWriter.Write(read.Value);
            if (!back.IsSuccess)
            {
                mismatched.Add($"{Path.GetFileName(path)}: writer refused — {back.Refusal.Message}");
                continue;
            }

            if (!back.Value.AsSpan().SequenceEqual(original))
            {
                mismatched.Add($"{Path.GetFileName(path)}: {Describe(original, back.Value)}");
                continue;
            }

            written++;
        }

        Assert.True(written > 0, $"Nothing was written back. Refusals: {string.Join("; ", refused.Take(3))}");
        Assert.True(mismatched.Count == 0,
            $"{mismatched.Count} of {files.Count} models did not survive a round trip:\n" +
            string.Join("\n", mismatched.Take(10)));

        // Coverage. Two versions ship; meeting only one proves nothing about
        // the gates that separate them, and every one of those gates changes a
        // field's width.
        Assert.True(versions.Count >= 2,
            $"Only version(s) {string.Join(", ", versions.Order())} were met, so the version gates are untested. " +
            "Point PERIANTH_CORPUS at a root holding more than one.");
    }

    /// <summary>The first differing byte, which is what a shifted cursor shows.</summary>
    private static string Describe(byte[] original, byte[] written)
    {
        if (original.Length != written.Length)
        {
            return $"length {written.Length} against {original.Length}";
        }

        for (int i = 0; i < original.Length; i++)
        {
            if (original[i] != written[i])
            {
                return $"first differs at byte {i}: {written[i]:X2} against {original[i]:X2}";
            }
        }

        return "identical, which contradicts the comparison";
    }
}
