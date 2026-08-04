using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Cli;

/// <summary>
/// Takes files out of the archives and onto disk.
/// </summary>
/// <remarks>
/// A second verb rather than an option on the first, because it answers a
/// different question: export reads the archives to produce a GLB, and this
/// produces the game's own files. The work itself lives in
/// <see cref="ArchiveExtraction"/> so that the graphical front end runs the same
/// extraction rather than a second implementation of it.
/// </remarks>
internal static class ExtractCommand
{
    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? sdfRoot = null;
        string? path = null;
        string? character = null;
        string? find = null;
        string? destination = null;
        bool list = false;
        bool flatten = false;
        bool json = false;
        int limit = ArchiveExtraction.DefaultLimit;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--sdf-root":
                    if (!TryTake(arguments, ref i, out sdfRoot))
                    {
                        return Program.Fail(Missing("--sdf-root"), json, output, error, "Extraction");
                    }

                    break;

                case "--path":
                    if (!TryTake(arguments, ref i, out path))
                    {
                        return Program.Fail(Missing("--path"), json, output, error, "Extraction");
                    }

                    break;

                case "--character":
                    if (!TryTake(arguments, ref i, out character))
                    {
                        return Program.Fail(Missing("--character"), json, output, error, "Extraction");
                    }

                    break;

                case "--find":
                    if (!TryTake(arguments, ref i, out find))
                    {
                        return Program.Fail(Missing("--find"), json, output, error, "Extraction");
                    }

                    break;

                case "--out":
                    if (!TryTake(arguments, ref i, out destination))
                    {
                        return Program.Fail(Missing("--out"), json, output, error, "Extraction");
                    }

                    break;

                case "--limit":
                    if (!TryTake(arguments, ref i, out string? limitText) ||
                        !int.TryParse(limitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out limit) ||
                        limit < 0)
                    {
                        return Program.Fail(
                            Refusal.Unsupported("--limit is the number of files one request may write, or 0 for no limit."),
                            json,
                            output,
                            error,
                            "Extraction");
                    }

                    break;

                case "--list":
                    list = true;
                    break;

