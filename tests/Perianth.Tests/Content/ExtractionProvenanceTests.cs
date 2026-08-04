using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks reading back where an extracted file came from.
/// </summary>
/// <remarks>
/// This is what stops the authoring path guessing an archive path from a
/// directory layout. A guess landing one folder out would write a mod the game
/// never looks at, and it would look like it had worked.
/// </remarks>
public sealed class ExtractionProvenanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-prov-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Extracted(string relative, byte[] bytes, string virtualPath, string? digest = null)
    {
        string file = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);

        digest ??= Convert.ToHexStringLower(SHA256.HashData(bytes));
        File.WriteAllText(
            Path.Combine(_root, ArchiveExtraction.ManifestName),
            $$"""
            {
             "request": "test",
             "files": 1,
             "extracted": [
              {
               "path": "{{virtualPath}}",
               "output": "{{relative}}",
               "bytes": {{bytes.Length}},
               "sha256": "{{digest}}",
               "archives": [0]
              }
             ]
            }
            """);

        return file;
    }

    [Fact]
    public void The_archive_path_comes_from_the_manifest_not_the_directory()
    {
        string file = Extracted(
            "camel/baked/assets/textures/thing/tex_thing.dds",
            [1, 2, 3],
            "camel/baked/assets/textures/thing/tex_thing.dds");

        Result<FileProvenance> found = ExtractionProvenance.Of(file);

        Assert.False(found.IsRefused, found.IsRefused ? found.Refusal.Message : null);
        Assert.Equal("camel/baked/assets/textures/thing/tex_thing.dds", found.Value.VirtualPath);
        Assert.True(found.Value.Unmodified);
    }

    [Fact]
    public void A_flattened_extraction_still_resolves()
    {
        // --flat writes names without folders, so there is no layout to infer
        // from at all. The manifest is the only thing that knows.
        string file = Extracted(
            "tex_thing.dds", [1, 2, 3], "camel/baked/assets/textures/thing/tex_thing.dds");

        Assert.Equal(
            "camel/baked/assets/textures/thing/tex_thing.dds",
            ExtractionProvenance.Of(file).Value.VirtualPath);
    }

    [Fact]
    public void A_manifest_several_folders_up_is_found()
    {
        string file = Extracted("a/b/c/d/tex.dds", [7], "camel/x/tex.dds");

        Assert.Equal("camel/x/tex.dds", ExtractionProvenance.Of(file).Value.VirtualPath);
    }

    [Fact]
    public void An_edited_file_keeps_its_path_and_is_reported_as_changed()
    {
        // Pointing at a file already edited in place must still say where it
        // belongs. Refusing would be unhelpful; saying nothing would be wrong.
        string file = Extracted("camel/tex.dds", [1, 2, 3], "camel/tex.dds");
        File.WriteAllBytes(file, [9, 9, 9]);

        Result<FileProvenance> found = ExtractionProvenance.Of(file);

        Assert.Equal("camel/tex.dds", found.Value.VirtualPath);
        Assert.False(found.Value.Unmodified);
    }

    [Fact]
    public void A_file_moved_within_the_tree_is_found_by_its_contents()
    {
        byte[] bytes = [4, 5, 6];
        Extracted("camel/tex.dds", bytes, "camel/tex.dds");

        string moved = Path.Combine(_root, "renamed.dds");
        File.WriteAllBytes(moved, bytes);

        Assert.Equal("camel/tex.dds", ExtractionProvenance.Of(moved).Value.VirtualPath);
    }

    [Fact]
    public void A_second_extraction_into_the_same_folder_keeps_the_first_ones_record()
    {
        // Found by using the tool on somebody else's mod. Extracting one file
        // at a time into one folder is the ordinary way to work, and each run
        // overwriting the manifest left every file but the last with no
        // recorded origin -- silently, because the files themselves are fine.
        ExtractedFile first = new("camel/a/one.dds", "camel/a/one.dds", 3, Digest([1, 2, 3]), [0]);
        ExtractedFile second = new("camel/b/two.dds", "camel/b/two.dds", 2, Digest([4, 5]), [0]);

        byte[] after = ArchiveExtraction.Manifest(
            new ExtractionOutcome("camel/b/two.dds", [second], ArchiveExtraction.ManifestName, []),
            [first]);

        Assert.Contains("camel/a/one.dds", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
        Assert.Contains("camel/b/two.dds", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
    }

    [Fact]
    public void Re_extracting_a_file_replaces_its_record_rather_than_doubling_it()
    {
        ExtractedFile before = new("camel/a/one.dds", "camel/a/one.dds", 3, Digest([1, 2, 3]), [0]);
        ExtractedFile now = new("camel/a/one.dds", "camel/a/one.dds", 2, Digest([9, 9]), [1]);

        string after = Encoding.UTF8.GetString(ArchiveExtraction.Manifest(
            new ExtractionOutcome("camel/a/one.dds", [now], ArchiveExtraction.ManifestName, []),
            [before]));

        Assert.Contains(Digest([9, 9]), after, StringComparison.Ordinal);
        Assert.DoesNotContain(Digest([1, 2, 3]), after, StringComparison.Ordinal);
    }

    private static string Digest(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public void A_file_with_no_manifest_anywhere_refuses_and_says_what_to_do()
    {
        string file = Path.Combine(_root, "loose.dds");
        File.WriteAllBytes(file, [1]);

        Result<FileProvenance> found = ExtractionProvenance.Of(file);

        Assert.True(found.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, found.Refusal.Kind);
        Assert.Contains("archive path", found.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlisted_file_beside_a_manifest_refuses_rather_than_guessing()
    {
        Extracted("camel/tex.dds", [1], "camel/tex.dds");

        string stranger = Path.Combine(_root, "camel", "other.dds");
        File.WriteAllBytes(stranger, [2]);

        Assert.True(ExtractionProvenance.Of(stranger).IsRefused);
    }

    [Fact]
    public void A_missing_file_is_a_resource_refusal()
    {
        Result<FileProvenance> found = ExtractionProvenance.Of(Path.Combine(_root, "nothing.dds"));

        Assert.True(found.IsRefused);
        Assert.Equal(RefusalKind.Resource, found.Refusal.Kind);
    }

    [Fact]
    public void An_unreadable_manifest_is_malformed_rather_than_a_crash()
    {
        string file = Path.Combine(_root, "tex.dds");
        File.WriteAllBytes(file, [1]);
        File.WriteAllText(
            Path.Combine(_root, ArchiveExtraction.ManifestName), "{ this is not json");

        Result<FileProvenance> found = ExtractionProvenance.Of(file);

        Assert.True(found.IsRefused);
        Assert.Equal(RefusalKind.Malformed, found.Refusal.Kind);
    }
}
