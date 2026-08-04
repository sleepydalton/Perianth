using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;

namespace Perianth.Cli;

/// <summary>
/// Turns edited images into a mod the loader reads.
/// </summary>
/// <remarks>
/// A third verb because it answers a third question: export produces a GLB,
/// extract produces the game's own files, and this produces a mod. The work
/// lives in <see cref="TextureMod"/> so the window runs the same conversion.
/// </remarks>
internal static class TextureCommand
{
    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        List<string> from = [];
        List<string> originals = [];
        List<string> replaces = [];
        string? destination = null;
        string? name = null;
        string author = "unknown";
        string version = "1.0.0";
        string? description = null;
        bool mips = true;
        bool preload = false;
        bool json = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--from":
                    if (!TryTake(arguments, ref i, out string? image))
                    {
                        return Program.Fail(Missing("--from"), json, output, error, "Authoring");
                    }

                    from.Add(image);
                    break;

                case "--original":
                    if (!TryTake(arguments, ref i, out string? original))
                    {
                        return Program.Fail(Missing("--original"), json, output, error, "Authoring");
                    }

                    originals.Add(original);
                    break;

                case "--replaces":
                    if (!TryTake(arguments, ref i, out string? replaced))
                    {
                        return Program.Fail(Missing("--replaces"), json, output, error, "Authoring");
                    }

                    replaces.Add(replaced);
                    break;

                case "--out":
                    if (!TryTake(arguments, ref i, out destination))
                    {
                        return Program.Fail(Missing("--out"), json, output, error, "Authoring");
                    }

                    break;

                case "--name":
                    if (!TryTake(arguments, ref i, out name))
                    {
                        return Program.Fail(Missing("--name"), json, output, error, "Authoring");
                    }

                    break;

                case "--author":
                    if (!TryTake(arguments, ref i, out string? who))
                    {
                        return Program.Fail(Missing("--author"), json, output, error, "Authoring");
                    }

                    author = who;
                    break;

                case "--version":
                    if (!TryTake(arguments, ref i, out string? said))
                    {
                        return Program.Fail(Missing("--version"), json, output, error, "Authoring");
                    }

                    version = said;
                    break;

                case "--description":
                    if (!TryTake(arguments, ref i, out description))
                    {
                        return Program.Fail(Missing("--description"), json, output, error, "Authoring");
                    }

                    break;

                case "--no-mips":
                    mips = false;
                    break;

