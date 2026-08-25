using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Gltf;

namespace Perianth.Cli;

/// <summary>
/// Writes edited vertex positions from a GLB back into a model, as a mod.
/// </summary>
/// <remarks>
/// <para>
/// Export a model, edit its parts in Blender, and bring them back. Moving the
/// vertices a part has and redrawing the part outright are one verb, because they
/// are one thing to do: <see cref="GeometryImport"/> reads which applies off the
/// file rather than asking. The vertex count must not change either way — that is
/// what keeps this inside files the grammar accounts for.
/// </para>
/// <para>
/// <c>--dry-run</c> for the same reason <c>material</c> has it: an edit reporting
/// far more parts than you meant is the difference between the mod you wanted and
/// a model reshaped throughout, and that is worth seeing before it is written.
/// </para>
/// </remarks>
internal static class GeometryCommand
{
    private const string Verb = "Geometry editing";

    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? modelPath = null;
        string? cameldataPath = null;
        string? fromPath = null;
        string? destination = null;
        string? name = null;
        string author = "unknown";
        string version = "1.0.0";
        string? description = null;
        bool preload = false;
        bool ownUv0 = false;
        bool dryRun = false;
        bool json = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--mmb":
                    if (!TryTake(arguments, ref i, out modelPath))
                    {
                        return Program.Fail(Missing("--mmb"), json, output, error, Verb);
                    }

                    break;

                case "--cameldata":
                    if (!TryTake(arguments, ref i, out cameldataPath))
                    {
                        return Program.Fail(Missing("--cameldata"), json, output, error, Verb);
                    }

                    break;

