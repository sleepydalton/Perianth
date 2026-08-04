using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Sdf;

/// <summary>
/// Checks the SDF reader against the recorded reference manifest.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless both <c>PERIANTH_SDF_MANIFEST</c> and <c>PERIANTH_SDF_ROOT</c>
/// are set, so the default suite stays synthetic and asset-free.
/// </para>
/// <para>
/// This is the manifest that covers exactly the textures the baseline
/// specimens read. It is the depth oracle to the BC manifest's breadth: the
/// two describe disjoint populations, and neither substitutes for the other.
/// </para>
/// </remarks>
public sealed class SdfConformanceTests(ITestOutputHelper output)
{
    private const string ManifestVariable = "PERIANTH_SDF_MANIFEST";
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_recorded_path_resolves_to_the_reference_bytes()
    {
        if (!TryLoad(out JsonElement manifest, out string root))
        {
            return;
        }

        using SdfContentSource source = new(root);
        List<string> failures = [];
        int matched = 0;

        foreach (JsonProperty entry in manifest.GetProperty("entries").EnumerateObject())
        {
            Result<SdfContent> result = source.Read(entry.Name);

            if (result.IsRefused)
            {
                failures.Add($"{Tail(entry.Name)}: refused: {result.Refusal.Message}");
                continue;
            }

            if (!result.Value.IsPresent)
            {
                failures.Add($"{Tail(entry.Name)}: reported absent");
                continue;
            }

            ReadOnlySpan<byte> bytes = result.Value.Bytes.Span;
            int expectedLength = entry.Value.GetProperty("bytes").GetInt32();

            if (bytes.Length != expectedLength)
            {
                failures.Add($"{Tail(entry.Name)}: resolved {bytes.Length} bytes, reference {expectedLength}");
                continue;
            }

            string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string expected = entry.Value.GetProperty("sha256").GetString() ?? string.Empty;

            if (!string.Equals(digest, expected, StringComparison.Ordinal))
            {
                failures.Add($"{Tail(entry.Name)}: resolved to {digest[..16]}…, reference {expected[..16]}…");
                continue;
            }

            matched++;
        }

        Report("resolve", matched, failures);
    }

    [Fact]
    public void The_archive_index_is_case_insensitive()
    {
        if (!TryLoad(out JsonElement manifest, out string root))
        {
            return;
        }

        using SdfContentSource source = new(root);
        List<string> failures = [];
        int matched = 0;

        // Spec 3 makes SDF index lookup case-insensitive while loose lookup is
        // not, so this is source-format behaviour rather than a corpus
        // accident, and the manifest recorded the reference agreeing.
        foreach (JsonProperty probe in manifest.GetProperty("case_variants").EnumerateObject())
        {
            string canonical = probe.Value.GetProperty("resolves_to").GetString() ?? string.Empty;
            bool referenceMatched = probe.Value.GetProperty("matches").GetBoolean();

            Result<SdfContent> variant = source.Read(probe.Name);
            Result<SdfContent> original = source.Read(canonical);

            if (variant.IsRefused || original.IsRefused)
            {
                failures.Add($"{Tail(probe.Name)}: refused");
                continue;
            }

            bool agrees = variant.Value.IsPresent &&
                original.Value.IsPresent &&
                variant.Value.Bytes.Span.SequenceEqual(original.Value.Bytes.Span);

            if (agrees != referenceMatched)
            {
                failures.Add($"{Tail(probe.Name)}: matched={agrees}, reference={referenceMatched}");
                continue;
            }

            matched++;
        }

        Report("case variant", matched, failures);
    }

