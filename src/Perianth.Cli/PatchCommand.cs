using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;

namespace Perianth.Cli;

/// <summary>
/// Makes and applies byte-level patches.
/// </summary>
/// <remarks>
/// The verb exists so a modification can be shared without the game's own bytes
/// being shared with it. Making one needs the original and the edit; applying
/// one needs the recipient's own copy of the original, which is the whole
/// point, and produces the same mod folder the texture verb writes.
/// </remarks>
internal static class PatchCommand
{
    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        bool make = false;
        bool addition = false;
        bool apply = false;
        bool describe = false;
        bool json = false;
        List<string> edited = [];
        List<string> originals = [];
        List<string> patches = [];
        string? destination = null;
        string? name = null;
        string author = "unknown";
        string version = "1.0.0";
        string? description = null;
        string? replaces = null;
        bool preload = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--new":
                    addition = true;
                    break;

                case "--make":
                    make = true;
                    break;

                case "--apply":
                    apply = true;
                    break;

                case "--describe":
                    describe = true;
                    break;

                case "--edited":
                    if (!Take(arguments, ref i, out string? one))
                    {
                        return Program.Fail(Missing("--edited"), json, output, error, "Patch");
                    }

                    edited.Add(one);
                    break;

                case "--original":
                    if (!Take(arguments, ref i, out string? was))
                    {
                        return Program.Fail(Missing("--original"), json, output, error, "Patch");
                    }

                    originals.Add(was);
                    break;

                case "--patch":
                    if (!Take(arguments, ref i, out string? file))
                    {
                        return Program.Fail(Missing("--patch"), json, output, error, "Patch");
                    }

                    patches.Add(file);
                    break;

                case "--replaces":
                    if (!Take(arguments, ref i, out replaces))
                    {
                        return Program.Fail(Missing("--replaces"), json, output, error, "Patch");
                    }

                    break;

                case "--out":
                    if (!Take(arguments, ref i, out destination))
                    {
                        return Program.Fail(Missing("--out"), json, output, error, "Patch");
                    }

                    break;

                case "--name":
                    if (!Take(arguments, ref i, out name))
                    {
                        return Program.Fail(Missing("--name"), json, output, error, "Patch");
                    }

                    break;

                case "--author":
                    if (!Take(arguments, ref i, out string? who))
                    {
                        return Program.Fail(Missing("--author"), json, output, error, "Patch");
                    }

                    author = who;
                    break;

                case "--version":
                    if (!Take(arguments, ref i, out string? said))
                    {
                        return Program.Fail(Missing("--version"), json, output, error, "Patch");
                    }

                    version = said;
                    break;

