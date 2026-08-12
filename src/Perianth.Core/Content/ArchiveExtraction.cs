using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;
using Perianth.Formats.Sdf;

namespace Perianth.Core.Content;

/// <summary>
/// One file taken out of the archives, and where it came from.
/// </summary>
/// <param name="VirtualPath">The path inside the container.</param>
/// <param name="Output">The written file, relative to the extraction root.</param>
/// <param name="Bytes">Its decoded length.</param>
/// <param name="Sha256">The digest of the bytes as extracted.</param>
/// <param name="Archives">Every archive the file's chunks came from, ascending.</param>
public sealed record ExtractedFile(
    string VirtualPath,
    string Output,
    long Bytes,
    string Sha256,
    ImmutableArray<int> Archives);

/// <summary>What one extraction wrote.</summary>
/// <param name="Request">The path asked for, normalized.</param>
/// <param name="Files">Everything written, ordered by virtual path.</param>
/// <param name="Manifest">The provenance manifest, relative to the extraction root.</param>
/// <param name="Diagnostics">
/// Anything worth saying about a run that nonetheless succeeded. Empty is the
/// ordinary case.
/// </param>
/// <param name="Cancelled">
/// Whether the caller stopped it partway.
/// </param>
/// <remarks>
/// A cancelled extraction is still described rather than discarded: the files
/// that landed are real, and the manifest lists them. Cancelling is not a
/// refusal — nothing was wrong with the request — so it is reported in the
/// result instead of pretending the container was at fault.
/// </remarks>
public sealed record ExtractionOutcome(
    string Request,
    ImmutableArray<ExtractedFile> Files,
    string Manifest,
    ImmutableArray<Diagnostic> Diagnostics,
    bool Cancelled = false);

/// <summary>
/// Takes files out of an SDF archive set and onto disk, recording where each
/// one came from.
/// </summary>
/// <remarks>
/// <para>
/// The written tree mirrors the container's own paths, because that is the
/// layout the loose-file mod loader reads and the layout <c>--content-root</c>
/// resolves against. An extraction is therefore directly usable as the input to
/// an export, and a modded file stays where the game will look for it.
/// </para>
/// <para>
/// Provenance is recorded at extraction time because it cannot be recovered
/// afterwards: a file on disk does not say which virtual path it came from, and
/// a later diff against the original needs exactly that. It costs one manifest
/// now and is not reconstructable later.
/// </para>
/// </remarks>
public static class ArchiveExtraction
{
    /// <summary>The manifest's name inside the extraction root.</summary>
    public const string ManifestName = "perianth-extraction.json";

    /// <summary>
    /// How many files a folder request writes before it must be confirmed.
    /// </summary>
    /// <remarks>
    /// A guard against the request that means more than it looks like it means,
    /// not a capacity limit. The container's animation folder holds 68,561
    /// files in one flat directory, so asking for a folder is not evidence of
    /// wanting all of it.
    /// </remarks>
    public const int DefaultLimit = 2000;

    /// <summary>
    /// The longest full path this system will accept for an extracted file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Windows' <c>MAX_PATH</c> is 260 characters including the terminator, so
    /// 259 is the usable length. Long-path support can lift it, but it is off by
    /// default and depends on a machine-wide setting this tool does not control,
    /// so the conservative limit is the honest one to plan against: an
    /// extraction that fits is portable, and one that does not would have failed
    /// on some other machine instead.
    /// </para>
    /// <para>
    /// Elsewhere the practical ceiling is the kernel's, and it is far out of
    /// reach of anything in this container — the longest archive path is 196
    /// characters.
    /// </para>
    /// <para>
    /// Public because a front end wants to warn while a folder is being chosen,
    /// not after a thousand files have been written.
    /// </para>
    /// </remarks>
    public static int MaxPathLength => OperatingSystem.IsWindows() ? WindowsPathLength : 4095;

    /// <summary>
    /// The usable length of a full path on a default Windows install.
    /// </summary>
    /// <remarks>
    /// <c>MAX_PATH</c> is 260 including the terminator. Named separately from
    /// <see cref="MaxPathLength"/> because it is also what an extraction on
    /// another system is judged against when asking whether the result would
    /// travel — the limit that matters there is the reader's, not the writer's.
    /// </remarks>
    public const int WindowsPathLength = 259;

