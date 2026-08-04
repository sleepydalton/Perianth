using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Content;

/// <summary>Where a file on disk came from, according to the extraction that wrote it.</summary>
/// <param name="VirtualPath">The path inside the archives.</param>
/// <param name="Sha256">The digest recorded when it was extracted.</param>
/// <param name="Unmodified">Whether the file still has that digest.</param>
/// <param name="Manifest">The manifest this was read from.</param>
public sealed record FileProvenance(
    string VirtualPath,
    string Sha256,
    bool Unmodified,
    string Manifest);

/// <summary>
/// Answers what an extracted file was, by reading the manifest beside it.
/// </summary>
/// <remarks>
/// <para>
/// A file on disk does not say which archive path it came from, and the whole
/// reason <see cref="ArchiveExtraction"/> writes a manifest is that this cannot
/// be recovered afterwards. So authoring reads it back rather than inferring
/// from the directory layout: an extraction made with <c>--flat</c> has no
/// layout to infer from, and a guess that lands one folder out would put a
/// modded texture where the game never looks.
/// </para>
/// <para>
/// Identity comes from the recorded output path; the digest is then compared
/// separately and reported rather than required. A caller pointing at a file it
/// has already edited still gets the right virtual path, and is told the bytes
/// have changed instead of being refused over it.
/// </para>
/// </remarks>
public static class ExtractionProvenance
{
    /// <summary>
    /// Finds what <paramref name="file"/> was extracted from.
    /// </summary>
    /// <remarks>
    /// Searches the file's own directory and every ancestor for the manifest,
    /// because the extraction root is wherever the caller chose and the file may
    /// be several folders below it.
    /// </remarks>
    public static Result<FileProvenance> Of(string file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string full;
        try
        {
            full = Path.GetFullPath(file);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return Refusal.Unsupported($"'{file}' is not a usable path.");
        }

        if (!File.Exists(full))
        {
            return Refusal.Resource($"There is no file at '{file}'.", DiagnosticIds.ResourceMissing);
        }

        for (DirectoryInfo? at = new FileInfo(full).Directory; at is not null; at = at.Parent)
        {
            string manifest = Path.Combine(at.FullName, ArchiveExtraction.ManifestName);
            if (!File.Exists(manifest))
            {
                continue;
            }

            return Match(full, at.FullName, manifest);
        }

        return Refusal.Unsupported(
            $"No {ArchiveExtraction.ManifestName} was found beside '{file}' or above it, so there is "
            + "nothing recording which archive path it came from. Extract it with this tool, or name "
            + "the archive path directly.");
    }

    private static Result<FileProvenance> Match(string file, string root, string manifest)
    {
        // The manifest records each output relative to the extraction root,
        // which is the directory the manifest itself sits in.
        string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

        byte[] manifestBytes;
        byte[] fileBytes;
        try
        {
            manifestBytes = File.ReadAllBytes(manifest);
            fileBytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{manifest}' could not be read.", DiagnosticIds.ResourceMissing);
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData(fileBytes));

        Dictionary<string, (string Path, string Sha)> byOutput = new(StringComparer.Ordinal);
        Dictionary<string, (string Path, string Sha)> bySha = new(StringComparer.Ordinal);

        try
        {
            using JsonDocument document = JsonDocument.Parse(manifestBytes);
            if (!document.RootElement.TryGetProperty("extracted", out JsonElement extracted))
            {
                return Refusal.Malformed($"'{manifest}' lists no extracted files.");
            }

            foreach (JsonElement entry in extracted.EnumerateArray())
            {
                string? path = entry.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
                string? output = entry.TryGetProperty("output", out JsonElement o) ? o.GetString() : null;
                string? sha = entry.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null;

                if (path is null || output is null || sha is null)
                {
                    continue;
                }

                byOutput[output.Replace('\\', '/')] = (path, sha);
                bySha.TryAdd(sha, (path, sha));
            }
        }
        catch (JsonException)
        {
            return Refusal.Malformed($"'{manifest}' is not readable as a provenance manifest.");
        }

        if (byOutput.TryGetValue(relative, out (string Path, string Sha) found))
        {
            return Result.Ok(new FileProvenance(
                found.Path,
                found.Sha,
                string.Equals(found.Sha, digest, StringComparison.Ordinal),
                manifest));
        }

        // Moved or renamed inside the tree: the bytes still identify it, and
        // saying so beats refusing a file whose origin is not in doubt.
        if (bySha.TryGetValue(digest, out (string Path, string Sha) moved))
        {
            return Result.Ok(new FileProvenance(moved.Path, moved.Sha, Unmodified: true, manifest));
        }

        // Only strings are interpolated here, so there is nothing for a culture
        // to format differently.
        return Refusal.Unsupported(
            $"'{relative}' is not listed in {ArchiveExtraction.ManifestName}, and nothing there has its contents, "
            + "so which archive path it came from is not recorded. Name the archive path directly.");
    }
}
