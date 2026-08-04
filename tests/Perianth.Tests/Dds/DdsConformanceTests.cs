using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Dds;

/// <summary>
/// Checks the decoder against the recorded reference manifest.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless both <c>PERIANTH_DDS_MANIFEST</c> and <c>PERIANTH_DDS_ROOT</c>
/// are set, so the default suite stays synthetic and asset-free. This is the
/// only test in the project that wants real game files, and it earns the
/// exception: the manifest turns a decoder mismatch into a hash comparison
/// naming the input, rather than a visual inspection of a texture.
/// </para>
/// <para>
/// The manifest is keyed by the SHA-256 of the input file's bytes rather than
/// by path, so it carries no local paths and works wherever the corpus lives.
/// Files present on disk but absent from the manifest are counted and ignored:
/// the manifest is the authority on what was recorded, not on what exists.
/// </para>
/// </remarks>
public sealed class DdsConformanceTests(ITestOutputHelper output)
{
    private const string ManifestVariable = "PERIANTH_DDS_MANIFEST";
    private const string RootVariable = "PERIANTH_DDS_ROOT";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Headers_match_the_reference_for_every_recorded_input()
    {
        if (!TryLoad(out Dictionary<string, Entry> manifest, out string root))
        {
            return;
        }

        List<string> failures = [];
        int checkedCount = 0;
        Dictionary<string, int> byFormat = [];
        HashSet<string> seen = [];

        foreach ((string path, string digest, byte[] bytes) in Inputs(root))
        {
            if (!manifest.TryGetValue(digest, out Entry entry))
            {
                continue;
            }

            checkedCount++;
            seen.Add(digest);
            byFormat[entry.SourceFormat] = byFormat.GetValueOrDefault(entry.SourceFormat) + 1;

            Result<DdsHeader> result = DdsReader.ReadHeader(bytes);

            if (entry.SourceFormat.StartsWith("uncompressed", StringComparison.Ordinal)
                && !string.Equals(entry.SourceFormat, "uncompressed32", StringComparison.Ordinal))
            {
                // 32bpp is read now; the narrower depths are still refused by
                // name, and this is what holds that boundary in place. See
                // DdsReader.ReadUncompressed for why the line sits here.
                if (!result.IsRefused || result.Refusal.Kind != RefusalKind.Unsupported)
                {
                    failures.Add($"{Name(path)}: expected an unsupported refusal for {entry.SourceFormat}");
                }

                continue;
            }

            if (!result.TryGetValue(out DdsHeader header, out Refusal? refusal))
            {
                failures.Add($"{Name(path)}: {entry.SourceFormat} {entry.Width}x{entry.Height} refused: {refusal.Message}");
                continue;
            }

            if (header.Width != entry.Width || header.Height != entry.Height)
            {
                failures.Add(
                    $"{Name(path)}: read {header.Width}x{header.Height}, reference {entry.Width}x{entry.Height}");
            }

            if (Expected(entry.SourceFormat) is { } expected && header.Format != expected)
            {
                failures.Add($"{Name(path)}: read {header.Format}, reference {entry.SourceFormat}");
            }
        }

        Report("header", checkedCount, byFormat, failures);
        AssertWholeOracleCovered(seen, manifest.Keys, root);
    }