    [Fact]
    public void Absent_paths_report_absence_rather_than_resolving_something_nearby()
    {
        if (!TryLoad(out JsonElement manifest, out string root))
        {
            return;
        }

        using SdfContentSource source = new(root);
        List<string> failures = [];
        int matched = 0;

        // Three of these are real prefixes of real entries, so a reader that
        // descends the index and accepts the terminal it lands nearest returns
        // a file for them. Only a negative case catches that.
        foreach (JsonProperty probe in manifest.GetProperty("absent").EnumerateObject())
        {
            if (probe.Value.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            Result<SdfContent> result = source.Read(probe.Name);

            if (result.IsRefused)
            {
                failures.Add($"{Tail(probe.Name)}: refused rather than reporting absence: {result.Refusal.Message}");
                continue;
            }

            if (result.Value.IsPresent)
            {
                failures.Add($"{Tail(probe.Name)}: resolved {result.Value.Bytes.Length} bytes, reference reports absent");
                continue;
            }

            matched++;
        }

        Report("absence probe", matched, failures);
    }

    [Fact]
    public void Resolved_textures_decode_to_the_reference_pixels()
    {
        if (!TryLoad(out JsonElement manifest, out string root))
        {
            return;
        }

        using SdfContentSource source = new(root);
        List<string> failures = [];
        int matched = 0;
        Dictionary<string, int> byFormat = [];

        // This is the join between the two oracles. Until now the BC decoder
        // was conformant only against loose corpus files, none of which is one
        // of these: the archives hold a different build of the same textures.
        // Reading a texture out of the container and decoding it exercises
        // both readers against the pixels the baseline specimens compare.
        foreach (JsonProperty entry in manifest.GetProperty("entries").EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("decoded", out JsonElement decoded) ||
                decoded.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            Result<SdfContent> content = source.Read(entry.Name);
            if (content.IsRefused || !content.Value.IsPresent)
            {
                failures.Add($"{Tail(entry.Name)}: could not be read out of the archives");
                continue;
            }

            Result<DdsImage> image = DdsReader.Read(content.Value.Bytes.Span);
            if (image.IsRefused)
            {
                failures.Add($"{Tail(entry.Name)}: decode refused: {image.Refusal.Message}");
                continue;
            }

            string format = entry.Value.GetProperty("source_format").GetString() ?? "?";
            string digest = Convert.ToHexStringLower(SHA256.HashData(image.Value.Pixels));
            string expected = entry.Value.GetProperty("rgba_sha256").GetString() ?? string.Empty;

            if (!string.Equals(digest, expected, StringComparison.Ordinal))
            {
                failures.Add($"{Tail(entry.Name)}: {format} decoded to {digest[..16]}…, reference {expected[..16]}…");
                continue;
            }

            byFormat[format] = byFormat.GetValueOrDefault(format) + 1;
            matched++;
        }

        Report("archive texture decode", matched, failures, byFormat);
    }

    [Fact]
    public void The_walk_and_the_descent_agree_across_the_whole_index()
    {
        if (!TryLoad(out JsonElement manifest, out string root))
        {
            return;
        }

        using SdfContentSource source = new(root);

        Result<ImmutableArray<SdfPathEntry>> walked = source.Paths();
        Assert.False(walked.IsRefused, walked.IsRefused ? walked.Refusal.Message : null);

        Result<SdfToc> toc = source.Toc();
        Assert.False(toc.IsRefused, toc.IsRefused ? toc.Refusal.Message : null);
        ReadOnlyMemory<byte> table = toc.Value.FileTable;

        List<string> failures = [];
        Dictionary<string, int> counts = [];
        HashSet<int> terminals = [];
        HashSet<string> listed = new(StringComparer.Ordinal);
        int matched = 0;

        foreach (SdfPathEntry entry in walked.Value)
        {
            // Distinct terminals prove no subtree is shared, which is what makes
            // the walk's node budget a sound cycle guard rather than a limit
            // that could fire on a legitimate tree.
            if (!terminals.Add(entry.NodeOffset))
            {
                failures.Add($"{Tail(entry.Path)}: a second path reaches the terminal at 0x{entry.NodeOffset:X}");
                continue;
            }

            listed.Add(SdfIndex.NormalizePath(entry.Path));

            // The descent reads the same grammar in the other direction and
            // chooses one child where the walk takes both, so agreeing on every
            // path in the real index is what says the walk explores the tree the
            // container actually spells.
            Result<SdfEntry?> found = SdfIndex.Lookup(table.Span, entry.Path, toc.Value.Layout);

            if (found.IsRefused)
            {
                failures.Add($"{Tail(entry.Path)}: the descent refused a path the walk spelled: {found.Refusal.Message}");
                continue;
            }

            if (found.Value is null)
            {
                failures.Add($"{Tail(entry.Path)}: the walk spelled a path the descent cannot find");
                continue;
            }

            counts[entry.IsDirectory ? "directories" : "files"] =
                counts.GetValueOrDefault(entry.IsDirectory ? "directories" : "files") + 1;
            matched++;
        }

        // …and the other direction: everything the manifest records is in the
        // listing. A walk that quietly stopped early would agree on everything
        // it did spell, so only this catches it.
        foreach (JsonProperty entry in manifest.GetProperty("entries").EnumerateObject())
        {
            if (!listed.Contains(SdfIndex.NormalizePath(entry.Name)))
            {
                failures.Add($"{Tail(entry.Name)}: recorded in the manifest but absent from the walk");
            }
        }

        Report("index walk", matched, failures, counts);
    }

    private void Report(
        string what,
        int matched,
        List<string> failures,
        Dictionary<string, int>? counts = null)
    {
        StringBuilder coverage = new();
        coverage.Append(CultureInfo.InvariantCulture, $"{what}: {matched} agreed, {failures.Count} disagreed");

        foreach ((string key, int value) in (counts ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            coverage.Append(CultureInfo.InvariantCulture, $"; {key}: {value}");
        }

        _output.WriteLine(coverage.ToString());

        Assert.True(matched > 0, $"no {what} comparisons ran, so this proved nothing");

        if (failures.Count == 0)
        {
            return;
        }

        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"{failures.Count} of {matched + failures.Count} {what} comparisons disagree with the reference");

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

    private static string Tail(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static bool TryLoad(out JsonElement manifest, out string root)
    {
        root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        string manifestPath = Environment.GetEnvironmentVariable(ManifestVariable) ?? string.Empty;

        if (manifestPath.Length == 0 || root.Length == 0)
        {
            manifest = default;
            Assert.Skip($"set {ManifestVariable} and {RootVariable} to run the SDF conformance suite");
            return false;
        }

        manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath)).RootElement;
        return true;
    }
}
