using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using Perianth.Core.Imaging;
using Perianth.Core.Io;
using Perianth.Formats.Dds;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Png;

namespace Perianth.Core.Content;

/// <summary>One file a mod puts in front of the game's own.</summary>
/// <param name="VirtualPath">The archive path this stands in for.</param>
/// <param name="Bytes">What to write there.</param>
public sealed record ModFile(string VirtualPath, ReadOnlyMemory<byte> Bytes);

/// <summary>Who made a mod, for the loader's overlay to show.</summary>
/// <param name="Name">The folder name and the displayed name.</param>
/// <param name="Author">Whoever made it.</param>
/// <param name="Version">
/// Their version string, unparsed. Free text rather than a number: a shipped
/// mod was observed declaring <c>25 WIP</c>.
/// </param>
/// <param name="Description">One line about it.</param>
/// <param name="PreloadCustomAssets">
/// The loader's optional wider asset support. Off unless asked for: it is left
/// alone when a mod already works without it, as it may cause crashes.
/// </param>
public sealed record ModDetails(
    string Name,
    string Author,
    string Version,
    string Description,
    bool PreloadCustomAssets = false);

/// <summary>What writing a mod produced.</summary>
/// <param name="Folder">The mod folder, ready to drop in.</param>
/// <param name="Files">The virtual paths written, in order.</param>
/// <param name="Diagnostics">Anything worth saying about a run that succeeded.</param>
public sealed record ModOutcome(
    string Folder,
    ImmutableArray<string> Files,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Turns an edited image into a texture the game loads, and assembles the mod
/// folder around it.
/// </summary>
/// <remarks>
/// <para>
/// The conversion writes an uncompressed DDS, which the engine accepts for a
/// material (Roadmap §6.9). That is the whole reason this is a few hundred
/// lines rather than a block encoder: an author edits a PNG in whatever they
/// already own, and nothing in the workflow needs a texture plugin.
/// </para>
/// <para>
/// The layout is the loader's own — a folder holding <c>manifest.ini</c> and
/// the game's paths mirrored beneath it — which is the same tree
/// <see cref="ArchiveExtraction"/> writes. So an extraction, an edit and a mod
/// are three states of one directory shape, and none of them needs repacking an
/// archive.
/// </para>
/// </remarks>
public static class TextureMod
{
    private const string ManifestName = "manifest.ini";

    /// <summary>
    /// Converts <paramref name="png"/> into an uncompressed DDS.
    /// </summary>
    /// <param name="png">The edited image, as a PNG file's bytes.</param>
    /// <param name="withMips">
    /// Whether to build the successively halved levels. The engine loads a
    /// texture without them — tested — so this is a quality choice, not a
    /// compatibility one: without a chain, a surface shimmers at distance
    /// rather than failing to appear.
    /// </param>
    /// <summary>
    /// Takes whichever of the two an author actually has: a PNG to convert, or
    /// a DDS already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The documentation has always said you may edit the extracted <c>.dds</c>
    /// in any editor that reads one, and until now nothing accepted the result
    /// back. Detected by magic rather than by file extension, because the name
    /// is the one part of a file anybody can get wrong.
    /// </para>
    /// <para>
    /// A DDS passes through byte for byte, and is read only to check it is one.
    /// Re-encoding it would be the tool overwriting a decision the author made
    /// in their own editor — including a block-compressed file, which the game
    /// ships and loads and this build has no encoder for.
    /// </para>
    /// </remarks>
    public static Result<byte[]> Import(ReadOnlySpan<byte> file, bool withMips)
    {
        if (!file.StartsWith("DDS "u8))
        {
            return ToDds(file, withMips);
        }

        Result<DdsImage> read = DdsReader.Read(file);
        return read.IsRefused ? read.Refusal : Result.Ok(file.ToArray());
    }

    public static Result<byte[]> ToDds(ReadOnlySpan<byte> png, bool withMips)
    {
        Result<PngImage> read = PngReader.Read(png);
        if (!read.TryGetValue(out PngImage? image, out Refusal? refusal))
        {
            return refusal;
        }

        RgbaImage rgba = new(image.Width, image.Height, image.Pixels.ToArray());

        if (!withMips)
        {
            return DdsWriter.Write(new DdsLevel(rgba.Width, rgba.Height, rgba.Pixels.ToArray()));
        }

        List<DdsLevel> levels = [];
        foreach (RgbaImage level in MipChain.Build(rgba))
        {
            levels.Add(new DdsLevel(level.Width, level.Height, level.Pixels.ToArray()));
        }

        return DdsWriter.Write(levels);
    }

    /// <summary>
    /// Compares an authored texture against the original it replaces, and says
    /// what a modder would want to know.
    /// </summary>
    /// <remarks>
    /// Warnings, never refusals. Whether a replacement is a good idea is the
    /// author's call — they may well mean to change a texture's size — and this
    /// tool guarantees the bytes rather than certifying the result. What it can
    /// do is notice the mistakes that are almost never deliberate.
    /// </remarks>
    public static ImmutableArray<Diagnostic> Compare(
        ReadOnlySpan<byte> authored, ReadOnlySpan<byte> original)
    {
        ImmutableArray<Diagnostic>.Builder notes = ImmutableArray.CreateBuilder<Diagnostic>();

        Result<DdsHeader> before = DdsReader.ReadHeader(original);
        Result<DdsHeader> after = DdsReader.ReadHeader(authored);

        if (!before.TryGetValue(out DdsHeader was, out _) ||
            !after.TryGetValue(out DdsHeader now, out _))
        {
            return notes.ToImmutable();
        }

        if (was.Width != now.Width || was.Height != now.Height)
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.TextureSizeChanged,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The original is {was.Width}x{was.Height} and this is {now.Width}x{now.Height}. The game will stretch it over the same surface.")));
        }

        // Measured: 46,890 of the 47,321 textures in the archives ship a full
        // chain. One level is the mistake a first attempt makes, not a choice.
        if (was.MipMapCount > 1 && now.MipMapCount <= 1)
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.TextureMipsDropped,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The original carries {was.MipMapCount} mip levels and this carries {now.MipMapCount}. It will load, but it may shimmer at a distance.")));
        }

        return notes.ToImmutable();
    }

    /// <summary>
    /// Writes <paramref name="files"/> into one mod folder beneath
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// One folder for however many replacements, because a mod is a thing a
    /// person installs and enables. Writing a folder apiece would make five
    /// edited textures into five mods to manage, which is not what anyone
    /// making them means.
    /// </remarks>
    public static Result<ModOutcome> Write(
        string root, ModDetails details, IReadOnlyList<ModFile> files)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return Refusal.Unsupported("A mod needs at least one file to put in front of the game's.");
        }

        Result<string> named = FolderName(details.Name);
        if (!named.TryGetValue(out string? folderName, out Refusal? refusal))
        {
            return refusal;
        }

        string folder = Path.Combine(root, folderName);
        ImmutableArray<string>.Builder written = ImmutableArray.CreateBuilder<string>(files.Count);
        ImmutableArray<Diagnostic>.Builder notes = ImmutableArray.CreateBuilder<Diagnostic>();

        foreach (ModFile file in files)
        {
            Result<string> checkedPath = ArchivePath(file.VirtualPath);
            if (!checkedPath.TryGetValue(out string? virtualPath, out Refusal? bad))
            {
                return bad;
            }

            string output = Path.Combine(
                folder, virtualPath.Replace('/', Path.DirectorySeparatorChar));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refusal.Resource(
                    $"'{Path.GetDirectoryName(output)}' could not be created.",
                    DiagnosticIds.ResourceMissing);
            }

            Result<int> published = AtomicFile.Publish(output, file.Bytes.ToArray());
            if (published.IsRefused)
            {
                return published.Refusal;
            }

            written.Add(virtualPath);
        }

        Result<int> manifest = AtomicFile.Publish(
            Path.Combine(folder, ManifestName), ManifestBytes(details));

        if (manifest.IsRefused)
        {
            return manifest.Refusal;
        }

        return Result.Ok(new ModOutcome(folder, written.ToImmutable(), notes.ToImmutable()));
    }

    /// <summary>
    /// The loader's manifest: five keys, one per line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Newlines and carriage returns are stripped from every value rather than
    /// escaped, because the format has no escape and a value carrying one would
    /// silently become a different key.
    /// </para>
    /// <para>
    /// <c>preloadCustomAssets</c> is written even when false, and spelled in
    /// lower case, because that is what shipped mods do — two were read to
    /// settle it rather than guessing at the casing a boolean wants.
    /// </para>
    /// </remarks>
    private static byte[] ManifestBytes(ModDetails details)
    {
        StringBuilder text = new();
        text.Append("name=").Append(OneLine(details.Name)).Append('\n');
        text.Append("author=").Append(OneLine(details.Author)).Append('\n');
        text.Append("version=").Append(OneLine(details.Version)).Append('\n');
        text.Append("description=").Append(OneLine(details.Description)).Append('\n');
        text.Append("preloadCustomAssets=")
            .Append(details.PreloadCustomAssets ? "true" : "false")
            .Append('\n');

        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static string OneLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    /// <summary>
    /// Accepts a folder name that is safe on every platform this runs on.
    /// </summary>
    private static Result<string> FolderName(string name)
    {
        string trimmed = OneLine(name);

        if (trimmed.Length == 0)
        {
            return Refusal.Unsupported("A mod needs a name, which becomes its folder.");
        }

        foreach (char c in trimmed)
        {
            // The Windows set, applied everywhere, so a mod folder made on one
            // machine is installable on another.
            if (c < 0x20 || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')
            {
                return Refusal.Unsupported(
                    $"'{trimmed}' cannot be a folder name: it contains '{c}'.");
            }
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            return Refusal.Unsupported($"'{trimmed}' cannot be a folder name: it ends in a dot or a space.");
        }

        return Result.Ok(trimmed);
    }

    /// <summary>
    /// Checks a virtual path can only name a file beneath the mod folder.
    /// </summary>
    /// <remarks>
    /// The same rule <see cref="TexturePath"/> applies, and for a sharper
    /// reason: this one decides where a file is <em>written</em>, so a traversal
    /// component would put a mod's contents somewhere the author did not
    /// choose.
    /// </remarks>
    private static Result<string> ArchivePath(string virtualPath)
    {
        string normalized = virtualPath.Replace('\\', '/').Trim();

        if (normalized.Length == 0 || normalized.StartsWith('/'))
        {
            return Refusal.Unsupported($"'{virtualPath}' is not a path inside the archives.");
        }

        foreach (string component in normalized.Split('/'))
        {
            if (component.Length == 0 ||
                component == "." ||
                component == ".." ||
                component.Contains(':', StringComparison.Ordinal))
            {
                return Refusal.Unsupported($"'{virtualPath}' is not a path inside the archives.");
            }
        }

        return Result.Ok(normalized);
    }
}
