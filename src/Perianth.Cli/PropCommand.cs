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
using Perianth.Formats.Io;

namespace Perianth.Cli;

/// <summary>
/// Puts a prop into the world, by copying one that is already standing there.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart to <c>item</c> for the other half of "say the asset exists".
/// An item is worn and a prop stands in a place, so this takes a layer and a
/// position where that takes a slot and an obtain route.
/// </para>
/// <para>
/// <c>--list</c> comes first for a reason: a layer holds up to twenty-five kinds
/// of entity, the template decides everything this does not set, and none of
/// that is visible in the file afterwards. Choosing a template blind is the
/// mistake the listing exists to prevent.
/// </para>
/// </remarks>
internal static class PropCommand
{
    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? layerPath = null;
        string? template = null;
        string? name = null;
        string? graphObject = null;
        double[] position = [0, 0, 0];
        bool given = false;
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
                case "--layer": if (!TryTake(arguments, ref i, out layerPath)) { return Missing(option, json, output, error); } break;
                case "--template": if (!TryTake(arguments, ref i, out template)) { return Missing(option, json, output, error); } break;
                case "--name": if (!TryTake(arguments, ref i, out name)) { return Missing(option, json, output, error); } break;
                case "--graph-object": if (!TryTake(arguments, ref i, out graphObject)) { return Missing(option, json, output, error); } break;

                case "--at":
                    if (!TryTake(arguments, ref i, out string? spelled))
                    {
                        return Missing(option, json, output, error);
                    }

                    Result<double[]> read = ParsePosition(spelled);
                    if (!read.TryGetValue(out double[]? point, out Refusal? bad))
                    {
                        return Program.Fail(bad, json, output, error, "Authoring");
                    }

                    position = point;
                    given = true;
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
                        json,
                        output,
                        error,
                        "Authoring");
            }
        }

        if (layerPath is null)
        {
            return Program.Fail(
                Refusal.Unsupported("A prop stands in a layer, so --layer is required. Use --list to see what one holds."),
                json, output, error, "Authoring");
        }

        Result<SourceFile> source = SourceFileReader.Read(layerPath);
        if (!source.TryGetValue(out SourceFile? layer, out Refusal? unreadable))
        {
            return Program.Fail(unreadable, json, output, error, "Authoring");
        }

        if (list)
        {
            Result<ImmutableArray<LayerEntity>> held = PropPlace.List(layer);
            return held.TryGetValue(out ImmutableArray<LayerEntity> entities, out Refusal? refusal)
                ? Listed(entities, json, output)
                : Program.Fail(refusal, json, output, error, "Authoring");
        }

        if (template is null || name is null || graphObject is null || !given)
        {
            return Program.Fail(
                Refusal.Unsupported(
                    "Placing a prop needs --template (one already in the layer, to copy), --name, "
                    + "--graph-object and --at X,Y,Z. The template decides everything not named here, "
                    + "so run --list first."),
                json, output, error, "Authoring");
        }

        Result<PropPlacement> placed = PropPlace.Beside(
            layer, template, name, graphObject, new PropPosition(position[0], position[1], position[2]));

        if (!placed.TryGetValue(out PropPlacement? placement, out Refusal? refused))
        {
            return Program.Fail(refused, json, output, error, "Authoring");
        }

        // Read, never inferred, as everywhere else: a layer written to the wrong
        // archive path is a mod the game ignores while looking as though it
        // worked. The path is long and uid-shaped, so typing it is worse.
        Result<FileProvenance> provenance = ExtractionProvenance.Of(layerPath);
        if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? unknown))
        {
            return Program.Fail(unknown, json, output, error, "Authoring");
        }

        List<Diagnostic> notes = [.. placement.Diagnostics];
        notes.Add(new Diagnostic(
            DiagnosticIds.InputChangedDuringRead,
            DiagnosticSeverity.Warning,
            "The graph object is named, not checked: nothing here confirms the archives hold it, or that "
            + "it names a model. A prop pointing at a path that does not exist draws nothing, and the "
            + "layer looks correct either way."));

        ModFile file = new(where.VirtualPath, placement.Layer);

        if (dryRun)
        {
            return Describe(placement, file, notes, folder: null, json, output, error);
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
            [file]);

        return wrote.TryGetValue(out ModOutcome? outcome, out Refusal? failed)
            ? Describe(placement, file, notes, outcome.Folder, json, output, error)
            : Program.Fail(failed, json, output, error, "Authoring");
    }

    private static int Listed(
        ImmutableArray<LayerEntity> entities, bool json, TextWriter output)
    {
        if (json)
        {
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("result", "ok");
                writer.WriteStartArray("entities");

                foreach (LayerEntity entity in entities)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", entity.Name);
                    writer.WriteString("type", entity.Type);
                    writer.WriteNumber("chunk", entity.Chunk);

                    if (entity.Resource is not null)
                    {
                        writer.WriteString("resource", entity.Resource);
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            output.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
            return Program.Success;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{entities.Length} entities:"));

        foreach (LayerEntity entity in entities)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  [{entity.Chunk}] {entity.Type,-16} {entity.Name}"));

            if (entity.Resource is not null)
            {
                output.WriteLine("      " + entity.Resource);
            }
        }

        return Program.Success;
    }

    private static int Describe(
        PropPlacement placement,
        ModFile file,
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
                writer.WriteString("uid", placement.Uid);
                writer.WriteString("template", placement.Template);
                writer.WriteNumber("chunk", placement.Chunk);
                writer.WriteString("path", file.VirtualPath);

                if (folder is null)
                {
                    writer.WriteBoolean("dryRun", true);
                }
                else
                {
                    writer.WriteString("folder", folder);
                }

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

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Placed {placement.Uid} in chunk {placement.Chunk}, copied from '{placement.Template}'."));
        output.WriteLine("  " + file.VirtualPath);

        if (folder is null)
        {
            output.WriteLine("Nothing was written.");
        }
        else
        {
            output.WriteLine($"Wrote 1 file into {folder}.");
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

    private static Result<double[]> ParsePosition(string spelled)
    {
        string[] parts = spelled.Split(',');
        if (parts.Length != 3)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{spelled}' is not a position. Write it as X,Y,Z."));
        }

        double[] values = new double[3];
        for (int i = 0; i < 3; i++)
        {
            if (!double.TryParse(
                    parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{parts[i].Trim()}' is not a number."));
            }
        }

        return Result.Ok(values);
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