                case "--flat":
                    flatten = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture, $"{arguments[i]} is not an option this build accepts.")),
                        json,
                        output,
                        error,
                        "Extraction");
            }
        }

        if (sdfRoot is null || (path is null && character is null && find is null))
        {
            return Program.Fail(
                Refusal.Unsupported("--sdf-root is required, with --path for one file or folder, --character for a model's whole asset set, or --find to search."),
                json,
                output,
                error,
                "Extraction");
        }

        int chosen = (path is null ? 0 : 1) + (character is null ? 0 : 1) + (find is null ? 0 : 1);
        if (chosen > 1)
        {
            return Program.Fail(
                Refusal.Unsupported("--path, --character and --find each choose what to act on in a different way, so only one may be given."),
                json,
                output,
                error,
                "Extraction");
        }

        if (destination is null && !list && find is null)
        {
            return Program.Fail(
                Refusal.Unsupported("--out names the directory to extract into; --list shows what would be written without writing it."),
                json,
                output,
                error,
                "Extraction");
        }

        using SdfContentSource source = new(sdfRoot);

        Result<ImmutableArray<SdfPathEntry>> walked = source.Paths();
        if (!walked.TryGetValue(out ImmutableArray<SdfPathEntry> paths, out Refusal? walkRefusal))
        {
            return Program.Fail(walkRefusal, json, output, error, "Extraction");
        }

        if (find is not null)
        {
            // Searching never writes. It answers where something is, and the
            // caller then names it with --path — which keeps every write the
            // result of a path someone chose rather than of a substring.
            Result<ImmutableArray<SdfPathEntry>> matched = ArchiveExtraction.Find(paths, find);
            if (!matched.TryGetValue(out ImmutableArray<SdfPathEntry> hits, out Refusal? findRefusal))
            {
                return Program.Fail(findRefusal, json, output, error, "Extraction");
            }

            foreach (SdfPathEntry entry in hits)
            {
                output.WriteLine(entry.Path);
            }

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"{hits.Length} of {paths.Length} paths match '{find}'."));
            return Program.Success;
        }

        ImmutableArray<SdfPathEntry> wanted;
        CharacterAssets? assets = null;

        if (character is not null)
        {
            Result<CharacterAssets> resolved = CharacterResolver.Resolve(paths, character);
            if (!resolved.TryGetValue(out assets, out Refusal? resolveRefusal))
            {
                return Program.Fail(resolveRefusal, json, output, error, "Extraction");
            }

            // What it resolved and why, before anything is written. A wrong
            // guess that silently grabs the wrong clip is worse than asking, so
            // the rules are shown rather than merely applied.
            Describe(assets, error);

            Result<ImmutableArray<SdfPathEntry>> set = ArchiveExtraction.Exactly(paths, assets.Paths());
            if (!set.TryGetValue(out wanted, out Refusal? setRefusal))
            {
                return Program.Fail(setRefusal, json, output, error, "Extraction");
            }
        }
        else
        {
            Result<ImmutableArray<SdfPathEntry>> selected = ArchiveExtraction.Select(paths, path!);
            if (!selected.TryGetValue(out wanted, out Refusal? selectRefusal))
            {
                return Program.Fail(selectRefusal, json, output, error, "Extraction");
            }
        }

        if (list)
        {
            // Deliberately not the manifest shape: nothing has been extracted,
            // so this says what a run would take, not where anything came from.
            foreach (SdfPathEntry entry in wanted)
            {
                output.WriteLine(entry.Path);
            }

            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"{wanted.Length} files."));
            return Program.Success;
        }

        Result<ExtractionOutcome> extracted = ArchiveExtraction.Extract(
            source, wanted, path ?? character!, destination!, limit, flatten);
        if (!extracted.TryGetValue(out ExtractionOutcome? outcome, out Refusal? extractRefusal))
        {
            return Program.Fail(extractRefusal, json, output, error, "Extraction");
        }

        if (json)
        {
            output.WriteLine(Json(outcome, destination!));
            return Program.Success;
        }

        long bytes = 0;
        foreach (ExtractedFile file in outcome.Files)
        {
            bytes += file.Bytes;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Extracted {outcome.Files.Length} files, {bytes} bytes, into {destination}."));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Provenance recorded in {outcome.Manifest}."));

        // Warnings to standard error, so standard output stays the result.
        foreach (Diagnostic diagnostic in outcome.Diagnostics)
        {
            error.WriteLine(diagnostic.Message);
        }

        return Program.Success;
    }

    /// <summary>
    /// Says what the conventions accounted for, and what they did not.
    /// </summary>
    /// <remarks>
    /// To standard error, because it is commentary on the result rather than the
    /// result: standard output stays the list of files or the JSON.
    /// </remarks>
    private static void Describe(CharacterAssets assets, TextWriter error)
    {
        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"Resolved '{assets.Name}' from {assets.Model}:"));

        Line(error, "cameldata", assets.Cameldata);
        Line(error, "editordata", assets.Editordata);
        Line(error, "setup", assets.Setup);
        Line(error, "mouth", assets.Mouth);
        Line(error, "eyes", assets.Eyes);
        Line(error, "pupils", assets.Pupils);
        Line(error, "eyebrows", assets.Eyebrows);
        Line(error, "lipsync", assets.LipsyncDatabase);

        if (assets.Clips.Length > 0)
        {
            string how = assets.Clips[0].Match == AssetMatch.VariantBase ? " (via the rig family)" : string.Empty;
            error.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  clips      {assets.Clips.Length}{how}"));
        }

        foreach (string note in assets.Unresolved)
        {
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  - {note}"));
        }
    }

    private static void Line(TextWriter error, string label, ResolvedAsset? asset)
    {
        if (asset is not null)
        {
            string how = asset.Match == AssetMatch.VariantBase ? "  (via the rig family)" : string.Empty;
            error.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"  {label,-10} {asset.VirtualPath}{how}"));
        }
    }

    private static void Line(TextWriter error, string label, string? path)
    {
        if (path is not null)
        {
            error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {label,-10} {path}"));
        }
    }

    private static string Json(ExtractionOutcome outcome, string root)
    {
        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("schema_version", "perianth-extract-result.v1");
        writer.WriteString("status", "extracted");
        writer.WriteString("request", outcome.Request);
        writer.WriteString("output", root);
        writer.WriteString("manifest", outcome.Manifest);
        writer.WriteNumber("files", outcome.Files.Length);
        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static Refusal Missing(string option) => Refusal.Unsupported(string.Create(
        CultureInfo.InvariantCulture, $"{option} needs a value."));

    private static bool TryTake(string[] arguments, ref int index, out string? value)
    {
        if (index + 1 >= arguments.Length)
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return true;
    }
}
