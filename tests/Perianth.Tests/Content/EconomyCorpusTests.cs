using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// The four routes by which an item reaches the player, against the files the
/// game actually ships.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_ECON</c> names an extraction of
/// <c>camel/game system data/juice</c>, so the default suite stays asset-free.
/// </para>
/// <para>
/// The synthetic tests beside this one state what each route writes. What they
/// cannot state is that it lands in the right place in a 1.1 MB file holding
/// 2,211 declarations, and that is the whole risk: an entry appended one brace
/// early is inside the last entry rather than after it, and the file still
/// parses. So there are two assertions. First, <b>the insertion and nothing
/// else</b> — take the added bytes back out at the point the two files first
/// differ, and require the original, byte for byte; a range that drifted
/// anywhere would leave a difference this cannot remove. Second, <b>where it
/// landed</b>, because a misplaced entry is still one contiguous addition and
/// the first assertion cannot see it: the edited file is read again and the new
/// entry must sit exactly one brace deep inside the block it was aimed at.
/// </para>
/// <para>
/// No name, uid or path from the game appears here. The files are read from the
/// corpus and the declarations to edit are found by shape — the first thing
/// declared with a list of the right kind — so this asserts a property rather
/// than a fingerprint, and keeps working if the content changes.
/// </para>
/// </remarks>
public sealed class EconomyCorpusTests(ITestOutputHelper output)
{
    private const string EconVariable = "PERIANTH_ECON";

    /// <summary>A uid no shipped file can already hold, so a route cannot collide.</summary>
    private static readonly string Invented = ItemEdit.MintUid("perianth economy corpus test");

    /// <summary>The name the recipe route's appended declaration takes.</summary>
    private const string Appended = "Perianth Corpus Test Recipe_Tuning";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_route_inserts_its_entry_and_disturbs_no_other_byte()
    {
        string root = Environment.GetEnvironmentVariable(EconVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {EconVariable} to an extraction of 'camel/game system data/juice'");
            return;
        }

        int checked_ = 0;

        checked_ += Route(root, "items/starting_inventory.juice", "myItemList", (file, name) =>
            ItemEdit.Grant(file, name, Invented, 1), landsInBlock: true);

        checked_ += Route(root, "items/vendorconfig.mvendorconfig", "myVendorItemList", (file, name) =>
            ItemEdit.Stock(file, name, Invented, ItemEdit.GameStates[0]), landsInBlock: true);

        checked_ += Route(root, "loot/loottables.juice", "myLootEntries", (file, name) =>
            ItemEdit.Drop(file, name, Invented), landsInBlock: true);

        // The recipe route is the odd one: it appends a whole declaration rather
        // than an entry, so what must come back out is a copy of the template,
        // and what must be checked is that the copy reads as a declaration of
        // its own with the fields the operation claimed to set.
        checked_ += Route(root, "items/recipes.juice", "myIngredients", (file, name) =>
            ItemEdit.Craft(
                file,
                name,
                Appended,
                Invented,
                ItemEdit.MintUid("perianth economy corpus test result"),
                [new CraftIngredient(ItemEdit.MintUid("perianth economy corpus test scrap"), 2)]),
            declares: Appended);

        Assert.Equal(4, checked_);
    }

    /// <summary>
    /// Applies one route to a shipped file and asserts the insertion is all that
    /// changed.
    /// </summary>
    private int Route(
        string root,
        string relative,
        string block,
        Func<SourceFile, string, Result<ReadOnlyMemory<byte>>> apply,
        bool landsInBlock = false,
        string? declares = null)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{EconVariable} holds no '{relative}'");

        Result<SourceFile> source = SourceFileReader.Read(path);
        if (!source.IsSuccess)
        {
            Assert.Fail($"{relative}: {source.Refusal.Message}");
        }

        string target = FirstDeclarationWith(source.Value, block);
        Result<ReadOnlyMemory<byte>> edited = apply(source.Value, target);
        if (!edited.IsSuccess)
        {
            Assert.Fail($"{relative}: {edited.Refusal.Message}");
        }

        ReadOnlySpan<byte> before = source.Value.Bytes;
        ReadOnlySpan<byte> after = edited.Value.Span;
        Assert.True(after.Length > before.Length, $"{relative}: the edit added nothing");

        int at = 0;
        while (at < before.Length && before[at] == after[at])
        {
            at++;
        }

        int added = after.Length - before.Length;
        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{relative}: {added} bytes at {at} of {before.Length}"));

