using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using Perianth.Core.Content;
using Perianth.Core.Imaging;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Editordata;
using Perianth.Formats.Io;

namespace Perianth.Cli;

/// <summary>
/// Changes what a model's parts are painted with, and writes the result as a mod.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>texture</c>: that verb edits an image, this one edits
/// which image a part uses and what colour it is drawn with. Roadmap §6.11
/// records why those are two operations rather than one — roughly half a
/// model's parts carry their colour in the texture and half in the tint, so
/// each operation is inert on the other's half.
/// </para>
/// <para>
/// <c>--dry-run</c> exists because the two are very different in reach. One
/// texture is typically bound by dozens or hundreds of parts, so an edit that
/// says "changed 210 sections" when you expected one is the difference between
/// the mod you meant and a model repainted throughout.
/// </para>
/// </remarks>
internal static class MaterialCommand
{
    private const string Verb = "Material editing";

    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? source = null;
        string? replaces = null;
        string? destination = null;
        string? name = null;
        string author = "unknown";
        string version = "1.0.0";
        string? description = null;
        List<(string From, string To)> repoints = [];
        List<(string Texture, Rgb Tint)> retints = [];
        List<int> sections = [];
        Rgb? onlyTint = null;
        string? assign = null;
        string channel = "DiffuseColor";
        string? sdfRoot = null;
        string? verify = null;
        bool preload = false;
        bool dryRun = false;
        bool json = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--editordata":
                    if (!TryTake(arguments, ref i, out source))
                    {
                        return Program.Fail(Missing("--editordata"), json, output, error, Verb);
                    }

                    break;

                case "--repoint":
                    if (!TryTake(arguments, ref i, out string? repoint))
                    {
                        return Program.Fail(Missing("--repoint"), json, output, error, Verb);
                    }

                    if (!Split(repoint, out string? from, out string? to))
                    {
                        return Program.Fail(
                            Refusal.Unsupported($"--repoint wants OLD=NEW, and '{repoint}' has no '='."),
                            json, output, error, Verb);
                    }

                    repoints.Add((from, to));
                    break;

                case "--retint":
                    if (!TryTake(arguments, ref i, out string? retint))
                    {
                        return Program.Fail(Missing("--retint"), json, output, error, Verb);
                    }

                    if (!Split(retint, out string? texture, out string? colour))
                    {
                        return Program.Fail(
                            Refusal.Unsupported($"--retint wants TEXTURE=R,G,B, and '{retint}' has no '='."),
                            json, output, error, Verb);
                    }

                    if (!TryColour(colour, out Rgb tint, out Refusal? badColour))
                    {
                        return Program.Fail(badColour, json, output, error, Verb);
                    }

                    retints.Add((texture, tint));
                    break;

                case "--assign":
                    if (!TryTake(arguments, ref i, out assign))
                    {
                        return Program.Fail(Missing("--assign"), json, output, error, Verb);
                    }

                    break;

                case "--channel":
                    if (!TryTake(arguments, ref i, out string? named))
                    {
                        return Program.Fail(Missing("--channel"), json, output, error, Verb);
                    }

                    channel = named;
                    break;

                case "--only-tint":
                    if (!TryTake(arguments, ref i, out string? only))
                    {
                        return Program.Fail(Missing("--only-tint"), json, output, error, Verb);
                    }

                    if (!TryColour(only, out Rgb filter, out Refusal? badFilter))
                    {
                        return Program.Fail(badFilter, json, output, error, Verb);
                    }

                    onlyTint = filter;
                    break;

                case "--section":
                    if (!TryTake(arguments, ref i, out string? ordinal))
                    {
                        return Program.Fail(Missing("--section"), json, output, error, Verb);
                    }

                    if (!int.TryParse(ordinal, NumberStyles.None, CultureInfo.InvariantCulture, out int index))
                    {
                        return Program.Fail(
                            Refusal.Unsupported($"--section wants a whole number, and '{ordinal}' is not one."),
                            json, output, error, Verb);
                    }

                    sections.Add(index);
                    break;

                case "--replaces":
                    if (!TryTake(arguments, ref i, out replaces))
                    {
                        return Program.Fail(Missing("--replaces"), json, output, error, Verb);
                    }