                case "--description":
                    if (!Take(arguments, ref i, out description))
                    {
                        return Program.Fail(Missing("--description"), json, output, error, "Patch");
                    }

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
                        "Patch");
            }
        }

        int modes = (make ? 1 : 0) + (apply ? 1 : 0) + (describe ? 1 : 0);
        if (modes != 1)
        {
            return Program.Fail(
                Refusal.Unsupported("Say exactly one of --make, --apply or --describe."),
                json,
                output,
                error,
                "Patch");
        }

        if (describe)
        {
            return Describe(patches, output, error, json);
        }

        return make
            ? Make(edited, originals, replaces, destination, addition, output, error, json)
            : Apply(patches, originals, destination, name, author, version, description, preload, output, error, json);
    }

    /// <summary>
    /// Writes one patch per edited file.
    /// </summary>
    /// <remarks>
    /// The archive path comes from the original's recorded provenance, as the
    /// texture verb does it, so a patch knows where its result belongs without
    /// the person making it having to say.
    /// </remarks>
    private static int Make(
        List<string> edited,
        List<string> originals,
        string? replaces,
        string? destination,
        bool addition,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        // --new says the file is the author's own and the game never had it, so
        // there is no original to diff against and none to demand. Its patch
        // carries the whole file, which is the honest price: those bytes are
        // theirs to give away, and only the game's are not.
        if (edited.Count == 0 || (!addition && originals.Count != edited.Count))
        {
            return Program.Fail(
                Refusal.Unsupported("Each --edited needs one --original, in the same order, or --new if the game never had it."),
                json,
                output,
                error,
                "Patch");
        }

        if (addition && originals.Count > 0)
        {
            return Program.Fail(
                Refusal.Unsupported("--new means there is no original, so --original does not belong with it."),
                json,
                output,
                error,
                "Patch");
        }

        if (addition && replaces is null)
        {
            // Provenance is what names the archive path, and a file the game
            // never had has none. Guessing one would write a mod the game never
            // reads while looking like it worked.
            return Program.Fail(
                Refusal.Unsupported("--new needs --replaces to say where in the game the file goes."),
                json,
                output,
                error,
                "Patch");
        }

        if (destination is null)
        {
            return Program.Fail(
                Refusal.Unsupported("--out names the directory to write the patches into."),
                json,
                output,
                error,
                "Patch");
        }

        if (replaces is not null && edited.Count > 1)
        {
            return Program.Fail(
                Refusal.Unsupported("--replaces names one archive path, so it suits one --edited only."),
                json,
                output,
                error,
                "Patch");
        }

        List<string> written = [];

        for (int i = 0; i < edited.Count; i++)
        {
            byte[] original = [];

            if (!addition)
            {
                Result<byte[]> before = Read(originals[i]);
                if (!before.TryGetValue(out original!, out Refusal? refusal))
                {
                    return Program.Fail(refusal, json, output, error, "Patch");
                }
            }

            Result<byte[]> after = Read(edited[i]);
            if (!after.TryGetValue(out byte[]? changed, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Patch");
            }

            string virtualPath;
            if (replaces is not null || addition)
            {
                virtualPath = replaces!;
            }
            else
            {
                Result<FileProvenance> provenance = ExtractionProvenance.Of(originals[i]);
                if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? unknown))
                {
                    return Program.Fail(unknown, json, output, error, "Patch");
                }

                virtualPath = where.VirtualPath;

                if (!where.Unmodified)
                {
                    error.WriteLine(
                        $"'{originals[i]}' has changed since it was extracted, so this patch is against "
                        + "what is there now rather than against the game's own file.");
                }
            }

            Result<byte[]> patch = addition
                ? BytePatch.MakeAddition(changed, virtualPath)
                : BytePatch.Make(original, changed, virtualPath);
            if (!patch.TryGetValue(out byte[]? bytes, out Refusal? refused))
            {
                return Program.Fail(refused, json, output, error, "Patch");
            }

            string file = Path.Combine(
                destination, Path.GetFileNameWithoutExtension(edited[i]) + ".perianthpatch");

            try
            {
                Directory.CreateDirectory(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Program.Fail(
                    Refusal.Resource($"'{destination}' could not be created.", DiagnosticIds.ResourceMissing),
                    json,
                    output,
                    error,
                    "Patch");
            }

            Result<int> published = AtomicFile.Publish(file, bytes);
            if (published.IsRefused)
            {
                return Program.Fail(published.Refusal, json, output, error, "Patch");
            }

            written.Add(file);
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{file} — {bytes.Length} bytes for a {changed.Length} byte file ({100.0 * bytes.Length / changed.Length:F1}%)"));
        }

        output.WriteLine(addition
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {written.Count} patches. They carry your own files whole, which is yours to give away; applying one needs no original.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {written.Count} patches. They carry only what changed, so they can be shared; applying one needs the recipient's own copy of the original."));

        return Program.Success;
    }

    /// <summary>
    /// Applies patches against the recipient's own originals, into one mod.
    /// </summary>
    private static int Apply(
        List<string> patches,
        List<string> originals,
        string? destination,
        string? name,
        string author,
        string version,
        string? description,
        bool preload,
        TextWriter output,
        TextWriter error,
        bool json)
    {
        if (patches.Count == 0)
        {
            return Program.Fail(
                Refusal.Unsupported("Name at least one patch with --patch."),
                json,
                output,
                error,
                "Patch");
        }

        if (destination is null || name is null)
        {
            return Program.Fail(
                Refusal.Unsupported("Applying patches writes a mod, which needs --out and --name."),
                json,
                output,
                error,
                "Patch");
        }

        ImmutableArray<ModFile>.Builder files = ImmutableArray.CreateBuilder<ModFile>(patches.Count);

        // Read and describe every patch before asking for any original, so a
        // mixed set can be counted. A patch carrying a file the game never had
        // needs no original and must not consume one, or every original after
        // it in the list pairs with the wrong patch.
        List<(byte[] Patch, PatchHeader Header)> plan = new(patches.Count);

        foreach (string path in patches)
        {
            Result<byte[]> read = Read(path);
            if (!read.TryGetValue(out byte[]? patch, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, "Patch");
            }

            Result<PatchHeader> described = BytePatch.Describe(patch);
            if (!described.TryGetValue(out PatchHeader? header, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Patch");
            }

            plan.Add((patch, header));
        }

        int wanted = plan.Count(entry => !entry.Header.IsNewFile);

        if (originals.Count != wanted)
        {
            return Program.Fail(
                Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{wanted} of these {plan.Count} patches change a file the game ships and need one --original each, in the same order; {originals.Count} were given. The rest add files of their own and need none.")),
                json,
                output,
                error,
                "Patch");
        }

        int taken = 0;

        foreach ((byte[] patch, PatchHeader header) in plan)
        {
            byte[] original = [];

            if (!header.IsNewFile)
            {
                Result<byte[]> source = Read(originals[taken++]);
                if (!source.TryGetValue(out original!, out Refusal? missing))
                {
                    return Program.Fail(missing, json, output, error, "Patch");
                }
            }

            Result<byte[]> applied = BytePatch.Apply(patch, original);
            if (!applied.TryGetValue(out byte[]? result, out Refusal? refused))
            {
                return Program.Fail(refused, json, output, error, "Patch");
            }

            files.Add(new ModFile(header.VirtualPath, result));
        }

        Result<ModOutcome> written = TextureMod.Write(
            destination,
            new ModDetails(name, author, version, description ?? name, preload),
            files.ToImmutable());

        if (!written.TryGetValue(out ModOutcome? outcome, out Refusal? failed))
        {
            return Program.Fail(failed, json, output, error, "Patch");
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Applied {outcome.Files.Length} patches into {outcome.Folder}."));

        foreach (string path in outcome.Files)
        {
            output.WriteLine("  " + path);
        }

        output.WriteLine("Copy that folder into FractureLoader/Mods/ to use it.");
        return Program.Success;
    }

    /// <summary>Says what a patch is for, so its recipient can find that file.</summary>
    private static int Describe(List<string> patches, TextWriter output, TextWriter error, bool json)
    {
        if (patches.Count == 0)
        {
            return Program.Fail(
                Refusal.Unsupported("--describe needs at least one --patch."),
                json,
                output,
                error,
                "Patch");
        }

        foreach (string file in patches)
        {
            Result<byte[]> read = Read(file);
            if (!read.TryGetValue(out byte[]? patch, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, "Patch");
            }

            Result<PatchHeader> described = BytePatch.Describe(patch);
            if (!described.TryGetValue(out PatchHeader? header, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Patch");
            }

            output.WriteLine(file);

            // Which kind it is, first: it decides whether the recipient needs
            // to find a file of their own before this can be applied.
            output.WriteLine(header.IsNewFile
                ? "  adds      " + header.VirtualPath
                : "  replaces  " + header.VirtualPath);

            if (header.IsNewFile)
            {
                output.WriteLine("            a file the game does not ship, carried whole; no original needed");
            }
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  original  {header.OriginalLength} bytes, sha256 {header.OriginalSha256}"));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  result    {header.ResultLength} bytes, sha256 {header.ResultSha256}"));
        }

        return Program.Success;
    }

    private static Result<byte[]> Read(string path)
    {
        try
        {
            return Result.Ok(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource($"'{path}' could not be read.", DiagnosticIds.ResourceMissing);
        }
    }

    private static Refusal Missing(string option) =>
        Refusal.Unsupported(string.Create(CultureInfo.InvariantCulture, $"{option} needs a value."));

    private static bool Take(
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