                case "--preload-custom-assets":
                    preload = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{arguments[i]} is not an option this build accepts.")),
                        json,
                        output,
                        error,
                        "Authoring");
            }
        }

        Result<ImmutableArray<ModFile>> built = Build(from, originals, replaces, mips, out var notes);
        if (!built.TryGetValue(out ImmutableArray<ModFile> files, out Refusal? refusal))
        {
            return Program.Fail(refusal, json, output, error, "Authoring");
        }

        if (destination is null || name is null)
        {
            return Program.Fail(
                Refusal.Unsupported("A mod needs --out and --name."), json, output, error, "Authoring");
        }

        Result<ModOutcome> written = TextureMod.Write(
            destination,
            new ModDetails(name, author, version, description ?? name, preload),
            files);

        if (!written.TryGetValue(out ModOutcome? outcome, out Refusal? writeRefusal))
        {
            return Program.Fail(writeRefusal, json, output, error, "Authoring");
        }

        if (json)
        {
            output.WriteLine(Json(outcome, notes));
            return Program.Success;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {outcome.Files.Length} replacements into {outcome.Folder}."));

        foreach (string path in outcome.Files)
        {
            output.WriteLine("  " + path);
        }

        output.WriteLine("Copy that folder into FractureLoader/Mods/ to use it.");

        // Said here because this is where somebody has just made something they
        // might want to give away, and the mod folder holds the game's own
        // bytes. A patch does not, whatever it weighs.
        output.WriteLine(
            "To share it, use 'perianth patch --make': a patch carries only your changes, so nobody "
            + "receives the game's own files.");

        foreach (Diagnostic note in notes)
        {
            error.WriteLine(note.Message);
        }

        return Program.Success;
    }

    /// <summary>
    /// Pairs each image with the archive path it stands in for.
    /// </summary>
    /// <remarks>
    /// <c>--original</c> names a file that was extracted, and its archive path
    /// comes from the recorded provenance rather than from where it sits;
    /// <c>--replaces</c> names the archive path outright, for a file this tool
    /// did not extract. One of the two is required per image, and giving both
    /// for the same image is refused rather than silently preferring one.
    /// </remarks>
    private static Result<ImmutableArray<ModFile>> Build(
        List<string> from,
        List<string> originals,
        List<string> replaces,
        bool mips,
        out ImmutableArray<Diagnostic> notes)
    {
        notes = [];

        if (from.Count == 0)
        {
            return Refusal.Unsupported("Name at least one edited image with --from.");
        }

        if (originals.Count > 0 && replaces.Count > 0)
        {
            return Refusal.Unsupported(
                "Use --original or --replaces, not both: they are two ways of saying the same thing, "
                + "and mixing them makes which image goes where ambiguous.");
        }

        int given = originals.Count + replaces.Count;
        if (given != from.Count)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"{from.Count} images were given but {given} originals: each --from needs one --original or --replaces, in the same order."));
        }

        ImmutableArray<ModFile>.Builder files = ImmutableArray.CreateBuilder<ModFile>(from.Count);
        ImmutableArray<Diagnostic>.Builder found = ImmutableArray.CreateBuilder<Diagnostic>();

        for (int i = 0; i < from.Count; i++)
        {
            byte[] image;
            try
            {
                image = File.ReadAllBytes(from[i]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Refusal.Resource($"'{from[i]}' could not be read.", DiagnosticIds.ResourceMissing);
            }

            Result<byte[]> converted = TextureMod.Import(image, mips);
            if (!converted.TryGetValue(out byte[]? dds, out Refusal? refusal))
            {
                return refusal;
            }

            string virtualPath;

            if (replaces.Count > 0)
            {
                virtualPath = replaces[i];
            }
            else
            {
                Result<FileProvenance> provenance = ExtractionProvenance.Of(originals[i]);
                if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? bad))
                {
                    return bad;
                }

                virtualPath = where.VirtualPath;

                if (!where.Unmodified)
                {
                    found.Add(new Diagnostic(
                        DiagnosticIds.InputChangedDuringRead,
                        DiagnosticSeverity.Warning,
                        $"'{originals[i]}' is not the file that was extracted — its contents have changed since. "
                        + "The comparison below is against what is there now."));
                }

                // Read for comparison only. The original is never written.
                try
                {
                    found.AddRange(TextureMod.Compare(dds, File.ReadAllBytes(originals[i])));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Comparing is a courtesy; failing to read the original for
                    // it must not lose the conversion that already succeeded.
                }
            }

            files.Add(new ModFile(virtualPath, dds));
        }

        notes = found.ToImmutable();
        return Result.Ok(files.ToImmutable());
    }

    private static string Json(ModOutcome outcome, ImmutableArray<Diagnostic> notes)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("result", "ok");
            writer.WriteString("folder", outcome.Folder);
            writer.WriteStartArray("files");

            foreach (string path in outcome.Files)
            {
                writer.WriteStringValue(path);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("warnings");

            foreach (Diagnostic note in notes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", note.Id);
                writer.WriteString("message", note.Message);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Refusal Missing(string option) =>
        Refusal.Unsupported(string.Create(CultureInfo.InvariantCulture, $"{option} needs a value."));

    private static bool TryTake(
        string[] arguments, ref int i, [NotNullWhen(true)] out string? value)
    {
        if (i + 1 < arguments.Length)
        {
            value = arguments[++i];
            return true;
        }

        value = null;
        return false;
    }
}
