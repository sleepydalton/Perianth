using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;

namespace Perianth.Cli;

/// <summary>
/// Says what an outfit will draw, and what it will leave out.
/// </summary>
/// <remarks>
/// <para>
/// Every decision this makes is invisible in the output: a hairstyle that
/// produced no model and a hairstyle nobody chose look the same in a GLB. The
/// reports that prompted this verb were all of one shape — "it vanished, and
/// nothing said why" — and three of them could not be reproduced at all,
/// because the rules lived only behind the window.
/// </para>
/// <para>
/// So this is not a second way to dress a character. It runs the same
/// <see cref="CostumeCatalogue.Explain"/> the window draws from, and prints the
/// account rather than a file.
/// </para>
/// </remarks>
internal static class CostumeCommand
{
    private const string Verb = "Costume";

    internal static int Run(string[] arguments, TextWriter output, TextWriter error)
    {
        string? sdfRoot = null;
        string? contentRoot = null;
        string? slot = null;
        List<string> wear = [];
        bool list = false;
        bool json = false;

        for (int i = 0; i < arguments.Length; i++)
        {
            switch (arguments[i])
            {
                case "--sdf-root":
                    if (!TryTake(arguments, ref i, out sdfRoot))
                    {
                        return Program.Fail(Missing("--sdf-root"), json, output, error, Verb);
                    }

                    break;

                case "--content-root":
                    if (!TryTake(arguments, ref i, out contentRoot))
                    {
                        return Program.Fail(Missing("--content-root"), json, output, error, Verb);
                    }

                    break;

                case "--slot":
                    if (!TryTake(arguments, ref i, out slot))
                    {
                        return Program.Fail(Missing("--slot"), json, output, error, Verb);
                    }

                    break;

                case "--wear":
                    if (!TryTake(arguments, ref i, out string? one))
                    {
                        return Program.Fail(Missing("--wear"), json, output, error, Verb);
                    }

                    wear.Add(one);
                    break;

                case "--list":
                    list = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Program.Fail(
                        Refusal.Unsupported($"{arguments[i]} is not an option the costume command accepts."),
                        json, output, error, Verb);
            }
        }

        if (sdfRoot is null && contentRoot is null)
        {
            return Program.Fail(
                Refusal.Unsupported(
                    "The item list lives in the game's own files, so this needs --sdf-root to read the "
                    + "archives, or --content-root for a folder already extracted from them."),
                json, output, error, Verb);
        }

        if (!list && wear.Count == 0)
        {
            return Program.Fail(
                Refusal.Unsupported("--list shows what can be worn; --wear NAME says what wearing it draws."),
                json, output, error, Verb);
        }

        using SdfContentSource? archives = sdfRoot is null ? null : new SdfContentSource(sdfRoot);

        ImmutableArray<SdfPathEntry> paths = [];
        if (archives is not null)
        {
            Result<ImmutableArray<SdfPathEntry>> walked = archives.Paths();
            if (!walked.TryGetValue(out paths, out Refusal? walkRefusal))
            {
                return Program.Fail(walkRefusal, json, output, error, Verb);
            }
        }
        else
        {
            Result<ImmutableArray<SdfPathEntry>> folder = FolderIndex.Paths(contentRoot!);
            if (!folder.TryGetValue(out paths, out Refusal? folderRefusal))
            {
                return Program.Fail(folderRefusal, json, output, error, Verb);
            }
        }

        using ContentSources content = new(contentRoot, sdfRoot);
        Result<ImmutableArray<CostumeItem>> read = CostumeCatalogue.Read(content, paths);
        if (!read.TryGetValue(out ImmutableArray<CostumeItem> items, out Refusal? readRefusal))
        {
            return Program.Fail(readRefusal, json, output, error, Verb);
        }

        return list
            ? List(items, slot, json, output)
            : Wear(items, wear, json, output, error);
    }

    /// <summary>Everything wearable, or one slot of it.</summary>
    private static int List(
        ImmutableArray<CostumeItem> items, string? slot, bool json, TextWriter output)
    {
        IEnumerable<CostumeItem> shown = slot is null
            ? items
            : items.Where(item => item.Slot.Contains(slot, StringComparison.OrdinalIgnoreCase));

        ImmutableArray<CostumeItem> listed = [.. shown];

        if (json)
        {
            output.WriteLine(Json(writer =>
            {
                writer.WriteStartArray("items");
                foreach (CostumeItem item in listed)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", item.Name);
                    writer.WriteString("slot", item.Slot);
                    writer.WriteString("kind", item.Kind);
                    writer.WriteNumber("variants", item.Variants.Length);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }));

            return Program.Success;
        }

        foreach (IGrouping<string, CostumeItem> group in listed.GroupBy(item => item.Slot, StringComparer.Ordinal))
        {
            output.WriteLine(group.Key);
            foreach (CostumeItem item in group)
            {
                string cuts = item.Variants.Length > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"  ({item.Variants.Length} cuts)")
                    : string.Empty;
                output.WriteLine($"    {item.Name}{cuts}");
            }
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{listed.Length} entries."));
        return Program.Success;
    }

