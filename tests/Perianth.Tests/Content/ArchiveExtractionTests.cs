using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Perianth.Tests.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks what an extraction selects, writes and records.
/// </summary>
public sealed class ArchiveExtractionTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-extract-");

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>
    /// Three files: two sharing a folder, one elsewhere, so that a folder
    /// request that took everything would be visible.
    /// </summary>
    private SdfContentSource Source()
    {
        SdfContainerBuilder container = new();
        SdfIndexBuilder index = new();

        // chr/a.mmb, chr/b.mmb and other/c.dds, spelled by branching on the
        // characters that actually differ.
        long a = container.AppendToArchive(Pattern(64, 1));
        long b = container.AppendToArchive(Pattern(96, 2));
        long c = container.AppendToArchive(Pattern(32, 3));

        int root = index.Branch('o');
        index.Literal("chr/");
        int inner = index.Branch('b');
        index.Literal("a.mmb").Terminal(chunkCount: 1).Chunk(64, a);

        index.PatchBranch(inner, index.Position);
        index.Literal("b.mmb").Terminal(chunkCount: 1).Chunk(96, b);

        index.PatchBranch(root, index.Position);
        index.Literal("other/c.dds").Terminal(chunkCount: 1).Chunk(32, c);

        container.Index = index.Build();
        container.Write(_directory.FullName);

        return new SdfContentSource(_directory.FullName);
    }

    private static ImmutableArray<SdfPathEntry> Paths(SdfContentSource source)
    {
        Result<ImmutableArray<SdfPathEntry>> walked = source.Paths();
        Assert.False(walked.IsRefused, walked.IsRefused ? walked.Refusal.Message : null);
        return walked.Value;
    }

    [Fact]
    public void A_folder_selects_what_is_under_it_and_nothing_else()
    {
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(Paths(source), "chr/");

        Assert.False(selected.IsRefused, selected.IsRefused ? selected.Refusal.Message : null);
        Assert.Equal(["chr/a.mmb", "chr/b.mmb"], selected.Value.Select(entry => entry.Path));
    }

    [Fact]
    public void A_folder_may_be_named_without_its_separator()
    {
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(Paths(source), "chr");

        Assert.False(selected.IsRefused, selected.IsRefused ? selected.Refusal.Message : null);
        Assert.Equal(2, selected.Value.Length);
    }

    [Fact]
    public void An_exact_path_selects_one_file()
    {
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(Paths(source), "chr/a.mmb");

        Assert.False(selected.IsRefused, selected.IsRefused ? selected.Refusal.Message : null);
        Assert.Equal("chr/a.mmb", Assert.Single(selected.Value).Path);
    }

    [Fact]
    public void A_name_that_is_both_a_file_and_a_folder_is_refused_rather_than_chosen_between()
    {
        // Either answer silently discards the other, and the caller cannot see
        // which happened. The trailing separator says which was meant.
        SdfContainerBuilder container = new();
        SdfIndexBuilder index = new();

        long file = container.AppendToArchive(Pattern(16, 1));
        long under = container.AppendToArchive(Pattern(16, 2));

        index.Literal("thing");
        int patch = index.Branch('/');
        index.Terminal(chunkCount: 1).Chunk(16, file);

        index.PatchBranch(patch, index.Position);
        index.Literal("/inner.dds").Terminal(chunkCount: 1).Chunk(16, under);

        container.Index = index.Build();
        container.Write(_directory.FullName);

        using SdfContentSource source = new(_directory.FullName);

        Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(Paths(source), "thing");

        Assert.True(selected.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, selected.Refusal.Kind);
        Assert.Contains("ambiguous", selected.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_the_archives_do_not_hold_is_refused_by_name()
    {
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(Paths(source), "nowhere/");

        Assert.True(selected.IsRefused);
        Assert.Contains("nowhere/", selected.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_matches_anywhere_in_the_path_and_ignores_case()
    {
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> found = ArchiveExtraction.Find(Paths(source), "CHR/");

        Assert.False(found.IsRefused, found.IsRefused ? found.Refusal.Message : null);
        Assert.Equal(["chr/a.mmb", "chr/b.mmb"], found.Value.Select(entry => entry.Path));
    }

    [Fact]
    public void Find_matches_a_fragment_of_a_file_name()
    {
        // The reason it exists: finding a file whose folder you do not know.
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> found = ArchiveExtraction.Find(Paths(source), "c.dds");

        Assert.Equal("other/c.dds", Assert.Single(found.Value).Path);
    }

    [Fact]
    public void Find_reports_no_match_as_an_empty_result_rather_than_a_refusal()
    {
        // Nothing matching is an ordinary answer; only an unusable query is a
        // refusal.
        using SdfContentSource source = Source();

        Result<ImmutableArray<SdfPathEntry>> found = ArchiveExtraction.Find(Paths(source), "nothing");

        Assert.False(found.IsRefused);
        Assert.Empty(found.Value);

        Assert.True(ArchiveExtraction.Find(Paths(source), string.Empty).IsRefused);
    }

    [Fact]
    public void Extraction_mirrors_the_archive_paths_on_disk()
    {
        // The layout is load-bearing: it is what the loose-file mod loader reads
        // and what --content-root resolves against, so an extraction is directly
        // usable as an export's input.
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, "out");

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, cancellation: TestContext.Current.CancellationToken);

        Assert.False(extracted.IsRefused, extracted.IsRefused ? extracted.Refusal.Message : null);
        Assert.True(File.Exists(Path.Combine(root, "chr", "a.mmb")));
        Assert.True(File.Exists(Path.Combine(root, "chr", "b.mmb")));
        Assert.False(File.Exists(Path.Combine(root, "other", "c.dds")));
        Assert.Equal(Pattern(64, 1), File.ReadAllBytes(Path.Combine(root, "chr", "a.mmb")));
    }

    [Fact]
    public void Every_extracted_file_records_where_it_came_from()
    {
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, "out");

        ExtractionOutcome outcome = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, cancellation: TestContext.Current.CancellationToken).Value;

        ExtractedFile first = outcome.Files[0];
        Assert.Equal("chr/a.mmb", first.VirtualPath);
        Assert.Equal(64, first.Bytes);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Pattern(64, 1))),
            first.Sha256);
        Assert.Equal([0], first.Archives);

        // The manifest is on disk beside what it describes.
        Assert.True(File.Exists(Path.Combine(root, ArchiveExtraction.ManifestName)));
    }

    [Fact]
    public void The_same_extraction_writes_the_same_manifest_bytes()
    {
        // No timestamp and no local paths, so a manifest that travels with a mod
        // says nothing about the machine that made it and can be compared.
        using SdfContentSource source = Source();
        ImmutableArray<SdfPathEntry> selected = ArchiveExtraction.Select(Paths(source), "chr/").Value;

        byte[] first = ArchiveExtraction.Manifest(
            ArchiveExtraction.Extract(source, selected, "chr/", Path.Combine(_directory.FullName, "one"), cancellation: TestContext.Current.CancellationToken).Value);
        byte[] second = ArchiveExtraction.Manifest(
            ArchiveExtraction.Extract(source, selected, "chr/", Path.Combine(_directory.FullName, "two"), cancellation: TestContext.Current.CancellationToken).Value);

        Assert.Equal(first, second);
        Assert.DoesNotContain(_directory.FullName, Encoding.UTF8.GetString(first), StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_above_the_limit_is_refused_before_anything_is_written()
    {
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, "out");

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, limit: 1, cancellation: TestContext.Current.CancellationToken);

        Assert.True(extracted.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, extracted.Refusal.Kind);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_zero_limit_extracts_whatever_was_asked_for()
    {
        using SdfContentSource source = Source();

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source,
            ArchiveExtraction.Select(Paths(source), "chr/").Value,
            "chr/",
            Path.Combine(_directory.FullName, "out"),
            limit: 0, cancellation: TestContext.Current.CancellationToken);

        Assert.False(extracted.IsRefused, extracted.IsRefused ? extracted.Refusal.Message : null);
        Assert.Equal(2, extracted.Value.Files.Length);
    }

    [Fact]
    public void Flattening_writes_the_names_without_the_folders()
    {
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, "out");

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, flatten: true, cancellation: TestContext.Current.CancellationToken);

        Assert.False(extracted.IsRefused, extracted.IsRefused ? extracted.Refusal.Message : null);
        Assert.True(File.Exists(Path.Combine(root, "a.mmb")));
        Assert.False(Directory.Exists(Path.Combine(root, "chr")));

        // Provenance survives the folders: a flattened file still knows where it
        // came from, which is the whole point of recording it at extraction.
        ExtractedFile first = extracted.Value.Files[0];
        Assert.Equal("chr/a.mmb", first.VirtualPath);
        Assert.Equal("a.mmb", first.Output);
    }

    [Fact]
    public void Flattening_two_files_onto_one_name_is_refused_naming_both()
    {
        // The archives hold the same name under many folders, so this is the
        // ordinary case for a broad selection rather than an exotic one. The
        // loss would be silent, and the caller asked for both.
        SdfContainerBuilder container = new();
        SdfIndexBuilder index = new();

        long a = container.AppendToArchive(Pattern(16, 1));
        long b = container.AppendToArchive(Pattern(16, 2));

        index.Literal("one/");
        int patch = index.Branch('t');
        index.Literal("same.dds").Terminal(chunkCount: 1).Chunk(16, a);

        index.PatchBranch(patch, index.Position);
        index.Literal("two/same.dds").Terminal(chunkCount: 1).Chunk(16, b);

        container.Index = index.Build();
        container.Write(_directory.FullName);

        using SdfContentSource source = new(_directory.FullName);
        string root = Path.Combine(_directory.FullName, "out");

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, Paths(source), "everything", root, flatten: true, cancellation: TestContext.Current.CancellationToken);

        Assert.True(extracted.IsRefused);
        Assert.Contains("one/same.dds", extracted.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("two/same.dds", extracted.Refusal.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_path_this_system_takes_but_Windows_would_not_is_reported_rather_than_refused()
    {
        // The extraction is correct here; it is the mod built from it that would
        // travel badly. On Windows the same root is past the hard limit, so the
        // guard refuses instead — both behaviours are the intended one.
        using SdfContentSource source = Source();

        // Depth rather than one long name: a single component over 255
        // characters is refused by the filesystem for a different reason
        // entirely, which would prove nothing about path length.
        string segment = new('d', 80);
        string root = Path.Combine(_directory.FullName, segment, segment, segment);

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, cancellation: TestContext.Current.CancellationToken);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(extracted.IsRefused);
            return;
        }

        Assert.False(extracted.IsRefused, extracted.IsRefused ? extracted.Refusal.Message : null);

        Diagnostic warning = Assert.Single(extracted.Value.Diagnostics);
        Assert.Equal(DiagnosticIds.ExtractionPathNotPortable, warning.Id);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("Windows", warning.Message, StringComparison.Ordinal);

        // Reported, not withheld: every file was still written.
        Assert.Equal(2, extracted.Value.Files.Length);
    }

    [Fact]
    public void Cancelling_reports_what_landed_rather_than_pretending_it_failed()
    {
        // Nothing was wrong with the request, so this is not a refusal. The
        // files already written are real and the manifest lists them, which is
        // what stops a half-finished folder being a mystery later.
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, "out");

        using System.Threading.CancellationTokenSource stop = new();
        stop.Cancel();

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source,
            ArchiveExtraction.Select(Paths(source), "chr/").Value,
            "chr/",
            root,
            cancellation: stop.Token);

        Assert.False(extracted.IsRefused, extracted.IsRefused ? extracted.Refusal.Message : null);
        Assert.True(extracted.Value.Cancelled);
        Assert.Empty(extracted.Value.Files);

        Diagnostic stopped = Assert.Single(extracted.Value.Diagnostics);
        Assert.Equal(DiagnosticIds.ExtractionCancelled, stopped.Id);

        // The manifest still describes the folder, even an empty one.
        Assert.True(File.Exists(Path.Combine(root, ArchiveExtraction.ManifestName)));
    }

    /// <summary>Records reports as they are made, on the calling thread.</summary>
    /// <remarks>
    /// Deliberately not <see cref="Progress{T}"/>, which posts to whichever
    /// synchronization context captured it and so may deliver nothing before the
    /// assertion runs. A test that cannot fail proves nothing.
    /// </remarks>
    private sealed class Recorder : IProgress<int>
    {
        public List<int> Seen { get; } = [];

        public void Report(int value) => Seen.Add(value);
    }

    [Fact]
    public void Progress_counts_up_once_for_every_file_written()
    {
        using SdfContentSource source = Source();
        Recorder progress = new();

        ArchiveExtraction.Extract(
            source,
            ArchiveExtraction.Select(Paths(source), "chr/").Value,
            "chr/",
            Path.Combine(_directory.FullName, "out"),
            progress: progress,
            cancellation: TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], progress.Seen);
    }

    [Fact]
    public void An_ordinary_extraction_says_nothing()
    {
        using SdfContentSource source = Source();

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source,
            ArchiveExtraction.Select(Paths(source), "chr/").Value,
            "chr/",
            Path.Combine(_directory.FullName, "out"), cancellation: TestContext.Current.CancellationToken);

        Assert.Empty(extracted.Value.Diagnostics);
    }

    [Fact]
    public void An_output_directory_leaving_no_room_for_the_path_is_refused_with_the_arithmetic()
    {
        // Windows' 260-character ceiling against archive paths reaching 196:
        // the two meet at a perfectly ordinary output folder. The failure the
        // filesystem raises names no cause, so the refusal has to do the
        // arithmetic itself and say where the budget went.
        using SdfContentSource source = Source();
        string root = Path.Combine(_directory.FullName, new string('d', ArchiveExtraction.MaxPathLength));

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, ArchiveExtraction.Select(Paths(source), "chr/").Value, "chr/", root, cancellation: TestContext.Current.CancellationToken);

        Assert.True(extracted.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, extracted.Refusal.Kind);
        Assert.Contains("Extract somewhere shorter", extracted.Refusal.Message, StringComparison.Ordinal);
        Assert.Contains("chr/a.mmb", extracted.Refusal.Message, StringComparison.Ordinal);

        // Refused before anything was written, so a long root costs nothing but
        // the message.
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_path_that_would_climb_out_of_the_extraction_root_is_refused()
    {
        // Nothing in the shipped index spells this, which is exactly why the
        // guard has to be checked rather than assumed: an archive is an input,
        // and an input that redirects a write is the one that matters.
        SdfContainerBuilder container = new();
        SdfIndexBuilder index = new();

        long payload = container.AppendToArchive(Pattern(16, 1));
        index.Literal("../escaped.dds").Terminal(chunkCount: 1).Chunk(16, payload);
        container.Index = index.Build();
        container.Write(_directory.FullName);

        using SdfContentSource source = new(_directory.FullName);
        string root = Path.Combine(_directory.FullName, "out");

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, Paths(source), "../escaped.dds", root, cancellation: TestContext.Current.CancellationToken);

        Assert.True(extracted.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, extracted.Refusal.Kind);
        Assert.False(File.Exists(Path.Combine(_directory.FullName, "escaped.dds")));
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + seed);
        }

        return bytes;
    }
}