    [Theory]
    [InlineData("DXT1")]
    [InlineData("DXT5")]
    [InlineData("DX10:98")]
    [InlineData("uncompressed32")]
    public void Decoded_pixels_match_the_reference(string sourceFormat)
    {
        if (!TryLoad(out Dictionary<string, Entry> manifest, out string root))
        {
            return;
        }

        List<string> failures = [];
        int matched = 0;
        int notYetDecoded = 0;
        HashSet<string> seen = [];

        foreach ((string path, string digest, byte[] bytes) in Inputs(root))
        {
            if (!manifest.TryGetValue(digest, out Entry entry))
            {
                continue;
            }

            if (!string.Equals(entry.SourceFormat, sourceFormat, StringComparison.Ordinal))
            {
                notYetDecoded++;
                continue;
            }

            seen.Add(digest);

            Result<DdsImage> result = DdsReader.Read(bytes);
            if (result.IsRefused)
            {
                failures.Add($"{Name(path)}: refused: {result.Refusal.Message}");
                continue;
            }

            DdsImage image = result.Value;

            if (image.Pixels.Length != entry.RgbaBytes)
            {
                failures.Add(
                    $"{Name(path)}: decoded {image.Pixels.Length} bytes, reference {entry.RgbaBytes}");
                continue;
            }

            string digestOut = Convert.ToHexStringLower(SHA256.HashData(image.Pixels));
            if (!string.Equals(digestOut, entry.RgbaSha256, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{Name(path)}: {image.Width}x{image.Height} decoded to {digestOut[..16]}…, reference {entry.RgbaSha256[..16]}…");
                continue;
            }

            matched++;
        }

        Report(
            $"{sourceFormat} decode",
            matched,
            new Dictionary<string, int> { ["other formats"] = notYetDecoded },
            failures);

        AssertWholeOracleCovered(
            seen,
            manifest
                .Where(pair => string.Equals(pair.Value.SourceFormat, sourceFormat, StringComparison.Ordinal))
                .Select(pair => pair.Key),
            root);
    }

    /// <summary>
    /// Fails unless every recorded input was actually reached.
    /// </summary>
    /// <remarks>
    /// Without this the suite passes just as loudly when the corpus root holds
    /// three files as when it holds twelve thousand, and "conformant" would
    /// mean whatever happened to be on disk. Coverage is the claim being made,
    /// so coverage is asserted rather than printed.
    /// </remarks>
    private void AssertWholeOracleCovered(
        HashSet<string> seen,
        IEnumerable<string> expected,
        string root)
    {
        List<string> missing = [.. expected.Where(digest => !seen.Contains(digest))];
        _output.WriteLine($"covered {seen.Count} of {seen.Count + missing.Count} recorded inputs");

        Assert.True(
            missing.Count == 0,
            $"{missing.Count} recorded inputs were not found under {root}, so this run did not check the whole oracle");
    }

    private static DdsFormat? Expected(string sourceFormat) => sourceFormat switch
    {
        "DXT1" => DdsFormat.Bc1,
        "DXT5" => DdsFormat.Bc3,
        "DX10:98" => DdsFormat.Bc7,
        "uncompressed32" => DdsFormat.Uncompressed32,
        _ => null,
    };

    private void Report(
        string what,
        int checkedCount,
        Dictionary<string, int> counts,
        List<string> failures)
    {
        // Written whether or not anything failed. A conformance run that says
        // only "passed" hides the number that matters: a suite that compared
        // one file and a suite that compared four thousand both look the same
        // from the outside.
        StringBuilder coverage = new();
        coverage.Append(CultureInfo.InvariantCulture, $"{what}: {checkedCount} agreed, {failures.Count} disagreed");
        foreach ((string key, int value) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            coverage.Append(CultureInfo.InvariantCulture, $"; {key}: {value}");
        }

        _output.WriteLine(coverage.ToString());

        if (failures.Count == 0)
        {
            return;
        }

        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"{failures.Count} of {checkedCount + failures.Count} ");
        message.Append(CultureInfo.InvariantCulture, $"{what} comparisons disagree with the reference");

        foreach ((string key, int value) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            message.Append(CultureInfo.InvariantCulture, $"; {key}: {value}");
        }

        // Enough to diagnose, not enough to bury the count.
        foreach (string failure in failures.Take(20))
        {
            message.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  {failure}");
        }

        if (failures.Count > 20)
        {
            message.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  … and {failures.Count - 20} more");
        }

        Assert.Fail(message.ToString());
    }

    private static IEnumerable<(string Path, string Digest, byte[] Bytes)> Inputs(string root)
    {
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                     .Where(p => p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (IOException)
            {
                continue;
            }

            yield return (path, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
        }
    }

    private static string Name(string path) => Path.GetFileName(path);

    private static bool TryLoad(out Dictionary<string, Entry> manifest, out string root)
    {
        manifest = [];
        root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        string manifestPath = Environment.GetEnvironmentVariable(ManifestVariable) ?? string.Empty;

        if (manifestPath.Length == 0 || root.Length == 0)
        {
            Assert.Skip($"set {ManifestVariable} and {RootVariable} to run the conformance suite");
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        foreach (JsonProperty property in document.RootElement.GetProperty("entries").EnumerateObject())
        {
            manifest[property.Name] = new Entry(
                property.Value.GetProperty("width").GetInt32(),
                property.Value.GetProperty("height").GetInt32(),
                property.Value.GetProperty("source_format").GetString() ?? string.Empty,
                property.Value.GetProperty("rgba_sha256").GetString() ?? string.Empty,
                property.Value.GetProperty("rgba_bytes").GetInt32());
        }

        return true;
    }

    private readonly record struct Entry(
        int Width,
        int Height,
        string SourceFormat,
        string RgbaSha256,
        int RgbaBytes);
}
