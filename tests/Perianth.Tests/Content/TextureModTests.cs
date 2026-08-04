using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Core.Imaging;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Checks turning an edited image into a texture, and the mod folder around it.
/// </summary>
public sealed class TextureModTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("perianth-mod-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static byte[] Png(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(i * 11);
        }

        return PngEncoder.Encode(new RgbaImage(width, height, pixels));
    }

    private static ModDetails Details() => new("Green Sign", "somebody", "1.0.0", "A test.");

    [Fact]
    public void An_edited_png_becomes_a_texture_the_reader_accepts()
    {
        Result<byte[]> dds = TextureMod.ToDds(Png(8, 4), withMips: false);

        Assert.False(dds.IsRefused, dds.IsRefused ? dds.Refusal.Message : null);

        Result<DdsImage> read = DdsReader.Read(dds.Value);
        Assert.Equal(8, read.Value.Width);
        Assert.Equal(DdsFormat.Uncompressed32, read.Value.Format);
    }

    [Fact]
    public void The_pixels_survive_the_conversion_exactly()
    {
        // The author's own artwork. Anything less than exact here is silent
        // quality loss on work they did by hand.
        byte[] png = Png(6, 5);
        byte[] dds = TextureMod.ToDds(png, withMips: true).Value;

        Assert.Equal(
            Perianth.Formats.Png.PngReader.Read(png).Value.Pixels.ToArray(),
            DdsReader.Read(dds).Value.Pixels.ToArray());
    }

    [Fact]
    public void Mips_are_written_when_asked_for_and_not_when_not()
    {
        byte[] png = Png(8, 8);

        Assert.Equal(1, DdsReader.ReadHeader(TextureMod.ToDds(png, withMips: false).Value).Value.MipMapCount);
        Assert.Equal(4, DdsReader.ReadHeader(TextureMod.ToDds(png, withMips: true).Value).Value.MipMapCount);
    }

    [Fact]
    public void An_image_this_build_cannot_read_refuses_rather_than_writing_nothing()
    {
        Result<byte[]> dds = TextureMod.ToDds("not a picture"u8, withMips: true);

        Assert.True(dds.IsRefused);
    }

    [Fact]
    public void A_changed_size_is_a_warning_not_a_refusal()
    {
        // Resizing a texture is a thing an author may well mean to do, so this
        // says so and gets out of the way.
        byte[] original = TextureMod.ToDds(Png(8, 8), withMips: true).Value;
        byte[] authored = TextureMod.ToDds(Png(4, 4), withMips: true).Value;

        ImmutableArray<Diagnostic> notes = TextureMod.Compare(authored, original);

        Diagnostic note = Assert.Single(notes);
        Assert.Equal(DiagnosticIds.TextureSizeChanged, note.Id);
        Assert.Equal(DiagnosticSeverity.Warning, note.Severity);
    }

    [Fact]
    public void Dropping_the_mip_chain_is_noticed()
    {
        byte[] original = TextureMod.ToDds(Png(8, 8), withMips: true).Value;
        byte[] authored = TextureMod.ToDds(Png(8, 8), withMips: false).Value;

        Assert.Equal(
            DiagnosticIds.TextureMipsDropped,
            Assert.Single(TextureMod.Compare(authored, original)).Id);
    }

    [Fact]
    public void A_faithful_replacement_says_nothing()
    {
        byte[] original = TextureMod.ToDds(Png(8, 8), withMips: true).Value;

        Assert.Empty(TextureMod.Compare(original, original));
    }

    [Fact]
    public void A_mod_folder_mirrors_the_archive_path_and_carries_a_manifest()
    {
        Result<ModOutcome> written = TextureMod.Write(
            _root,
            Details(),
            [new ModFile("camel/baked/assets/textures/thing/tex_thing.dds", new byte[] { 1, 2, 3 })]);

        Assert.False(written.IsRefused, written.IsRefused ? written.Refusal.Message : null);

        string folder = Path.Combine(_root, "Green Sign");
        Assert.True(File.Exists(Path.Combine(
            folder, "camel", "baked", "assets", "textures", "thing", "tex_thing.dds")));

        string manifest = File.ReadAllText(Path.Combine(folder, "manifest.ini"));
        Assert.Contains("name=Green Sign", manifest, StringComparison.Ordinal);
        Assert.Contains("author=somebody", manifest, StringComparison.Ordinal);
        Assert.Contains("version=1.0.0", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Many_replacements_go_into_one_mod_rather_than_one_each()
    {
        // A mod is a thing a person installs. Five edited textures are one mod
        // to enable, not five to manage.
        Result<ModOutcome> written = TextureMod.Write(
            _root,
            Details(),
            [
                new ModFile("camel/a/one.dds", new byte[] { 1 }),
                new ModFile("camel/b/two.dds", new byte[] { 2 }),
                new ModFile("camel/c/three.dds", new byte[] { 3 }),
            ]);

        Assert.Equal(3, written.Value.Files.Length);
        Assert.Single(Directory.GetDirectories(_root));
        Assert.Single(Directory.GetFiles(written.Value.Folder, "manifest.ini"));
    }

    [Fact]
    public void A_traversing_path_is_refused_rather_than_written()
    {
        // This decides where bytes land on someone's disk, so it is the one
        // place a path check is not a formality.
        foreach (string bad in new[]
        {
            "../outside.dds",
            "camel/../../outside.dds",
            "/absolute.dds",
            "C:/elsewhere.dds",
        })
        {
            Result<ModOutcome> written = TextureMod.Write(
                _root, Details(), [new ModFile(bad, new byte[] { 1 })]);

            Assert.True(written.IsRefused, $"'{bad}' was not refused");
        }
    }

    [Fact]
    public void A_name_that_cannot_be_a_folder_is_refused()
    {
        foreach (string bad in new[] { "", "  ", "with/slash", "with:colon", "trailing." })
        {
            Result<ModOutcome> written = TextureMod.Write(
                _root,
                Details() with { Name = bad },
                [new ModFile("camel/a.dds", new byte[] { 1 })]);

            Assert.True(written.IsRefused, $"'{bad}' was not refused as a folder name");
        }
    }

    [Fact]
    public void A_newline_in_a_detail_cannot_forge_another_key()
    {
        // The ini format has no escape, so a description carrying a newline
        // would otherwise become a key of its own.
        Result<ModOutcome> written = TextureMod.Write(
            _root,
            Details() with { Description = "harmless\nname=Something Else" },
            [new ModFile("camel/a.dds", new byte[] { 1 })]);

        string manifest = File.ReadAllText(Path.Combine(written.Value.Folder, "manifest.ini"));
        string[] lines = manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // The property is one line per key, not the absence of the text. The
        // smuggled "name=" survives inside the description's value, where a
        // reader splitting on the first '=' can only see it as description.
        Assert.Equal(5, lines.Length);
        Assert.Equal(
            ["name", "author", "version", "description", "preloadCustomAssets"],
            lines.Select(line => line[..line.IndexOf('=', StringComparison.Ordinal)]));
        Assert.Equal("name=Green Sign", lines[0]);
    }

    [Fact]
    public void The_manifest_carries_every_key_the_loader_documents()
    {
        // Read off two shipped mods rather than guessed: both write
        // preloadCustomAssets explicitly, in lower case, and one declares its
        // version as "25 WIP" — so version is free text, not a number.
        Result<ModOutcome> written = TextureMod.Write(
            _root,
            new ModDetails("Green Sign", "somebody", "25 WIP", "A test.", PreloadCustomAssets: true),
            [new ModFile("camel/a.dds", new byte[] { 1 })]);

        string manifest = File.ReadAllText(Path.Combine(written.Value.Folder, "manifest.ini"));

        Assert.Equal(
            ["name=Green Sign", "author=somebody", "version=25 WIP", "description=A test.", "preloadCustomAssets=true"],
            manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void Preloading_is_off_unless_it_is_asked_for()
    {
        // The loader's own documentation says to leave it alone when a mod
        // works without it, because turning it on can crash the game. So it is
        // never set on somebody's behalf, and it is written rather than omitted
        // because that is what shipped mods do.
        Result<ModOutcome> written = TextureMod.Write(
            _root, Details(), [new ModFile("camel/a.dds", new byte[] { 1 })]);

        Assert.Contains(
            "preloadCustomAssets=false",
            File.ReadAllText(Path.Combine(written.Value.Folder, "manifest.ini")),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_mod_with_no_files_is_refused()
    {
        Assert.True(TextureMod.Write(_root, Details(), []).IsRefused);
    }

    [Fact]
    public void Writing_the_same_mod_twice_gives_the_same_bytes()
    {
        List<ModFile> files = [new ModFile("camel/a.dds", new byte[] { 1, 2, 3 })];

        TextureMod.Write(_root, Details(), files);
        byte[] first = File.ReadAllBytes(Path.Combine(_root, "Green Sign", "manifest.ini"));

        TextureMod.Write(_root, Details(), files);
        byte[] second = File.ReadAllBytes(Path.Combine(_root, "Green Sign", "manifest.ini"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void An_already_edited_dds_is_taken_verbatim()
    {
        // The documentation always said an extracted .dds could be edited in
        // any editor that reads one, and nothing accepted the result back.
        // Passed through rather than re-encoded: the author made a decision in
        // their own editor, and a block-compressed file is one this build could
        // not re-encode anyway.
        byte[] dds = DdsWriter.Write(new DdsLevel(2, 2, new byte[16])).Value;

        Result<byte[]> imported = TextureMod.Import(dds, withMips: true);

        Assert.False(imported.IsRefused, imported.IsRefused ? imported.Refusal.Message : null);
        Assert.Equal(dds, imported.Value);
    }

    [Fact]
    public void Something_that_is_neither_a_png_nor_a_dds_is_refused()
    {
        Assert.True(TextureMod.Import("not an image at all"u8, withMips: true).IsRefused);
    }

    [Fact]
    public void A_file_claiming_to_be_a_dds_but_malformed_is_refused()
    {
        // Detected by magic rather than by name, so a truncated one must be
        // caught here rather than written into a mod as though it were fine.
        Assert.True(TextureMod.Import("DDS \x00\x00\x00"u8, withMips: true).IsRefused);
    }
}