    /// <summary>
    /// The paths one request names, without reading or writing any of them.
    /// </summary>
    /// <remarks>
    /// A request is either an exact path or a folder. A folder may be written
    /// with or without its trailing separator; naming something that is both a
    /// file and a folder is refused rather than resolved, because either answer
    /// silently discards the other.
    /// </remarks>
    public static Result<ImmutableArray<SdfPathEntry>> Select(
        ImmutableArray<SdfPathEntry> paths,
        string request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string wanted = SdfIndex.NormalizePath(request).TrimStart('/');
        if (wanted.Length == 0)
        {
            return Refusal.Unsupported("--path names the file or folder to extract, and cannot be empty.");
        }

        string folder = wanted.EndsWith('/') ? wanted : wanted + "/";
        string exact = wanted.TrimEnd('/');
        bool exactAsked = !wanted.EndsWith('/');

        ImmutableArray<SdfPathEntry>.Builder under = ImmutableArray.CreateBuilder<SdfPathEntry>();
        SdfPathEntry? file = null;

        foreach (SdfPathEntry entry in paths)
        {
            string normalized = SdfIndex.NormalizePath(entry.Path);

            if (exactAsked && string.Equals(normalized, exact, StringComparison.Ordinal))
            {
                file = entry;
                continue;
            }

            if (normalized.StartsWith(folder, StringComparison.Ordinal))
            {
                under.Add(entry);
            }
        }

        if (file is not null && under.Count > 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{exact} names both a file and a folder holding {under.Count} files, so what to extract is ambiguous. Add a trailing / for the folder."));
        }

        if (file is not null)
        {
            return Result.Ok(ImmutableArray.Create(file.Value));
        }

        if (under.Count == 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The archives hold no file or folder named {wanted}."));
        }

        // Ordinal by path, so the same request writes the same manifest.
        under.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return Result.Ok(under.ToImmutable());
    }