    /// <summary>What an outfit draws, and what it leaves out.</summary>
    private static int Wear(
        ImmutableArray<CostumeItem> items,
        List<string> wear,
        bool json,
        TextWriter output,
        TextWriter error)
    {
        List<CostumeCatalogue.CostumeWorn> worn = [];
        foreach (string wanted in wear)
        {
            Result<CostumeItem> found = Match(items, wanted);
            if (!found.TryGetValue(out CostumeItem? item, out Refusal? refusal))
            {
                return Program.Fail(refusal, json, output, error, Verb);
            }

            worn.Add(new CostumeCatalogue.CostumeWorn(item));
        }

        // Before anything is explained: an outfit worn over another outfit is
        // not a combination with a wrong-looking result, it is two bodies in one
        // place, and the account below would describe both as drawn.
        Result<string> outfit = CostumeCatalogue.Outfit(worn.Select(one => one.Item));
        if (!outfit.TryGetValue(out string? wearing, out Refusal? clash))
        {
            return Program.Fail(clash, json, output, error, Verb);
        }

        ImmutableArray<CostumeCatalogue.CostumeOutcome> outcomes = CostumeCatalogue.Explain(worn);

        if (json)
        {
            output.WriteLine(Json(writer =>
            {
                writer.WriteString("outfit", wearing);
                writer.WriteStartArray("worn");
                foreach (CostumeCatalogue.CostumeOutcome outcome in outcomes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", outcome.Item.Name);
                    writer.WriteString("slot", outcome.Item.Slot);
                    writer.WriteString("outfit", outcome.Item.Outfit);
                    writer.WriteBoolean("replaces", outcome.Replaces);
                    if (outcome.Piece is not null)
                    {
                        writer.WriteString("drawn_as", outcome.Piece.Kind);
                        writer.WriteString("model", outcome.Piece.ModelPath);
                    }

                    writer.WriteStartArray("blocked");
                    foreach (string kind in outcome.Blocked)
                    {
                        writer.WriteStringValue(kind);
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
            }));

            return Program.Success;
        }

        if (!string.Equals(wearing, CostumeCatalogue.EveryOutfit, StringComparison.Ordinal))
        {
            output.WriteLine($"The {wearing} outfit.");
        }

        foreach (CostumeCatalogue.CostumeOutcome outcome in outcomes)
        {
            output.WriteLine($"{outcome.Item.Slot}: {outcome.Item.Name}");

            if (outcome.Piece is not null)
            {
                // The cut is said even when there is only one, because "drawn as
                // its skull cut" is the whole answer to "why is my hair short".
                output.WriteLine($"    draws  {outcome.Piece.ModelPath}");
                if (outcome.Item.Variants.Length > 1)
                {
                    output.WriteLine($"    as     {outcome.Piece.Kind}");
                }
            }
            else
            {
                output.WriteLine("    draws  nothing: everything it can be drawn as is hidden by what else is worn");
            }

            if (!outcome.Blocked.IsDefaultOrEmpty)
            {
                output.WriteLine($"    hidden {string.Join(", ", outcome.Blocked)}");
            }

            if (outcome.Item.Variants.Length > 1)
            {
                IEnumerable<string> free = outcome.Item.Variants
                    .Select(variant => variant.Kind)
                    .Where(kind => !outcome.Blocked.Contains(kind, StringComparer.Ordinal));
                string left = string.Join(", ", free);
                output.WriteLine($"    left   {(left.Length == 0 ? "none" : left)}");
            }

            if (outcome.Replaces)
            {
                output.WriteLine("    takes off the character's own parts underneath");
            }
        }

        return Program.Success;
    }

    /// <summary>
    /// The one entry a name picks out.
    /// </summary>
    /// <remarks>
    /// Ambiguity refuses rather than taking the first. Half these names are
    /// prefixes of another — a costume and its head piece — and quietly wearing
    /// the wrong one produces an account of an outfit nobody asked about.
    /// </remarks>
    private static Result<CostumeItem> Match(ImmutableArray<CostumeItem> items, string wanted)
    {
        ImmutableArray<CostumeItem> exact =
            [.. items.Where(item => string.Equals(item.Name, wanted, StringComparison.OrdinalIgnoreCase))];

        if (exact.Length == 1)
        {
            return Result.Ok(exact[0]);
        }

        ImmutableArray<CostumeItem> near =
            [.. items.Where(item => item.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))];

        if (near.Length == 1)
        {
            return Result.Ok(near[0]);
        }

        if (near.Length == 0)
        {
            return Refusal.Unsupported($"Nothing wearable is called '{wanted}'. Use --list to see the names.");
        }

        IEnumerable<string> names = near.Take(8).Select(item => item.Name);
        return Refusal.Unsupported(string.Create(
            CultureInfo.InvariantCulture,
            $"'{wanted}' names {near.Length} entries, so it is not clear which to wear: {string.Join(", ", names)}."));
    }

    private static string Json(Action<Utf8JsonWriter> body)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
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
