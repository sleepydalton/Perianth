using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Xunit;

namespace Perianth.Tests.Juice;

/// <summary>
/// Indexes every item definition under a corpus root and writes each one back.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>PERIANTH_ITEMS</c> names a directory of extracted
/// <c>.mitem</c> files, so the default suite stays asset-free. Point it at an
/// extraction of <c>camel/game system data/juice/items</c>.
/// </para>
/// <para>
/// The oracle is the one every other writer here keeps: <b>read a real file,
/// write it back, require the bytes to match</b>. For a span index that is a
/// stronger claim than it sounds — the type never rebuilds a file from a model
/// of itself, so this is really asserting that the ranges land where they claim
/// to, across every indentation, quoting and nesting the shipped data uses.
/// </para>
/// <para>
/// It also asserts <em>coverage</em> rather than mere agreement, in two ways: a
/// root holding no files fails rather than passing vacuously, and every file
/// must yield a declaration. A reader that refused half the corpus and rewrote
/// the rest perfectly would otherwise look identical to one that worked.
/// </para>
/// <para>
/// The counts it reproduces, from the census over all 3,038 shipped items
/// (Roadmap §10.89): 26 declared classes, of which the costume ones name their
/// slot; and the most common fields are <c>myUIName</c>, <c>myUIDescription</c>,
/// <c>myIcon</c> and <c>myModel</c>.
/// </para>
/// </remarks>
public sealed class JuiceCorpusTests(ITestOutputHelper output)
{
    private const string ItemsVariable = "PERIANTH_ITEMS";

    private const string NpcVariable = "PERIANTH_NPCS";

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Every_item_indexes_and_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(ItemsVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {ItemsVariable} to a directory of extracted .mitem files");
            return;
        }

        List<string> failures = [];
        Dictionary<string, int> classes = [];
        Dictionary<string, int> fields = [];
        int files = 0;
        int identical = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mitem", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (source.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<JuiceDocument> result = JuiceDocument.Read(source.Value);
            if (result.IsRefused)
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Refusal.Message}");
                continue;
            }

            files++;
            JuiceDocument document = result.Value;

            classes[document.DeclaredClass] = classes.GetValueOrDefault(document.DeclaredClass) + 1;
            foreach (JuiceField field in document.Fields)
            {
                fields[field.Name] = fields.GetValueOrDefault(field.Name) + 1;
            }

            if (document.Bytes.Span.SequenceEqual(source.Value.Bytes))
            {
                identical++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(path)}: rewrote to different bytes");
            }

            // Byte identity alone cannot see a field that was never indexed —
            // the file still rewrites perfectly. So the fields are counted
            // independently, by the one thing these files agree on: a
            // declaration's own fields sit at exactly one tab. A brace inside a
            // quoted value once stopped the index after the first field, and
            // only this catches that.
            int expected = 0;
            foreach (string line in System.IO.File.ReadAllLines(path))
            {
                if (line.StartsWith("\tmy", StringComparison.Ordinal))
                {
                    expected++;
                }
            }

            if (expected != document.Fields.Length)
            {
                failures.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(path)}: indexed {document.Fields.Length} fields, file has {expected}"));
            }

            // The uid is what a vendor, recipe or loot table names, so a range
            // that drifted by a byte would produce an authoring tool that writes
            // references to nothing.
            string uid = Encoding.UTF8.GetString(
                document.Bytes.Span.Slice(document.UidRange.Offset, document.UidRange.Length));
            if (!JuiceDocument.IsUid(uid))
            {
                failures.Add($"{Path.GetFileName(path)}: uid range reads '{uid}'");
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} items, {identical} byte-identical, {classes.Count} declared classes"));
        foreach ((string name, int count) in classes.OrderByDescending(p => p.Value).Take(8))
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count,5} {name}"));
        }

        foreach ((string name, int count) in fields.OrderByDescending(p => p.Value).Take(8))
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  field {name}: {count}"));
        }

        Assert.True(files > 0, $"{ItemsVariable} named a root holding no .mitem files");
        Assert.Empty(failures);
        Assert.Equal(files, identical);
    }

    /// <summary>
    /// The same oracle over character definitions, which use the same language
    /// and one shape items never do.
    /// </summary>
    /// <remarks>
    /// Worth its own run rather than folding into the items root, because an
    /// <c>.mnpc</c> exercises <b>inheritance</b> — <c>Class Name &lt; uid=… &gt;
    /// : Parent</c> — which no item declares. A reader that mishandled the
    /// clause would rewrite every item perfectly and lose the parent on 652
    /// declarations of 1,827.
    /// </remarks>
    [Fact]
    public void Every_character_definition_writes_back_byte_for_byte()
    {
        string root = Environment.GetEnvironmentVariable(NpcVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {NpcVariable} to a directory of extracted .mnpc files");
            return;
        }

        List<string> failures = [];
        Dictionary<string, int> classes = [];
        int files = 0;
        int identical = 0;
        int inherited = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mnpc", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            Result<SourceFile> source = SourceFileReader.Read(path);
            if (!source.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: unreadable: {source.Refusal.Message}");
                continue;
            }

            Result<JuiceDocument> result = JuiceDocument.Read(source.Value);
            if (!result.IsSuccess)
            {
                failures.Add($"{Path.GetFileName(path)}: {result.Refusal.Message}");
                continue;
            }

            files++;
            JuiceDocument document = result.Value;
            classes[document.DeclaredClass] = classes.GetValueOrDefault(document.DeclaredClass) + 1;

            string text = Encoding.Latin1.GetString(source.Value.Bytes);
            if (text.Contains("> : ", StringComparison.Ordinal))
            {
                inherited++;
            }

            if (document.Bytes.Span.SequenceEqual(source.Value.Bytes))
            {
                identical++;
            }
            else
            {
                failures.Add($"{Path.GetFileName(path)}: rewrote to different bytes");
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{files} definitions, {identical} byte-identical, {classes.Count} classes, {inherited} with a parent"));

        foreach ((string name, int count) in classes.OrderByDescending(p => p.Value))
        {
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {count,5} {name}"));
        }

        Assert.True(files > 0, $"{NpcVariable} named a root holding no .mnpc files");
        Assert.Empty(failures);
        Assert.Equal(files, identical);

        // The shape items cannot supply. A root without one proves less than it
        // looks, so this fails rather than passing vacuously.
        Assert.True(inherited > 0, "no definition in that root derives from another");
    }
}