                case "--from":
                    if (!TryTake(arguments, ref i, out fromPath))
                    {
                        return Program.Fail(Missing("--from"), json, output, error, Verb);
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
                    if (!TryTake(arguments, ref i, out string? declared))
                    {
                        return Program.Fail(Missing("--version"), json, output, error, Verb);
                    }

                    version = declared;
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

                case "--own-uvs":
                    ownUv0 = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported($"{arguments[i]} is not an option the geometry command accepts."),
                        json, output, error, Verb);
            }
        }

        if (modelPath is null || cameldataPath is null || fromPath is null)
        {
            return Program.Fail(
                Refusal.Unsupported(
                    "Reshaping needs --mmb and --cameldata to say which model, and --from to say which " +
                    "edited GLB to read the new positions out of."),
                json, output, error, Verb);
        }

        if (!dryRun && (destination is null || name is null))
        {
            return Program.Fail(
                Refusal.Unsupported("Writing a mod needs --out and --name. Use --dry-run to see what would change."),
                json, output, error, Verb);
        }

        // The file itself, not only its records: rebuilding a part writes a
        // payload back into the bytes it was read from.
        Result<SourceFile> opened = SourceFileReader.Read(modelPath);
        if (!opened.TryGetValue(out SourceFile? modelSource, out Refusal? openRefusal))
        {
            return Program.Fail(openRefusal, json, output, error, Verb);
        }

        Result<MmbModel> model = MmbReader.Read(modelSource);
        if (!model.TryGetValue(out MmbModel? mmb, out Refusal? modelRefusal))
        {
            return Program.Fail(modelRefusal, json, output, error, Verb);
        }

        Result<CameldataFile> cameldata = Read(cameldataPath, CameldataReader.Read);
        if (!cameldata.TryGetValue(out CameldataFile? camel, out Refusal? cameldataRefusal))
        {
            return Program.Fail(cameldataRefusal, json, output, error, Verb);
        }

        byte[] glb;
        try
        {
            glb = File.ReadAllBytes(fromPath);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return Program.Fail(
                Refusal.Resource($"The edited GLB could not be read: {failure.Message}"), json, output, error, Verb);
        }

        Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb);
        if (!meshes.TryGetValue(out ImmutableArray<GlbMesh> read, out Refusal? glbRefusal))
        {
            return Program.Fail(glbRefusal, json, output, error, Verb);
        }

        Result<GeometryImportResult> applied = GeometryImport.Apply(
            modelSource,
            mmb,
            camel,
            [.. read.Select(m => new EditedPart(m.Name, m.Positions, m.PoolSlots, m.Indices, m.Uv0))],
            ownUv0);
        if (!applied.TryGetValue(out GeometryImportResult? edit, out Refusal? editRefusal))
        {
            return Program.Fail(editRefusal, json, output, error, Verb);
        }

        if (!edit.Moved)
        {
            // Every part matched and none moved, which is a GLB that was never
            // edited. Writing it would produce a mod that installs, loads, and
            // changes nothing -- indistinguishable from one that failed.
            return Program.Fail(
                Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Nothing moved in any of the GLB's {edit.Reshaped} parts, and no texture coordinate moved either. The usual cause is editing in Object Mode, which moves the whole object and leaves its vertices where they were: select the part, press Tab for Edit Mode, press A to select its vertices, then move or scale those.")),
                json, output, error, Verb);
        }

        if (dryRun)
        {
            output.WriteLine(json ? Json(null, edit) : Describe(edit));
            return Program.Success;
        }

        Result<byte[]> written = CameldataWriter.Write(edit.Cameldata);
        if (!written.TryGetValue(out byte[]? bytes, out Refusal? writeRefusal))
        {
            return Program.Fail(writeRefusal, json, output, error, Verb);
        }

        // Both files, always. They are a matched pair -- a record's ordinal
        // associates one-to-one with a constant's -- and whether the loader would
        // accept a lone cameldata against the archived model is unmeasured. When
        // no part was rebuilt the MMB is the original byte for byte, so shipping
        // it costs nothing and needs no knowledge of its container.
        Result<FileProvenance> cameldataWhere = ExtractionProvenance.Of(cameldataPath);
        if (!cameldataWhere.TryGetValue(out FileProvenance? camelAt, out Refusal? unknownCameldata))
        {
            return Program.Fail(unknownCameldata, json, output, error, Verb);
        }

        Result<FileProvenance> modelWhere = ExtractionProvenance.Of(modelPath);
        if (!modelWhere.TryGetValue(out FileProvenance? modelAt, out Refusal? unknownModel))
        {
            return Program.Fail(unknownModel, json, output, error, Verb);
        }

        Result<ModOutcome> mod = TextureMod.Write(
            destination!,
            new ModDetails(name!, author, version, description ?? name!, preload),
            [
                new ModFile(camelAt.VirtualPath, bytes),
                new ModFile(modelAt.VirtualPath, edit.Model),
            ]);

        if (!mod.TryGetValue(out ModOutcome? outcome, out Refusal? modRefusal))
        {
            return Program.Fail(modRefusal, json, output, error, Verb);
        }

        if (json)
        {
            output.WriteLine(Json(outcome.Folder, edit));
            return Program.Success;
        }

        output.WriteLine(Describe(edit));
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Wrote {outcome.Files.Length} files into {outcome.Folder}, the edited cameldata and the model beside it."));
        output.WriteLine("Copy that folder into FractureLoader/Mods/ to use it.");
        output.WriteLine(
            "To share it, use 'perianth patch --make': a patch carries only your changes, so nobody "
            + "receives the game's own files.");

        return Program.Success;
    }

    /// <summary>
    /// What changed, in the terms the two halves of the edit differ in.
    /// </summary>
    /// <remarks>
    /// A rebuilt part is reported by its triangles rather than by moved
    /// positions, because it has no old positions to have moved from — the count
    /// that means something is what it now draws.
    /// </remarks>
    private static string Describe(GeometryImportResult edit)
    {
        // The binding node is said because it is chosen rather than asked for: a
        // new part copies the model's last one, and so binds where that one did.
        // Where the setup hides that node the new part never draws, and nothing
        // in the mod folder says why -- which is how an in-game probe came back
        // blank and was read as a fact about the game.
        string added = edit.Added == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Added {edit.Added} parts the edited file named beyond the model's end. They bind to node '{edit.AddedBinding}', copied from the model's last part; if the animation hides that node they will not draw.");

        // Said whether or not it is good news, because nothing in the mod folder
        // shows which happened: a part that brought a layout nobody used is
        // painted by a projector rather than as its author drew it.
        string layout = edit.LayoutIgnored == 0 || edit.LayoutUnconvertible == edit.LayoutIgnored
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.LayoutIgnored - edit.LayoutUnconvertible} part(s) brought a texture layout that was not used, because they work theirs out from position. Right for flat shapes, wrong for solid ones, where one image is smeared down every side. Pass --own-uvs to store the layout instead.");

        // The one case where --own-uvs cannot do what it says, and it used to be
        // accepted without a word. A part that only moved writes no payload, and
        // which layout rule it uses is written there.
        string unconvertible = edit.LayoutUnconvertible == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"--own-uvs could not be applied to {edit.LayoutUnconvertible} part(s): they kept their arrangement, so they were reshaped rather than redrawn, and only a redraw can change which layout rule a part uses. They still work their layout out from position. Change the triangles as well as the points to redraw one.");

        string carried = edit.Uv0Slots == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Moved {edit.Uv0Slots} texture coordinate(s) with the points that carry them.");

        string stored = edit.Converted == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Switched {edit.Converted} part(s) to store the texture layout their mesh brought.");

        string reshaped = edit.Reshaped == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Reshaped {edit.Reshaped} parts: {edit.Slots} vertex positions moved, {edit.Depths} depths changed.");

        string rebuilt = edit.Rebuilt == 0
            ? string.Empty
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Rebuilt {edit.Rebuilt} parts, which now draw {edit.Triangles} triangles.");

        return string.Join(
            Environment.NewLine,
            new[] { added, reshaped, rebuilt, stored, carried, layout, unconvertible }
                .Where(line => line.Length > 0));
    }

    private static string Json(string? folder, GeometryImportResult edit)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            if (folder is not null)
            {
                writer.WriteString("folder", folder);
            }

            writer.WriteNumber("reshaped", edit.Reshaped);
            writer.WriteNumber("rebuilt", edit.Rebuilt);
            writer.WriteString("addedBinding", edit.AddedBinding);
            writer.WriteNumber("textureLayoutStored", edit.Converted);
            writer.WriteNumber("textureLayoutIgnored", edit.LayoutIgnored);
            writer.WriteNumber("textureLayoutUnconvertible", edit.LayoutUnconvertible);
            writer.WriteNumber("positions", edit.Slots);
            writer.WriteNumber("depths", edit.Depths);
            writer.WriteNumber("textureCoordinates", edit.Uv0Slots);
            writer.WriteNumber("triangles", edit.Triangles);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Result<T> Read<T>(string path, Func<SourceFile, Result<T>> read)
    {
        Result<SourceFile> source = SourceFileReader.Read(path);
        return source.IsRefused ? source.Refusal : read(source.Value);
    }

    private static bool TryTake(string[] arguments, ref int index, [NotNullWhen(true)] out string? value)
    {
        if (index + 1 < arguments.Length)
        {
            value = arguments[++index];
            return true;
        }

        value = null;
        return false;
    }

    private static Refusal Missing(string option) => Refusal.Unsupported($"{option} needs a value.");
}