                    break;

                case "--out":
                    if (!TryTake(arguments, ref i, out destination))
                    {
                        return Program.Fail(Missing("--out"), json, output, error, Verb);
                    }

                    break;

                case "--name":
                    if (!TryTake(arguments, ref i, out name))
                    {
                        return Program.Fail(Missing("--name"), json, output, error, Verb);
                    }

                    break;

                case "--author":
                    if (!TryTake(arguments, ref i, out string? who))
                    {
                        return Program.Fail(Missing("--author"), json, output, error, Verb);
                    }

                    author = who;
                    break;

                case "--version":
                    if (!TryTake(arguments, ref i, out string? said))
                    {
                        return Program.Fail(Missing("--version"), json, output, error, Verb);
                    }

                    version = said;
                    break;

                case "--description":
                    if (!TryTake(arguments, ref i, out description))
                    {
                        return Program.Fail(Missing("--description"), json, output, error, Verb);
                    }

                    break;

                case "--preload-custom-assets":
                    preload = true;
                    break;

                case "--sdf-root":
                    if (!TryTake(arguments, ref i, out sdfRoot))
                    {
                        return Program.Fail(Missing("--sdf-root"), json, output, error, Verb);
                    }

                    break;

                case "--verify":
                    if (!TryTake(arguments, ref i, out verify))
                    {
                        return Program.Fail(Missing("--verify"), json, output, error, Verb);
                    }

                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{arguments[i]} is not an option this build accepts.")),
                        json, output, error, Verb);
            }
        }

        if (verify is not null)
        {
            return Verify(verify, sdfRoot, json, output, error);
        }

        if (source is null)
        {
            return Program.Fail(
                Refusal.Unsupported("Name the file to edit with --editordata."), json, output, error, Verb);
        }

        if (repoints.Count == 0 && retints.Count == 0 && assign is null)
        {
            return Program.Fail(
                Refusal.Unsupported("Give at least one --repoint, --retint or --assign, or there is nothing to change."),
                json, output, error, Verb);
        }

        if (assign is not null && sections.Count == 0)
        {
            // Without named parts this would bind one texture across a whole
            // model, which is a thing to do by accident and never on purpose.
            return Program.Fail(
                Refusal.Unsupported("--assign paints named parts, so it needs at least one --section."),
                json, output, error, Verb);
        }

        if (onlyTint is not null && retints.Count == 0)
        {
            return Program.Fail(
                Refusal.Unsupported("--only-tint narrows a --retint, and none was given."),
                json, output, error, Verb);
        }

        Result<SourceFile> read = SourceFileReader.Read(source);
        if (!read.TryGetValue(out SourceFile? bytes, out Refusal? unreadable))
        {
            return Program.Fail(unreadable, json, output, error, Verb);
        }

        Result<EditordataFile> parsed = EditordataReader.Read(bytes);
        if (!parsed.TryGetValue(out EditordataFile? file, out Refusal? malformed))
        {
            return Program.Fail(malformed, json, output, error, Verb);
        }

        List<string> changes = [];

        foreach ((string oldPath, string newPath) in repoints)
        {
            Result<MaterialEditOutcome> done = MaterialEdit.Repoint(
                file, oldPath, newPath, sections.Count == 0 ? null : sections);

            if (!done.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, Verb);
            }

            file = outcome.File;
            changes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"repointed {oldPath} to {newPath} in {Count(outcome.Sections, "section")} ({Count(outcome.Bindings, "binding")})"));
        }

        if (assign is not null)
        {
            Result<MaterialEditOutcome> done = MaterialEdit.Bind(file, sections, channel, assign);

            if (!done.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, Verb);
            }

            file = outcome.File;
            changes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"painted {Count(outcome.Sections, "part")} with {assign} on {channel}"));
        }

        foreach ((string texture, Rgb tint) in retints)
        {
            Result<MaterialEditOutcome> done = MaterialEdit.Retint(file, texture, onlyTint, tint);

            if (!done.TryGetValue(out MaterialEditOutcome? outcome, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, Verb);
            }

            file = outcome.File;
            changes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"retinted {texture} to {Describe(tint)} in {Count(outcome.Sections, "section")}"));
        }

        if (dryRun)
        {
            if (json)
            {
                output.WriteLine(Json(folder: null, changes));
                return Program.Success;
            }

            output.WriteLine("Nothing was written. This is what --dry-run would do:");
            foreach (string change in changes)
            {
                output.WriteLine("  " + change);
            }

            return Program.Success;
        }

        if (destination is null || name is null)
        {
            return Program.Fail(
                Refusal.Unsupported("A mod needs --out and --name. Use --dry-run to see the effect without writing."),
                json, output, error, Verb);
        }

        Result<byte[]> written = EditordataWriter.Write(file);
        if (!written.TryGetValue(out byte[]? edited, out Refusal? unwritable))
        {
            return Program.Fail(unwritable, json, output, error, Verb);
        }

        string virtualPath;
        if (replaces is not null)
        {
            virtualPath = replaces;
        }
        else
        {
            // Provenance is read, never inferred: a flat extraction has no
            // layout to infer from, and a guess one folder out writes a mod the
            // game never reads while looking like it worked.
            Result<FileProvenance> provenance = ExtractionProvenance.Of(source);
            if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? unknown))
            {
                return Program.Fail(unknown, json, output, error, Verb);
            }

            virtualPath = where.VirtualPath;
        }

        Result<ModOutcome> mod = TextureMod.Write(
            destination,
            new ModDetails(name, author, version, description ?? name, preload),
            [new ModFile(virtualPath, edited)]);

        if (!mod.TryGetValue(out ModOutcome? result, out Refusal? failed))
        {
            return Program.Fail(failed, json, output, error, Verb);
        }

        if (json)
        {
            output.WriteLine(Json(result.Folder, changes));
            return Program.Success;
        }

        foreach (string change in changes)
        {
            output.WriteLine("  " + change);
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {Count(result.Files.Length, "file")} into {result.Folder}."));

        // Reported, not refused. The natural order is to repoint first and add
        // the texture second, so a path missing at this moment is as likely to
        // be the next step as a typo. --verify judges the finished folder,
        // where that excuse no longer holds.
        Report(result.Folder, sdfRoot, name, [.. repoints.Select(r => r.To)], output);

        output.WriteLine("Copy that folder into FractureLoader/Mods/ to use it.");
        output.WriteLine(
            "To share it, use 'perianth patch --make': a patch carries only your changes, so nobody "
            + "receives the game's own files.");

        return Program.Success;
    }

    /// <summary>
    /// Says which of the paths just repointed to nothing yet provides.
    /// </summary>
    /// <remarks>
    /// Only those, not every texture the model binds. Without the archives a
    /// path the game ships cannot be told from a missing one, so the broad
    /// question here would bury the one path the user typed under eighty it
    /// never asked about.
    /// </remarks>
    private static void Report(
        string folder, string? sdfRoot, string name, string[] repointed, TextWriter output)
    {
        if (repointed.Length == 0)
        {
            return;
        }

        Result<ImmutableArray<string>> checkedPaths = ModCheck.Provided(folder, sdfRoot, repointed);

        if (!checkedPaths.TryGetValue(out ImmutableArray<string> missing, out _) || missing.IsEmpty)
        {
            return;
        }

        string parent = Directory.GetParent(folder)?.FullName ?? folder;

        output.WriteLine(sdfRoot is null
            ? "This mod does not carry these textures. Pass --sdf-root to find out whether the game already ships them:"
            : "Nothing provides these textures — not the game, and not this mod:");

        foreach (string texture in missing)
        {
            output.WriteLine("  " + texture);
        }

        // Copyable rather than described. The failure this guards against is a
        // path typed twice and differing by a character, so a message that says
        // "add it under the path you named" invites the same mistake again.
        output.WriteLine("Add one to the same mod with:");
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  perianth texture --from YOURS.png --replaces {missing[0]} --out \"{parent}\" --name \"{name}\""));
    }

    /// <summary>Checks a finished mod folder, and refuses if anything is missing.</summary>
    private static int Verify(
        string folder, string? sdfRoot, bool json, TextWriter output, TextWriter error)
    {
        Result<ModReport> run = ModCheck.Run(folder, sdfRoot);
        if (!run.TryGetValue(out ModReport? report, out Refusal? refusal))
        {
            return Program.Fail(refusal, json, output, error, Verb);
        }

        if (json)
        {
            output.WriteLine(VerifyJson(report));
            return report.Missing.IsEmpty ? Program.Success : Program.Refused;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Count(report.Editordata, "material sheet")}, binding {Count(report.Textures, "texture")}."));

        if (!report.Checked)
        {
            output.WriteLine(
                "The game's archives were not given, so a texture the game ships cannot be told from a "
                + "missing one. Pass --sdf-root for the real answer.");
        }

        if (report.Missing.IsEmpty)
        {
            output.WriteLine("Every texture they name is provided.");
            return Program.Success;
        }

        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{Count(report.Missing.Length, "texture")} named by this mod and provided by nothing:"));

        foreach (MissingTexture missing in report.Missing)
        {
            error.WriteLine($"  {missing.Texture}");
            error.WriteLine($"    named by {missing.Editordata}");
        }

        return Program.Refused;
    }

    private static string VerifyJson(ModReport report)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("result", report.Missing.IsEmpty ? "ok" : "refused");
            writer.WriteNumber("editordata", report.Editordata);
            writer.WriteNumber("textures", report.Textures);
            writer.WriteBoolean("archives_given", report.Checked);
            writer.WriteStartArray("missing");

            foreach (MissingTexture missing in report.Missing)
            {
                writer.WriteStartObject();
                writer.WriteString("texture", missing.Texture);
                writer.WriteString("named_by", missing.Editordata);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads <c>R,G,B</c> as three invariant decimals.
    /// </summary>
    /// <remarks>
    /// Decimals rather than <c>#RRGGBB</c> because a tint is matched as well as
    /// set: eight bits per channel cannot express the values the shipped files
    /// hold, so a hex colour could set a tint it could never then select.
    /// </remarks>
    private static bool TryColour(string text, out Rgb colour, [NotNullWhen(false)] out Refusal? refusal)
    {
        colour = default;
        string[] parts = text.Split(',');

        if (parts.Length != 3)
        {
            refusal = Refusal.Unsupported($"A colour is three numbers as R,G,B, and '{text}' has {parts.Length}.");
            return false;
        }

        double[] channels = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out channels[i]))
            {
                refusal = Refusal.Unsupported($"'{parts[i]}' is not a number.");
                return false;
            }
        }

        colour = new Rgb(channels[0], channels[1], channels[2]);
        refusal = null;
        return true;
    }

    private static string Describe(Rgb tint) => string.Create(
        CultureInfo.InvariantCulture, $"{tint.R},{tint.G},{tint.B}");

    /// <summary>Splits at the first '=', so a path may contain later ones.</summary>
    private static bool Split(
        string text, [NotNullWhen(true)] out string? left, [NotNullWhen(true)] out string? right)
    {
        int separator = text.IndexOf('=', StringComparison.Ordinal);

        if (separator <= 0 || separator == text.Length - 1)
        {
            left = null;
            right = null;
            return false;
        }

        left = text[..separator];
        right = text[(separator + 1)..];
        return true;
    }

    private static string Json(string? folder, List<string> changes)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("result", "ok");

            if (folder is null)
            {
                writer.WriteBoolean("dry_run", true);
            }
            else
            {
                writer.WriteString("folder", folder);
            }

            writer.WriteStartArray("changes");
            foreach (string change in changes)
            {
                writer.WriteStringValue(change);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Counts a thing, and pluralizes the word for it.</summary>
    private static string Count(int howMany, string what) => string.Create(
        CultureInfo.InvariantCulture, $"{howMany} {what}{(howMany == 1 ? string.Empty : "s")}");

    private static Refusal Missing(string option) =>
        Refusal.Unsupported(string.Create(CultureInfo.InvariantCulture, $"{option} needs a value."));

    private static bool TryTake(string[] arguments, ref int i, [NotNullWhen(true)] out string? value)
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