    /// <summary>
    /// The index entries for a known set of exact paths.
    /// </summary>
    /// <remarks>
    /// <see cref="Select"/> answers "what does this request name", which means
    /// scanning. A caller that already holds resolved paths — everything
    /// <see cref="CharacterAssets.Paths"/> returns, say — knows exactly what it
    /// wants, and asking Select once per path turns one scan into as many scans
    /// as there are files. That is 24 million path normalizations for a 49-file
    /// character, and it is the whole cost of resolving one.
    /// </remarks>
    public static Result<ImmutableArray<SdfPathEntry>> Exactly(
        ImmutableArray<SdfPathEntry> paths,
        ImmutableArray<string> wanted)
    {
        Dictionary<string, SdfPathEntry> byPath = new(StringComparer.Ordinal);
        foreach (SdfPathEntry entry in paths)
        {
            byPath[SdfIndex.NormalizePath(entry.Path)] = entry;
        }

        ImmutableArray<SdfPathEntry>.Builder found = ImmutableArray.CreateBuilder<SdfPathEntry>(wanted.Length);
        foreach (string path in wanted)
        {
            if (!byPath.TryGetValue(SdfIndex.NormalizePath(path), out SdfPathEntry entry))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"The archives hold no file named {path}."));
            }

            found.Add(entry);
        }

        found.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// The textures a model's materials bind, for extracting alongside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CharacterAssets.Paths"/> cannot answer this, and the omission
    /// is not an oversight in it: everything there is found by naming
    /// convention, and a texture is not named after the model. It is named
    /// inside the editordata's material bindings, and 595,271 of 595,389
    /// bindings point into one shared tree rather than sitting beside the model
    /// — so no rule about names could reach them, and reading the file is the
    /// only way.
    /// </para>
    /// <para>
    /// The consequence of leaving them out was quiet: an extracted character
    /// exported from its own folder gave geometry and animation and then refused
    /// on the first texture, which looks like a broken export rather than an
    /// incomplete extraction.
    /// </para>
    /// <para>
    /// A binding the archives do not hold is skipped rather than refused over.
    /// A model is not less extractable because one of its 80 textures is
    /// missing, and the export refuses over that same path later with a message
    /// about the texture — which is where the refusal belongs, because that is
    /// where it matters.
    /// </para>
    /// </remarks>
    public static Result<ImmutableArray<string>> BoundTextures(
        ImmutableArray<SdfPathEntry> paths,
        ContentSources content,
        CharacterAssets assets)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(assets);

        if (assets.Editordata is null)
        {
            return Result.Ok(ImmutableArray<string>.Empty);
        }

        Result<byte[]?> read = content.Read(SdfIndex.NormalizePath(assets.Editordata));
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        if (bytes is null)
        {
            return Result.Ok(ImmutableArray<string>.Empty);
        }

        Result<EditordataFile> editordata = EditordataReader.Read(
            SourceFile.FromMemory(assets.Editordata, bytes));
        if (!editordata.TryGetValue(out EditordataFile? parsed, out Refusal? parseRefusal) || parsed is null)
        {
            return parseRefusal ?? Refusal.Malformed("The editordata could not be read.");
        }

        HashSet<string> held = new(StringComparer.Ordinal);
        foreach (SdfPathEntry entry in paths)
        {
            held.Add(SdfIndex.NormalizePath(entry.Path));
        }

        ImmutableArray<string>.Builder found = ImmutableArray.CreateBuilder<string>();
        foreach (TextureReference texture in MaterialTextures.List(parsed, assets.Name))
        {
            string path = SdfIndex.NormalizePath(texture.Path);
            if (held.Contains(path))
            {
                found.Add(path);
            }
        }

        return Result.Ok(found.ToImmutable());
    }

    /// <summary>
    /// The paths containing <paramref name="text"/>, case-insensitively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The container has no search of its own, and the conventions in
    /// <see cref="CharacterResolver"/> only find what they are named for. This is
    /// how a caller finds the rest: the animation tree alone holds 68,561 files
    /// in one flat folder, and listing it to read by eye is not finding.
    /// </para>
    /// <para>
    /// Substring rather than pattern, over the whole path rather than the file
    /// name. One rule that needs no explaining beats a syntax, and a caller who
    /// wants more can filter what comes back.
    /// </para>
    /// <para>
    /// <b>Ordered by what the caller is likely to have meant, not
    /// alphabetically.</b> Alphabetical looks neutral and is not: searching
    /// <c>cartman</c> matches 2,291 paths and puts <c>chr_cartman.mmb</c> —
    /// the one thing anyone typing that wants — at position <b>528</b>, behind
    /// hundreds of <c>.manimsys</c> and <c>.juice</c> files this tool cannot
    /// open at all. So a file type the tool reads comes first, models before
    /// the rest; then a match in the name over one in the folder; then the
    /// shorter path. Ties fall back to the path itself, so the order is still
    /// the same every time.
    /// </para>
    /// </remarks>
    public static Result<ImmutableArray<SdfPathEntry>> Find(
        ImmutableArray<SdfPathEntry> paths,
        string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string wanted = SdfIndex.NormalizePath(text);
        if (wanted.Length == 0)
        {
            return Refusal.Unsupported("--find names the text to look for, and cannot be empty.");
        }

        Result<(ImmutableArray<SdfPathEntry> Best, int Total)> found = new ArchiveSearch(paths).Best(wanted, limit: 0);
        return found.TryGetValue(out (ImmutableArray<SdfPathEntry> Best, int Total) all, out Refusal? refusal)
            ? Result.Ok(all.Best)
            : refusal;
    }



    /// <summary>
    /// Writes the selected files beneath <paramref name="root"/> and publishes
    /// the provenance manifest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The written tree mirrors the archive's own paths unless
    /// <paramref name="flatten"/> says otherwise. Flattening is the escape hatch
    /// for a path that will not fit (see <see cref="MaxPathLength"/>) and for
    /// taking one file out to edit: it drops the folders and keeps the names.
    /// It also gives up what the mirrored layout is for — the result is no
    /// longer a <c>--content-root</c> and no longer the layout the loose-file
    /// mod loader reads — so it is never the default.
    /// </para>
    /// <para>
    /// The manifest is written last: it describes what is on disk, so it must
    /// not appear before the files it describes. It records the virtual path
    /// either way, so a flattened file still knows where it came from.
    /// </para>
    /// </remarks>
    public static Result<ExtractionOutcome> Extract(
        SdfContentSource source,
        ImmutableArray<SdfPathEntry> selected,
        string request,
        string root,
        int limit = DefaultLimit,
        bool flatten = false,
        IProgress<int>? progress = null,
        CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (limit > 0 && selected.Length > limit)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"That names {selected.Length} files, above the {limit} one request writes without being told to. Raise --limit to extract them all."));
        }

        if (flatten)
        {
            // Two files sharing a name lose one of themselves once the folders
            // that told them apart are gone. Checked before anything is written,
            // because the loss is silent and the caller asked for both.
            Result<int> distinct = NamesAreDistinct(selected);
            if (!distinct.TryGetValue(out _, out Refusal? collision))
            {
                return collision;
            }
        }

        ImmutableArray<ExtractedFile>.Builder written = ImmutableArray.CreateBuilder<ExtractedFile>(selected.Length);
        int notPortable = 0;
        int longest = 0;

        bool cancelled = false;

        foreach (SdfPathEntry entry in selected)
        {
            if (cancellation.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (entry.IsDirectory)
            {
                continue;
            }

            Result<string> destination = Destination(root, entry.Path, flatten);
            if (!destination.TryGetValue(out string? output, out Refusal? pathRefusal))
            {
                return pathRefusal;
            }

            Result<SdfContent> content = source.Read(entry.Path);
            if (!content.TryGetValue(out SdfContent bytes, out Refusal? readRefusal))
            {
                return readRefusal;
            }

            if (!bytes.IsPresent)
            {
                // The walk spelled it, so the descent finding nothing means the
                // two readers disagree, which is a fault rather than an absence.
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.Path} is listed in the index but could not be read."));
            }

            Result<int> published = Publish(output, bytes.Bytes.ToArray());
            if (!published.TryGetValue(out _, out Refusal? writeRefusal))
            {
                return writeRefusal;
            }

            // Correct here, and unusable on a default Windows install. Counted
            // rather than refused: this extraction is complete and right, and it
            // is the mod built from it that would travel badly.
            if (output.Length > WindowsPathLength)
            {
                notPortable++;
                longest = System.Math.Max(longest, output.Length);
            }

            written.Add(new ExtractedFile(
                entry.Path,
                Path.GetRelativePath(root, output).Replace(Path.DirectorySeparatorChar, '/'),
                bytes.Bytes.Length,
                Convert.ToHexStringLower(SHA256.HashData(bytes.Bytes.Span)),
                ArchivesOf(source, entry)));

            // After the file is accounted for, so the count reported is of files
            // that exist rather than of files about to.
            progress?.Report(written.Count);
        }

        ImmutableArray<Diagnostic>.Builder diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (cancelled)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.ExtractionCancelled,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Stopped after {written.Count} of {selected.Length} files. What was written is complete and listed in the manifest; the rest was not started.")));
        }

        if (notPortable > 0)
        {
            string files = notPortable == 1 ? "file" : "files";
            diagnostics.Add(new Diagnostic(
                DiagnosticIds.ExtractionPathNotPortable,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{notPortable} extracted {files} sit at paths longer than the {WindowsPathLength} characters Windows accepts by default, the longest being {longest}. " +
                    $"They are correct here, but unpacking this set on Windows would refuse. Extract somewhere shorter, or with the folders flattened, if it is going to travel.")));
        }

        string normalizedRequest = SdfIndex.NormalizePath(request).TrimStart('/');
        ExtractionOutcome outcome = new(
            normalizedRequest, written.ToImmutable(), ManifestName, diagnostics.ToImmutable(), cancelled);

        string manifestPath = Path.Combine(root, ManifestName);
        Result<int> manifest = Publish(manifestPath, Manifest(outcome, Existing(manifestPath)));
        return manifest.TryGetValue(out _, out Refusal? manifestRefusal)
            ? Result.Ok(outcome)
            : manifestRefusal;
    }

    /// <summary>
    /// Reads the entries an earlier extraction into the same folder recorded.
    /// </summary>
    /// <remarks>
    /// The manifest describes the folder, not the run that made it. Extracting
    /// one file at a time into one directory is the ordinary way to work, and
    /// each run overwriting the record would leave every file but the last with
    /// no recorded origin — which is exactly what the manifest exists to
    /// prevent, and which fails silently because the files themselves are
    /// perfectly fine.
    /// </remarks>
    private static ImmutableArray<ExtractedFile> Existing(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        ImmutableArray<ExtractedFile>.Builder found = ImmutableArray.CreateBuilder<ExtractedFile>();

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            if (!document.RootElement.TryGetProperty("extracted", out JsonElement extracted))
            {
                return [];
            }

            foreach (JsonElement entry in extracted.EnumerateArray())
            {
                string? path = entry.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
                string? output = entry.TryGetProperty("output", out JsonElement o) ? o.GetString() : null;
                string? sha = entry.TryGetProperty("sha256", out JsonElement s) ? s.GetString() : null;
                long bytes = entry.TryGetProperty("bytes", out JsonElement b) && b.TryGetInt64(out long value)
                    ? value
                    : 0;

                if (path is null || output is null || sha is null)
                {
                    continue;
                }

                ImmutableArray<int>.Builder archives = ImmutableArray.CreateBuilder<int>();
                if (entry.TryGetProperty("archives", out JsonElement listed))
                {
                    foreach (JsonElement archive in listed.EnumerateArray())
                    {
                        if (archive.TryGetInt32(out int index))
                        {
                            archives.Add(index);
                        }
                    }
                }

                found.Add(new ExtractedFile(path, output, bytes, sha, archives.ToImmutable()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // An unreadable manifest must not cost the extraction that just
            // succeeded. What was written is real; the record starts again.
            return [];
        }

        return found.ToImmutable();
    }

    /// <summary>
    /// The manifest bytes, so that a caller can compare them without writing.
    /// </summary>
    /// <remarks>
    /// No timestamp, no local paths: the same extraction produces the same
    /// manifest byte for byte, and nothing about the machine that ran it leaks
    /// into a file that travels with a mod.
    /// </remarks>
    public static byte[] Manifest(ExtractionOutcome outcome) => Manifest(outcome, []);

    /// <summary>
    /// The manifest bytes, carrying forward what an earlier extraction into the
    /// same folder recorded.
    /// </summary>
    /// <remarks>
    /// Merged by output path, this run winning, then ordered by virtual path so
    /// the same folder always produces the same manifest however it was filled.
    /// </remarks>
    public static byte[] Manifest(ExtractionOutcome outcome, ImmutableArray<ExtractedFile> earlier)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        SortedDictionary<string, ExtractedFile> merged = new(StringComparer.Ordinal);

        foreach (ExtractedFile file in earlier)
        {
            merged[file.Output] = file;
        }

        foreach (ExtractedFile file in outcome.Files)
        {
            merged[file.Output] = file;
        }

        List<ExtractedFile> all = [.. merged.Values];
        all.Sort((left, right) => string.CompareOrdinal(left.VirtualPath, right.VirtualPath));

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("request", outcome.Request);
            writer.WriteNumber("files", all.Count);
            writer.WriteStartArray("extracted");

            foreach (ExtractedFile file in all)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.VirtualPath);
                writer.WriteString("output", file.Output);
                writer.WriteNumber("bytes", file.Bytes);
                writer.WriteString("sha256", file.Sha256);
                writer.WriteStartArray("archives");

                foreach (int archive in file.Archives)
                {
                    writer.WriteNumberValue(archive);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static ImmutableArray<int> ArchivesOf(SdfContentSource source, SdfPathEntry entry)
    {
        Result<SdfToc> toc = source.Toc();
        if (toc.IsRefused)
        {
            return [];
        }

        Result<SdfEntry> decoded = SdfIndex.ReadEntryAt(
            toc.Value.FileTable.Span, entry.NodeOffset, entry.Path, toc.Value.Layout);

        if (decoded.IsRefused)
        {
            return [];
        }

        SortedSet<int> archives = [];
        foreach (SdfChunk chunk in decoded.Value.Chunks)
        {
            archives.Add(chunk.ArchiveId);
        }

        return [.. archives];
    }

    /// <summary>
    /// Checks that flattening will not put two files at the same name.
    /// </summary>
    /// <remarks>
    /// The archives hold the same file name under many folders — every
    /// character has a <c>.mmb</c> — so this is the ordinary case for any broad
    /// selection, not an exotic one. Refusing names both paths, because the
    /// caller has to decide which they meant.
    /// </remarks>
    private static Result<int> NamesAreDistinct(ImmutableArray<SdfPathEntry> selected)
    {
        Dictionary<string, string> byName = new(StringComparer.Ordinal);

        foreach (SdfPathEntry entry in selected)
        {
            if (entry.IsDirectory)
            {
                continue;
            }

            string name = SdfIndex.NormalizePath(entry.Path[(entry.Path.LastIndexOf('/') + 1)..]);
            if (byName.TryGetValue(name, out string? first))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Flattening would write {first} and {entry.Path} to the same name. Extract them separately, or keep the folders."));
            }

            byName[name] = entry.Path;
        }

        return Result.Ok(byName.Count);
    }

    private static Result<int> Publish(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource(
                string.Create(CultureInfo.InvariantCulture, $"The directory {directory} could not be created."),
                DiagnosticIds.ResourceMissing);
        }

        return AtomicFile.Publish(path, bytes);
    }

    /// <summary>
    /// Maps one virtual path to a file beneath the extraction root.
    /// </summary>
    /// <remarks>
    /// The mapping is checked rather than trusted. A path that climbs out of the
    /// root, or that names a segment the host filesystem cannot spell, is
    /// refused: writing outside the directory the caller named would be a
    /// surprise no output is worth, and silently mangling a name would break the
    /// mirroring the loose-file layout depends on.
    /// </remarks>
    /// <remarks>
    /// The segment check and the boundary check below overlap: measured by
    /// removing each, either one alone refuses a <c>..</c> segment, and only
    /// removing both lets it through. They are kept together because they fail
    /// differently — the first rejects a name the host cannot spell, the second
    /// rejects anything that resolves outside the root however it got there —
    /// and because the cost of one being wrong is a file written somewhere the
    /// caller never named.
    /// </remarks>
    private static Result<string> Destination(string root, string virtualPath, bool flatten)
    {
        StringBuilder relative = new();
        string[] segments = flatten ? [virtualPath[(virtualPath.LastIndexOf('/') + 1)..]] : virtualPath.Split('/');

        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{virtualPath} has a path segment that cannot be written to disk safely."));
            }

            if (segment.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{virtualPath} names a segment this filesystem cannot spell."));
            }

            if (relative.Length > 0)
            {
                relative.Append(Path.DirectorySeparatorChar);
            }

            relative.Append(segment);
        }

        string full = Path.GetFullPath(Path.Combine(root, relative.ToString()));
        string bounded = Path.GetFullPath(root);

        // Windows refuses a full path over 260 characters unless long paths are
        // switched on, and the failure it raises names no cause a person can
        // act on. The archive's own paths reach 196 characters, so an output
        // directory of any real depth can cross it. Said plainly here, with the
        // budget left, because the fix is entirely the caller's: extract
        // somewhere shorter.
        if (full.Length > MaxPathLength)
        {
            // The directory's length, not the directory: a path that broke the
            // limit is by definition too long to read back in a sentence.
            int room = MaxPathLength - (relative.Length + 1);
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"Extracting {virtualPath} here would need a {full.Length}-character path, and this system allows {MaxPathLength}. " +
                $"That archive path is {relative.Length} characters on its own, so the output directory must be at most {room} " +
                $"characters; this one is {bounded.Length}. Extract somewhere shorter."));
        }

        if (!full.StartsWith(
                bounded.EndsWith(Path.DirectorySeparatorChar) ? bounded : bounded + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{virtualPath} would be written outside {root}."));
        }

        return Result.Ok(full);
    }
}
