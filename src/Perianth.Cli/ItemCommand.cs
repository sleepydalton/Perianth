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
/// Makes a new item the game knows about, and says how the player gets it.
/// </summary>
/// <remarks>
/// <para>
/// The verb for the rung above material and geometry authoring: those change
/// how something already in the game looks, and this declares something that
/// was not there. It writes the same kind of mod folder the texture and
/// material verbs do, so an item joins the patch flow with nothing new learned.
/// </para>
/// <para>
/// <b>Declaring an item is not the same as the player having one.</b> Nothing
/// inside an item says where it comes from — shops name items rather than the
/// other way round — so this takes an obtain route as well, and warns rather
/// than refuses when given none, because the file is still worth writing while
/// its route is decided.
/// </para>
/// <para>
/// Every input is a file that was extracted, and its archive path comes from
/// the recorded provenance rather than from where it sits, exactly as
/// <c>--original</c> does for a texture. The new item is the one exception: it
/// has no original, and its path is settled by its name
/// (<see cref="ItemEdit.ProposePath"/>) rather than chosen.
/// </para>
/// </remarks>
internal static class ItemCommand
{
    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? template = null;
        string? name = null;
        string? model = null;
        string? displayName = null;
        string? locpack = null;

        string? vendors = null;
        string? shop = null;
        string gameState = ItemEdit.GameStates[0];

        string? inventory = null;
        string? setting = null;
        int count = 1;

        string? loot = null;
        string? table = null;
        double chance = 1.0;
        int quantityMin = 1;
        int quantityMax = 1;

        string? recipes = null;
        string? recipeTemplate = null;
        string? recipeName = null;
        string? recipeItem = null;
        List<CraftIngredient> ingredients = [];

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
                case "--template": if (!TryTake(arguments, ref i, out template)) { return Missing(option, json, output, error); } break;
                case "--name": if (!TryTake(arguments, ref i, out name)) { return Missing(option, json, output, error); } break;
                case "--model": if (!TryTake(arguments, ref i, out model)) { return Missing(option, json, output, error); } break;
                case "--display-name": if (!TryTake(arguments, ref i, out displayName)) { return Missing(option, json, output, error); } break;
                case "--locpack": if (!TryTake(arguments, ref i, out locpack)) { return Missing(option, json, output, error); } break;

                case "--vendors": if (!TryTake(arguments, ref i, out vendors)) { return Missing(option, json, output, error); } break;
                case "--shop": if (!TryTake(arguments, ref i, out shop)) { return Missing(option, json, output, error); } break;
                case "--game-state": if (!TryTake(arguments, ref i, out gameState!)) { return Missing(option, json, output, error); } break;

                case "--inventory": if (!TryTake(arguments, ref i, out inventory)) { return Missing(option, json, output, error); } break;
                case "--setting": if (!TryTake(arguments, ref i, out setting)) { return Missing(option, json, output, error); } break;
                case "--count": if (!TryNumber(arguments, ref i, out count)) { return Missing(option, json, output, error); } break;

                case "--loot": if (!TryTake(arguments, ref i, out loot)) { return Missing(option, json, output, error); } break;
                case "--table": if (!TryTake(arguments, ref i, out table)) { return Missing(option, json, output, error); } break;
                case "--chance": if (!TryReal(arguments, ref i, out chance)) { return Missing(option, json, output, error); } break;
                case "--quantity-min": if (!TryNumber(arguments, ref i, out quantityMin)) { return Missing(option, json, output, error); } break;
                case "--quantity-max": if (!TryNumber(arguments, ref i, out quantityMax)) { return Missing(option, json, output, error); } break;

                case "--recipes": if (!TryTake(arguments, ref i, out recipes)) { return Missing(option, json, output, error); } break;
                case "--recipe-template": if (!TryTake(arguments, ref i, out recipeTemplate)) { return Missing(option, json, output, error); } break;
                case "--recipe-name": if (!TryTake(arguments, ref i, out recipeName)) { return Missing(option, json, output, error); } break;
                case "--recipe-item": if (!TryTake(arguments, ref i, out recipeItem)) { return Missing(option, json, output, error); } break;

                case "--ingredient":
                    if (!TryTake(arguments, ref i, out string? spelled))
                    {
                        return Missing(option, json, output, error);
                    }

                    Result<CraftIngredient> read = ParseIngredient(spelled);
                    if (!read.TryGetValue(out CraftIngredient ingredient, out Refusal? bad))
                    {
                        return Program.Fail(bad, json, output, error, "Authoring");
                    }

                    ingredients.Add(ingredient);
                    break;

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

        if (template is null || name is null || model is null)
        {
            return Program.Fail(
                Refusal.Unsupported(
                    "A new item needs --template (a shipped item of the slot wanted), --name and --model. "
                    + "The template's declared class is the slot, so copying a hat makes a hat."),
                json,
                output,
                error,
                "Authoring");
        }