        // The one assertion that matters. Cutting the added bytes out at the
        // point of divergence must restore the original exactly — so nothing was
        // reordered, renormalised or dropped anywhere in the file.
        byte[] cut = new byte[before.Length];
        after[..at].CopyTo(cut);
        after[(at + added)..].CopyTo(cut.AsSpan(at));
        Assert.True(before.SequenceEqual(cut), $"{relative}: the edit changed bytes it did not add");

        // A pure insertion can still be in the wrong place: one brace early puts
        // the entry inside the last one instead of after it, and the file is
        // still a single contiguous addition. So the edited file is re-read and
        // the entry must sit directly inside the block it was aimed at.
        SourceFile written = SourceFile.FromMemory(path, edited.Value.ToArray());

        if (declares is not null)
        {
            Result<JuiceDocument> made = JuiceDocument.Read(written, declares);
            if (!made.IsSuccess)
            {
                Assert.Fail($"{relative}: the appended declaration does not read: {made.Refusal.Message}");
            }

            Assert.True(made.Value.TryGetField("myItem", out JuiceField item));
            Assert.Equal(
                Invented,
                System.Text.Encoding.Latin1.GetString(
                    made.Value.Bytes.Span.Slice(item.Value.Offset, item.Value.Length)));
        }

        if (!landsInBlock)
        {
            return 1;
        }

        Result<JuiceDocument> reread = JuiceDocument.Read(written, target);
        if (!reread.IsSuccess)
        {
            Assert.Fail($"{relative}: the edited file no longer declares '{target}': {reread.Refusal.Message}");
        }

        Assert.True(reread.Value.TryGetField(block, out JuiceField list) && list.IsBlock);

        string text = System.Text.Encoding.Latin1.GetString(edited.Value.Span);
        int uidAt = text.IndexOf(Invented, StringComparison.Ordinal);
        Assert.InRange(uidAt, list.BlockStart, list.BlockEnd);

        // An entry appended one brace early sits inside the last one, so the
        // list is the same length and holds a malformed member. Counting is what
        // tells the two apart.
        JuiceDocument was = JuiceDocument.Read(source.Value, target).Value;
        _ = was.TryGetField(block, out JuiceField had);

        Assert.Equal(
            Entries(System.Text.Encoding.Latin1.GetString(before), had) + 1,
            Entries(text, list));

        return 1;
    }

    /// <summary>How many entries a block holds — the braces directly inside it.</summary>
    private static int Entries(string text, JuiceField block)
    {
        int count = 0;
        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int at = block.BlockStart; at < block.BlockEnd; at++)
        {
            char c = text[at];
            if (escaped) { escaped = false; continue; }
            if (c == '\\') { escaped = true; }
            else if (c == '"') { inString = !inString; }
            else if (c == '\n') { inString = false; }
            else if (!inString && c == '{') { if (depth++ == 0) { count++; } }
            else if (!inString && c == '}') { depth--; }
        }

        return count;
    }

    /// <summary>
    /// The name of the first declaration in a file carrying a given block field.
    /// </summary>
    /// <remarks>
    /// Found by trying each declaration in file order rather than by naming one,
    /// because a shipped name is game content and does not belong in this
    /// repository. A file whose first declaration is the wrong shape — a recipe
    /// file's <c>include</c> lines, say — simply does not match.
    /// </remarks>
    private static string FirstDeclarationWith(SourceFile file, string block)
    {
        foreach (string candidate in Names(file))
        {
            Result<JuiceDocument> read = JuiceDocument.Read(file, candidate);
            if (read.IsSuccess
                && read.Value.TryGetField(block, out JuiceField field)
                && field.IsBlock)
            {
                return candidate;
            }
        }

        Assert.Fail($"{file.Path} declares nothing carrying '{block}'");
        return string.Empty;
    }

    private static ImmutableArray<string> Names(SourceFile file)
    {
        ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();
        string text = System.Text.Encoding.Latin1.GetString(file.Bytes);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart('\t', ' ');
            if (trimmed.Length == 0 || !char.IsAsciiLetterUpper(trimmed[0]))
            {
                continue;
            }

            int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0)
            {
                continue;
            }

            string rest = trimmed[(space + 1)..];
            int uid = rest.IndexOf(" <", StringComparison.Ordinal);
            if (uid >= 0)
            {
                rest = rest[..uid];
            }

            names.Add(rest.Trim().Trim('"'));
        }

        return names.ToImmutable();
    }
}
