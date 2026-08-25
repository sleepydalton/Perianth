using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;

namespace Perianth.Cli;

/// <summary>
/// Makes a new character: an actor graph object and the definition that names it.
/// </summary>
/// <remarks>
/// <para>
/// The third of the three routes to "say the asset exists", and the only one
/// that is text <em>and</em> binary. The binary half is a string-table
/// substitution over a copied graph object, which is why it is small and why it
/// waited on <c>BvmWriter</c>.
/// </para>
/// <para>
/// <c>--list</c> comes first for the same reason as <c>prop</c>: a shipped actor
/// names 78 strings, of which a handful are the assets somebody wants to change
/// and the rest are node types and editor bookkeeping.
/// </para>
/// </remarks>
internal static class CharacterCommand
{
    private const string ModelExtension = ".mmb";
    private const string AnimExtension = ".manimsys";

    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? graphTemplate = null;
        string? npcTemplate = null;
        string? name = null;
        string? model = null;
        string? animSystem = null;
        string? displayName = null;
        string? locpack = null;
        string? graphPath = null;
        string? npcPath = null;
        string? contentRoot = null;
        List<(string From, string To)> moves = [];
        bool list = false;

        string? destination = null;
        string? modName = null;
        string author = "unknown";
        string version = "1.0.0";
        string? description = null;
        bool preload = false;
        bool dryRun = false;
        bool json = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            string option = arguments[i];
            switch (option)
            {
                case "--graph-template": if (!TryTake(arguments, ref i, out graphTemplate)) { return Missing(option, json, output, error); } break;
                case "--npc-template": if (!TryTake(arguments, ref i, out npcTemplate)) { return Missing(option, json, output, error); } break;
                case "--name": if (!TryTake(arguments, ref i, out name)) { return Missing(option, json, output, error); } break;
                case "--model": if (!TryTake(arguments, ref i, out model)) { return Missing(option, json, output, error); } break;
                case "--anim-system": if (!TryTake(arguments, ref i, out animSystem)) { return Missing(option, json, output, error); } break;
                case "--display-name": if (!TryTake(arguments, ref i, out displayName)) { return Missing(option, json, output, error); } break;
                case "--locpack": if (!TryTake(arguments, ref i, out locpack)) { return Missing(option, json, output, error); } break;
                case "--graph-path": if (!TryTake(arguments, ref i, out graphPath)) { return Missing(option, json, output, error); } break;
                case "--npc-path": if (!TryTake(arguments, ref i, out npcPath)) { return Missing(option, json, output, error); } break;
                case "--content-root": if (!TryTake(arguments, ref i, out contentRoot)) { return Missing(option, json, output, error); } break;

                case "--repoint":
                    if (!TryTake(arguments, ref i, out string? spelled))
                    {
                        return Missing(option, json, output, error);
                    }

                    int split = spelled.IndexOf('=', StringComparison.Ordinal);
                    if (split <= 0 || split == spelled.Length - 1)
                    {
                        return Program.Fail(
                            Refusal.Unsupported(string.Create(
                                CultureInfo.InvariantCulture,
                                $"'{spelled}' is not a move. Write it as OLD=NEW.")),
                            json, output, error, "Authoring");
                    }

                    moves.Add((spelled[..split], spelled[(split + 1)..]));
                    break;

                case "--list": list = true; break;
                case "--out": if (!TryTake(arguments, ref i, out destination)) { return Missing(option, json, output, error); } break;
                case "--mod-name": if (!TryTake(arguments, ref i, out modName)) { return Missing(option, json, output, error); } break;
                case "--author": if (!TryTake(arguments, ref i, out author!)) { return Missing(option, json, output, error); } break;
                case "--version": if (!TryTake(arguments, ref i, out version!)) { return Missing(option, json, output, error); } break;
                case "--description": if (!TryTake(arguments, ref i, out description)) { return Missing(option, json, output, error); } break;
                case "--preload-custom-assets": preload = true; break;
                case "--dry-run": dryRun = true; break;
                case "--json": json = true; break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{option} is not an option this build accepts.")),
                        json, output, error, "Authoring");
            }
        }

        if (graphTemplate is null)
        {
            return Program.Fail(
                Refusal.Unsupported("A character is drawn through an actor graph object, so --graph-template is required."),
                json, output, error, "Authoring");
        }

        Result<SourceFile> graphSource = SourceFileReader.Read(graphTemplate);
        if (!graphSource.TryGetValue(out SourceFile? graph, out Refusal? unreadable))
        {
            return Program.Fail(unreadable, json, output, error, "Authoring");
        }

        if (list)
        {
            Result<ImmutableArray<GraphString>> named = GraphEdit.List(graph);
            return named.TryGetValue(out ImmutableArray<GraphString> strings, out Refusal? refusal)
                ? Listed(strings, json, output)
                : Program.Fail(refusal, json, output, error, "Authoring");
        }

        if (name is null || (model is null && moves.Count == 0))
        {
            return Program.Fail(
                Refusal.Unsupported(
                    "A new character needs --name and something to draw: --model for its .mmb, or "
                    + "--repoint OLD=NEW to move an entry by name. Run --list first."),
                json, output, error, "Authoring");
        }

        // --model and --anim-system are conveniences over --repoint, resolved by
        // asking the template which entry they mean. Where that is ambiguous the
        // answer is a refusal naming the alternative, never a guess.
        if (model is not null)
        {
            Result<string> sole = GraphEdit.Sole(graph, ModelExtension);
            if (!sole.TryGetValue(out string? from, out Refusal? ambiguous))
            {
                return Program.Fail(ambiguous, json, output, error, "Authoring");
            }

            moves.Insert(0, (from, model));
        }

        if (animSystem is not null)
        {
            Result<string> sole = GraphEdit.Sole(graph, AnimExtension);
            if (!sole.TryGetValue(out string? from, out Refusal? ambiguous))
            {
                return Program.Fail(ambiguous, json, output, error, "Authoring");
            }

            moves.Add((from, animSystem));
        }

        Result<GraphEdited> edited = GraphEdit.Repoint(graph, moves);
        if (!edited.TryGetValue(out GraphEdited? built, out Refusal? refused))
        {
            return Program.Fail(refused, json, output, error, "Authoring");
        }

        string graphVirtual = graphPath ?? $"camel/graph objects/actor/{name.ToLowerInvariant()}.mgraphobject";
        List<ModFile> files = [new ModFile(graphVirtual, built.Bytes)];
        List<Diagnostic> notes = [];
        CharacterDerivation? character = null;

        if (npcTemplate is not null)
        {
            Result<SourceFile> npcSource = SourceFileReader.Read(npcTemplate);
            if (!npcSource.TryGetValue(out SourceFile? npc, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Authoring");
            }

            Result<CharacterDerivation> derived =
                CharacterEdit.Derive(npc, name, graphVirtual, displayName);
            if (!derived.TryGetValue(out character, out Refusal? failed))
            {
                return Program.Fail(failed, json, output, error, "Authoring");
            }

            files.Add(new ModFile(npcPath ?? CharacterEdit.ProposePath(name), character.Npc));

            if (character.Inherits is string parent)
            {
                notes.Add(new Diagnostic(
                    DiagnosticIds.InputChangedDuringRead,
                    DiagnosticSeverity.Warning,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The template derives from '{parent}' and the copy keeps that, so the new character inherits whatever it declares — including fields this never mentions.")));
            }

            if (displayName is not null && locpack is null)
            {
                notes.Add(new Diagnostic(
                    DiagnosticIds.InputChangedDuringRead,
                    DiagnosticSeverity.Warning,
                    "--display-name was given without --locpack, so the character carries the name but "
                    + "nothing resolves it."));
            }
            else if (displayName is not null)
            {
                Result<ModFile> row = Localised(locpack!, character.NameGuid!, character.DisplayName!);
                if (!row.TryGetValue(out ModFile? written, out Refusal? unwritten))
                {
                    return Program.Fail(unwritten, json, output, error, "Authoring");
                }

                files.Add(written);
            }
        }
        else
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                "No --npc-template was given, so this wrote a graph object and nothing that names it. "
                + "A graph object alone is art with no character attached to it."));
        }

        // A graph object names a model and a rig, and a part draws only where the
        // rig declares the node its label binds to. Repointing one without the
        // other pairs a model with a rig that may have nowhere to put it, which
        // is a mod that installs, loads and draws nothing different -- see
        // Roadmap §10.118. Checked here rather than refused in Core, because
        // shipping the matching system in the same mod is the correct fix.
        foreach (Diagnostic note in RigNotes(graph, model, animSystem, contentRoot))
        {
            notes.Add(note);
        }

        notes.Add(new Diagnostic(
            DiagnosticIds.InputChangedDuringRead,
            DiagnosticSeverity.Warning,
            "Whether the game loads a character definition the archives never held is unverified, and "
            + "an .mnpc's file name is not its declared name — 875 of 1,824 differ — so nothing here "
            + "can settle where a new one must go. Repointing a character the game already loads is "
            + "the operation that asks nothing new of the loader."));

        if (dryRun)
        {
            return Describe(built, character, files, notes, folder: null, json, output, error);
        }

        if (destination is null || modName is null)
        {
            return Program.Fail(
                Refusal.Unsupported("Writing a mod needs --out and --mod-name. Use --dry-run to see what it would write."),
                json, output, error, "Authoring");
        }

        Result<ModOutcome> wrote = TextureMod.Write(
            destination,
            new ModDetails(modName, author, version, description ?? modName, preload),
            files);

        return wrote.TryGetValue(out ModOutcome? outcome, out Refusal? unfinished)
            ? Describe(built, character, files, notes, outcome.Folder, json, output, error)
            : Program.Fail(unfinished, json, output, error, "Authoring");
    }

    /// <summary>
    /// What the rig check has to say about the model and animation system the
    /// edited graph object will name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Silent without <c>--content-root</c>, because the check needs to read the
    /// model and the setup and this verb otherwise deals only in path strings.
    /// It takes a loose tree and not the archives on purpose: anyone running this
    /// already has an extraction, since <c>--graph-template</c> is a file out of
    /// one, so requiring the game's archives as well would be asking for
    /// something the caller has no reason to have to hand.
    /// </para>
    /// <para>
    /// A failure to check is not a failure of the edit. The root may hold neither
    /// file, the system may name several setups, and none of that says anything
    /// about the graph object being written — so it is reported and the write
    /// goes ahead.
    /// </para>
    /// </remarks>
    private static IEnumerable<Diagnostic> RigNotes(
        SourceFile graph, string? model, string? animSystem, string? contentRoot)
    {
        if (contentRoot is null)
        {
            if (model is not null || animSystem is not null)
            {
                yield return new Diagnostic(
                    DiagnosticIds.InputChangedDuringRead,
                    DiagnosticSeverity.Warning,
                    "A graph object names a model and an animation system, and a part draws only where "
                    + "the rig declares the node it binds to. Pass --content-root to have that checked; "
                    + "without it, a model paired with a rig that cannot place it writes a mod that "
                    + "installs, loads and draws nothing different.");
            }

            yield break;
        }

        string? modelPath = model ?? Resolved(graph, ModelExtension);
        string? systemPath = animSystem ?? Resolved(graph, AnimExtension);
        if (modelPath is null || systemPath is null)
        {
            yield return new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                "The template does not name exactly one model and one animation system, so the rig "
                + "check has nothing single to compare.");
            yield break;
        }

        using ContentSources content = new(contentRoot, sdfRoot: null);

        // The character being changed is the one the template draws today, so it
        // is that model whose other graph objects matter: each of them still
        // draws it wherever it applies. Both are deliberate things to do, so this
        // says what the set is and chooses nothing.
        string? replacing = Resolved(graph, ModelExtension);
        if (model is not null && replacing is not null)
        {
            Result<ImmutableArray<string>> others = AnimationSystems.ActorsNaming(content, replacing);
            if (others.TryGetValue(out ImmutableArray<string> naming, out _) && naming.Length > 1)
            {
                yield return new Diagnostic(
                    DiagnosticIds.InputChangedDuringRead,
                    DiagnosticSeverity.Warning,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"{naming.Length} graph objects draw '{replacing}', and this changes one of them. "
                        + $"The character keeps its old look wherever the others apply. Edit each in turn if "
                        + $"that is not what you want: {string.Join(", ", naming)}"));
            }
        }

        Result<RigCoverage> checked_ = AnimationSystems.Coverage(content, modelPath, systemPath);
        if (!checked_.TryGetValue(out RigCoverage? coverage, out Refusal? refusal))
        {
            yield return new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The model and rig could not be compared, so this is unchecked: {refusal.Message}"));
            yield break;
        }

        if (coverage.Complete)
        {
            yield return new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The rig fits: '{coverage.Setup}' declares all {coverage.Bindings} nodes the model's parts bind to."));
            yield break;
        }

        yield return new Diagnostic(
            DiagnosticIds.InputChangedDuringRead,
            DiagnosticSeverity.Warning,
            string.Create(
                CultureInfo.InvariantCulture,
                $"The rig does not fit this model: '{coverage.Setup}' declares {coverage.Declared} of the "
                + $"{coverage.Bindings} nodes its parts bind to, so {coverage.Unplaced.Length} have nothing to place them "
                + $"and those parts will not draw. Move the animation system with the model (--anim-system), or pick a "
                + $"model whose rig this one already declares. Unplaced, first few: "
                + $"{string.Join(", ", coverage.Unplaced.Take(6))}"));
    }

    /// <summary>The template's sole entry of an extension, or null where it has no single one.</summary>
    private static string? Resolved(SourceFile graph, string extension)
    {
        Result<string> sole = GraphEdit.Sole(graph, extension);
        return sole.IsSuccess ? sole.Value : null;
    }

    private static Result<ModFile> Localised(string locpack, string key, string text)
    {
        Result<FileProvenance> provenance = ExtractionProvenance.Of(locpack);
        if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? unknown))
        {
            return unknown;
        }

        Result<SourceFile> source = SourceFileReader.Read(locpack);
        if (!source.TryGetValue(out SourceFile? file, out Refusal? unreadable))
        {
            return unreadable;
        }

        Result<ReadOnlyMemory<byte>> added = ItemEdit.AddLocalisation(file, key, text);
        return added.TryGetValue(out ReadOnlyMemory<byte> bytes, out Refusal? refused)
            ? Result.Ok(new ModFile(where.VirtualPath, bytes))
            : refused;
    }

    private static int Listed(ImmutableArray<GraphString> strings, bool json, TextWriter output)
    {
        if (json)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("result", "ok");
                writer.WriteStartArray("strings");

                foreach (GraphString entry in strings)
                {
                    writer.WriteStartObject();
                    writer.WriteString("value", entry.Value);
                    writer.WriteNumber("uses", entry.Uses);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return Program.Success;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{strings.Length} strings, of which the assets are:"));

        foreach (GraphString entry in strings)
        {
            // A path is what somebody repoints; the rest are node types and pin
            // names, and listing all 78 buries the five that matter.
            if (entry.Uses > 0 && entry.Value.Contains('/', StringComparison.Ordinal))
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture, $"  {entry.Uses}x  {entry.Value}"));
            }
        }

        return Program.Success;
    }

    private static int Describe(
        GraphEdited graph,
        CharacterDerivation? character,
        List<ModFile> files,
        List<Diagnostic> notes,
        string? folder,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (json)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("result", "ok");

                if (character is not null)
                {
                    writer.WriteString("uid", character.Uid);
                    writer.WriteString("graphObject", character.GraphObject);

                    if (character.NameGuid is not null)
                    {
                        writer.WriteString("nameGuid", character.NameGuid);
                    }

                    if (character.Inherits is not null)
                    {
                        writer.WriteString("inherits", character.Inherits);
                    }
                }

                if (folder is null)
                {
                    writer.WriteBoolean("dryRun", true);
                }
                else
                {
                    writer.WriteString("folder", folder);
                }

                writer.WriteStartArray("repointed");
                foreach ((string from, string to) in graph.Repointed)
                {
                    writer.WriteStartObject();
                    writer.WriteString("from", from);
                    writer.WriteString("to", to);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("files");
                foreach (ModFile file in files)
                {
                    writer.WriteStringValue(file.VirtualPath);
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

            output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return Program.Success;
        }

        foreach ((string from, string to) in graph.Repointed)
        {
            output.WriteLine($"  {from}");
            output.WriteLine($"    -> {to}");
        }

        if (character is not null)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"Character {character.Uid}"));

            if (character.DisplayName is not null)
            {
                output.WriteLine($"  shown as \"{character.DisplayName}\", keyed {character.NameGuid}");
            }
        }

        if (folder is null)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"Would write {files.Count} files. Nothing was written."));

            foreach (ModFile file in files)
            {
                output.WriteLine("  " + file.VirtualPath);
            }
        }
        else
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"Wrote {files.Count} files into {folder}."));
            output.WriteLine("Copy that folder into FractureLoader/Mods/ to use it.");
            output.WriteLine(
                "To share it, use 'perianth patch --make': a patch carries only your changes, so nobody "
                + "receives the game's own files.");
        }

        foreach (Diagnostic note in notes)
        {
            error.WriteLine(note.Message);
        }

        return Program.Success;
    }

    private static int Missing(string option, bool json, TextWriter output, TextWriter error) =>
        Program.Fail(
            Refusal.Unsupported(string.Create(CultureInfo.InvariantCulture, $"{option} needs a value.")),
            json,
            output,
            error,
            "Authoring");

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
