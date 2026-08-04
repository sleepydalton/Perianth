using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Png;
using Xunit;

namespace Perianth.Tests.Png;

/// <summary>
/// Checks the PNG reader against the recorded reference manifest.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless both <c>PERIANTH_PNG_MANIFEST</c> and <c>PERIANTH_PNG_ROOT</c>
/// are set, so the default suite stays asset-free.
/// </para>
/// <para>
/// The reason this oracle exists rather than a round trip against our own
/// encoder: an encoder picks one of five scanline filters per line, and ours
/// emits one. A reader that gets Paeth subtly wrong would round-trip perfectly
/// and fail on real files. These 3,279 recorded inputs use all five filters,
/// Paeth in 2,729 of them, and the suite asserts that coverage rather than
/// assuming it.
/// </para>
/// </remarks>
public sealed class PngConformanceTests(ITestOutputHelper output)
{
    private const string ManifestVariable = "PERIANTH_PNG_MANIFEST";
    private const string RootVariable = "PERIANTH_PNG_ROOT";

    private readonly ITestOutputHelper _output = output;

    /// <summary>The colour types this build reads; everything else must refuse.</summary>
    private static bool InScope(Entry entry) =>
        entry.Interlace == 0 && entry.BitDepth == 8 && entry.ColourType is 2 or 6;

    [Fact]
    public void Decoded_pixels_match_the_reference()
    {
        if (!TryLoad(out Dictionary<string, Entry> manifest, out string root))
        {
            return;
        }

        List<string> failures = [];
        Dictionary<string, int> counts = [];
        HashSet<string> seen = [];
        HashSet<int> filters = [];
        int matched = 0;

        foreach ((string path, string digest, byte[] bytes) in Inputs(root))
        {
            if (!manifest.TryGetValue(digest, out Entry entry) || !InScope(entry))
            {
                continue;
            }

            seen.Add(digest);
            counts[entry.SourceKind] = counts.GetValueOrDefault(entry.SourceKind) + 1;

            Result<PngImage> result = PngReader.Read(bytes);
            if (result.IsRefused)
            {
                failures.Add($"{Name(path)}: {entry.SourceKind} refused: {result.Refusal.Message}");
                continue;
            }

            PngImage image = result.Value;

            if (image.Width != entry.Width || image.Height != entry.Height)
            {
                failures.Add(
                    $"{Name(path)}: read {image.Width}x{image.Height}, reference {entry.Width}x{entry.Height}");
                continue;
            }

            string decoded = Convert.ToHexStringLower(SHA256.HashData(image.Pixels));
            if (!string.Equals(decoded, entry.RgbaSha256, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{Name(path)}: {entry.Width}x{entry.Height} filters [{string.Join(' ', entry.Filters)}] decoded to {decoded[..16]}…, reference {entry.RgbaSha256[..16]}…");
                continue;
            }

            filters.UnionWith(entry.Filters);
            matched++;
        }

        Report("decode", matched, counts, failures);

        // Agreement over three thousand files means little if they all use
        // filter 0. This is the claim the suite is actually making.
        Assert.True(
            filters.SetEquals([0, 1, 2, 3, 4]),
            $"the agreeing files exercised only filters [{string.Join(' ', filters.Order())}], so some reconstruction path went unchecked");

        AssertWholeOracleCovered(
            seen,
            manifest.Where(pair => InScope(pair.Value)).Select(pair => pair.Key),
            root);
    }

    [Fact]
    public void Everything_outside_the_supported_set_refuses_by_name()
    {
        if (!TryLoad(out Dictionary<string, Entry> manifest, out string root))
        {
            return;
        }

        List<string> failures = [];
        Dictionary<string, int> counts = [];
        HashSet<string> seen = [];

        foreach ((string path, string digest, byte[] bytes) in Inputs(root))
        {
            if (!manifest.TryGetValue(digest, out Entry entry) || InScope(entry))
            {
                continue;
            }

            seen.Add(digest);
            counts[entry.SourceKind] = counts.GetValueOrDefault(entry.SourceKind) + 1;

            Result<PngImage> result = PngReader.Read(bytes);

            // A partial decode of an interlaced or palette file would be a
            // plausible-looking wrong image, which is the outcome this project
            // refuses to produce.
            if (!result.IsRefused)
            {
                failures.Add($"{Name(path)}: {entry.SourceKind} was decoded rather than refused");
            }
            else if (result.Refusal.Kind != RefusalKind.Unsupported)
            {
                failures.Add($"{Name(path)}: {entry.SourceKind} refused as {result.Refusal.Kind}, not Unsupported");
            }
        }

        Report("refusal", seen.Count - failures.Count, counts, failures);

        AssertWholeOracleCovered(
            seen,
            manifest.Where(pair => !InScope(pair.Value)).Select(pair => pair.Key),
            root);
    }

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

    private void Report(
        string what,
        int checkedCount,
        Dictionary<string, int> counts,
        List<string> failures)
    {
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
                     .Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
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
                property.Value.GetProperty("source_kind").GetString() ?? string.Empty,
                property.Value.GetProperty("colour_type").GetInt32(),
                property.Value.GetProperty("bit_depth").GetInt32(),
                property.Value.GetProperty("interlace").GetInt32(),
                [.. property.Value.GetProperty("filters").EnumerateArray().Select(f => f.GetInt32())],
                property.Value.GetProperty("rgba_sha256").GetString() ?? string.Empty);
        }

        return true;
    }

    private readonly record struct Entry(
        int Width,
        int Height,
        string SourceKind,
        int ColourType,
        int BitDepth,
        int Interlace,
        int[] Filters,
        string RgbaSha256);
}