        Result<SourceFile> source = SourceFileReader.Read(template);
        if (!source.TryGetValue(out SourceFile? file, out Refusal? unreadable))
        {
            return Program.Fail(unreadable, json, output, error, "Authoring");
        }

        Result<ItemDerivation> made = ItemEdit.Derive(file, name, model, displayName);
        if (!made.TryGetValue(out ItemDerivation? item, out Refusal? refused))
        {
            return Program.Fail(refused, json, output, error, "Authoring");
        }

        List<ModFile> files = [new ModFile(ItemEdit.ProposePath(name), item.Item)];
        List<string> routes = [];
        List<Diagnostic> notes = [];

        if (item.ExtraPieces > 0)
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The template is a parent entry claiming {item.ExtraPieces} variants, and those are the records that draw. The copy keeps them, so it is a new menu entry wearing the template's own pieces and --model repoints only the parent. To change what is drawn, copy one of the variants instead.")));
        }

        if (displayName is not null)
        {
            if (locpack is null)
            {
                notes.Add(new Diagnostic(
                    DiagnosticIds.InputChangedDuringRead,
                    DiagnosticSeverity.Warning,
                    "--display-name was given without --locpack, so the item carries the name but nothing "
                    + "resolves it. The name shown in game comes from menus.locpack, keyed by the item's own guid."));
            }
            else
            {
                Result<ModFile> localised = Edit(
                    locpack, f => ItemEdit.AddLocalisation(f, item.NameGuid!, item.DisplayName!));
                if (!localised.TryGetValue(out ModFile? row, out Refusal? bad))
                {
                    return Program.Fail(bad, json, output, error, "Authoring");
                }

                files.Add(row);
            }
        }

        if (vendors is not null || shop is not null)
        {
            if (vendors is null || shop is null)
            {
                return Program.Fail(
                    Refusal.Unsupported("Selling it needs both --vendors and --shop: one file holds forty shops."),
                    json, output, error, "Authoring");
            }

            Result<ModFile> stocked = Edit(vendors, f => ItemEdit.Stock(f, shop, item.Uid, gameState));
            if (!stocked.TryGetValue(out ModFile? written, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Authoring");
            }

            files.Add(written);
            routes.Add($"sold by '{shop}' from {gameState}");
        }

        if (inventory is not null || setting is not null)
        {
            if (inventory is null || setting is null)
            {
                return Program.Fail(
                    Refusal.Unsupported("Granting it needs both --inventory and --setting."),
                    json, output, error, "Authoring");
            }

            Result<ModFile> granted = Edit(inventory, f => ItemEdit.Grant(f, setting, item.Uid, count));
            if (!granted.TryGetValue(out ModFile? written, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Authoring");
            }

            files.Add(written);
            routes.Add($"granted {count} through '{setting}'");
        }

        if (loot is not null || table is not null)
        {
            if (loot is null || table is null)
            {
                return Program.Fail(
                    Refusal.Unsupported("Dropping it needs both --loot and --table: one file holds 2,211 of them."),
                    json, output, error, "Authoring");
            }

            Result<ModFile> dropped = Edit(
                loot, f => ItemEdit.Drop(f, table, item.Uid, chance, quantityMin, quantityMax));
            if (!dropped.TryGetValue(out ModFile? written, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Authoring");
            }

            files.Add(written);
            routes.Add($"found in '{table}'");
        }

        if (recipes is not null || recipeTemplate is not null || recipeName is not null || recipeItem is not null)
        {
            if (recipes is null || recipeTemplate is null || recipeName is null || recipeItem is null)
            {
                return Program.Fail(
                    Refusal.Unsupported(
                        "Crafting it needs --recipes, --recipe-template, --recipe-name and --recipe-item. "
                        + "--recipe-item is the uid of the item that *is* the recipe, which the player holds — "
                        + "make that item too, and give it a route of its own."),
                    json, output, error, "Authoring");
            }

            Result<ModFile> crafted = Edit(recipes, f => ItemEdit.Craft(
                f, recipeTemplate, recipeName, recipeItem, item.Uid, [.. ingredients]));
            if (!crafted.TryGetValue(out ModFile? written, out Refusal? bad))
            {
                return Program.Fail(bad, json, output, error, "Authoring");
            }

            files.Add(written);
            routes.Add($"crafted by '{recipeName}'");
        }

        if (routes.Count == 0)
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                "The item is declared but unobtainable: nothing sells, grants, drops or crafts it. "
                + "Add --vendors/--shop, --inventory/--setting, --loot/--table or --recipes."));
        }

        // Said whether or not a route was given, because it is the one thing
        // here that has never been tested in game and no amount of correct
        // authoring settles it.
        //
        // The reason given here used to be the "%s.mitem" format string in the
        // executable -- an item resolved by turning its name into a path, with
        // no wildcard beside it. That reading is dead: the string has no
        // references and is best read as dead authoring code, and discovery is
        // a wildcard listing of the items folder, measured rather than inferred
        // (Roadmap §10.132). The conclusion is unchanged and the argument for it
        // now runs the other way, which is worth saying correctly -- a stale
        // reason is how a settled question gets reopened from the wrong end.
        notes.Add(new Diagnostic(
            DiagnosticIds.InputChangedDuringRead,
            DiagnosticSeverity.Warning,
            "Whether the game loads an item file the archives never held is unverified. The game builds "
            + "its item list by asking the folder for every .mitem in it, and the mod loader intercepts "
            + "files being opened rather than folders being listed — so a file that exists only to be "
            + "found this way is exactly the untested case. Replacing an item the game already ships is "
            + "confirmed to work."));

        if (dryRun)
        {
            return Describe(item, files, routes, notes, folder: null, json, output, error);
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

        return wrote.TryGetValue(out ModOutcome? outcome, out Refusal? failed)
            ? Describe(item, files, routes, notes, outcome.Folder, json, output, error)
            : Program.Fail(failed, json, output, error, "Authoring");
    }

    /// <summary>
    /// Applies one edit to an extracted file, pairing the result with the
    /// archive path the extraction recorded for it.
    /// </summary>
    /// <remarks>
    /// The path is read, never inferred. An economy file written one folder out
    /// produces a mod the game silently ignores while looking as though it
    /// worked, which is the same reason <c>texture --original</c> reads
    /// provenance rather than guessing from the directory.
    /// </remarks>
    private static Result<ModFile> Edit(
        string path, Func<SourceFile, Result<ReadOnlyMemory<byte>>> apply)
    {
        Result<FileProvenance> provenance = ExtractionProvenance.Of(path);
        if (!provenance.TryGetValue(out FileProvenance? where, out Refusal? unknown))
        {
            return unknown;
        }

        Result<SourceFile> source = SourceFileReader.Read(path);
        if (!source.TryGetValue(out SourceFile? file, out Refusal? unreadable))
        {
            return unreadable;
        }

        Result<ReadOnlyMemory<byte>> edited = apply(file);
        return edited.TryGetValue(out ReadOnlyMemory<byte> bytes, out Refusal? refused)
            ? Result.Ok(new ModFile(where.VirtualPath, bytes))
            : refused;
    }

    /// <summary>Reads <c>UID:count</c>, or <c>UID</c> for one.</summary>
    private static Result<CraftIngredient> ParseIngredient(string spelled)
    {
        int colon = spelled.LastIndexOf(':');
        if (colon < 0)
        {
            return Result.Ok(new CraftIngredient(spelled, 1));
        }

        return int.TryParse(
            spelled[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int many)
            ? Result.Ok(new CraftIngredient(spelled[..colon], many))
            : Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{spelled}' is not an ingredient. Write it as UID:count, or UID for one."));
    }

    private static int Describe(
        ItemDerivation item,
        List<ModFile> files,
        List<string> routes,
        List<Diagnostic> notes,
        string? folder,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        if (json)
        {
            output.WriteLine(Json(item, files, routes, notes, folder));
            return Program.Success;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Item {item.Uid} at {files[0].VirtualPath}"));

        if (item.DisplayName is not null)
        {
            output.WriteLine($"  shown as \"{item.DisplayName}\", keyed {item.NameGuid}");
        }

        foreach (string route in routes)
        {
            output.WriteLine("  " + route);
        }

        if (folder is null)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Would write {files.Count} files. Nothing was written."));

            foreach (ModFile file in files)
            {
                output.WriteLine("  " + file.VirtualPath);
            }
        }
        else
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Wrote {files.Count} files into {folder}."));
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

    private static string Json(
        ItemDerivation item,
        List<ModFile> files,
        List<string> routes,
        List<Diagnostic> notes,
        string? folder)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("result", "ok");
            writer.WriteString("uid", item.Uid);

            if (item.NameGuid is not null)
            {
                writer.WriteString("nameGuid", item.NameGuid);
                writer.WriteString("displayName", item.DisplayName);
            }

            if (folder is null)
            {
                writer.WriteBoolean("dryRun", true);
            }
            else
            {
                writer.WriteString("folder", folder);
            }

            writer.WriteStartArray("files");
            foreach (ModFile file in files)
            {
                writer.WriteStringValue(file.VirtualPath);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("routes");
            foreach (string route in routes)
            {
                writer.WriteStringValue(route);
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

    private static bool TryNumber(string[] arguments, ref int i, out int value)
    {
        value = 0;
        return TryTake(arguments, ref i, out string? said)
            && int.TryParse(said, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryReal(string[] arguments, ref int i, out double value)
    {
        value = 0;
        return TryTake(arguments, ref i, out string? said)
            && double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
