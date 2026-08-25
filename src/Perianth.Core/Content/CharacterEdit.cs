using System;
using System.Globalization;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;

namespace Perianth.Core.Content;

/// <summary>
/// A new character definition derived from one the game ships.
/// </summary>
/// <param name="Npc">The new <c>.mnpc</c>, ready to write.</param>
/// <param name="Uid">Its uid.</param>
/// <param name="GraphObject">The graph object it was pointed at.</param>
/// <param name="NameGuid">The guid its shown name resolves through, when one was set.</param>
/// <param name="DisplayName">The text that guid should resolve to.</param>
/// <param name="Inherits">The declaration it derives from, where the template had one.</param>
public sealed record CharacterDerivation(
    ReadOnlyMemory<byte> Npc,
    string Uid,
    string GraphObject,
    string? NameGuid,
    string? DisplayName,
    string? Inherits);

/// <summary>
/// Makes a new character by copying an <c>.mnpc</c> and pointing it at a graph
/// object of its own.
/// </summary>
/// <remarks>
/// <para>
/// A character is <b>text plus binary</b> (Roadmap §10.87), and this is the text
/// half: an <c>.mnpc</c> naming <c>myGraphObjectFile</c>, <c>myBehavior</c>,
/// <c>myOasisId</c> and the rest. The binary half is the actor graph object,
/// which <see cref="GraphEdit"/> makes by substituting its string table — so
/// between them a new character needs no graph editing at all.
/// </para>
/// <para>
/// The same copy-a-template rule as items and props, for the same reason: the
/// declarations carry up to thirty fields drawn from a schema of 887 classes,
/// and every line this does not name is one it reproduces exactly.
/// </para>
/// <para>
/// <b>An <c>.mnpc</c>'s file name is not its declared name</b>, unlike an item's
/// — 875 of 1,824 differ — so there is no name-to-path rule here and none is
/// invented. <see cref="ProposePath"/> offers the convention the other 949
/// follow and says that is what it is; the caller chooses.
/// </para>
/// <para>
/// <b>Inheritance is preserved, not resolved.</b> 652 of 1,827 declarations
/// derive from another with <c>: Parent</c>, and a copy keeps that clause — so
/// the new character inherits whatever the template did. Flattening it would
/// mean reading the parent, which means reading the schema, which is the thing
/// this project does not do.
/// </para>
/// </remarks>
public static class CharacterEdit
{
    /// <summary>The folder character definitions live in.</summary>
    private const string NpcFolder = "camel/game system data/juice/ai/npc/";

    /// <summary>The field naming the actor graph object.</summary>
    private const string GraphField = "myGraphObjectFile";

    /// <summary>Where a character of a given name would conventionally go.</summary>
    /// <remarks>
    /// <b>A convention, not a lookup</b> — and the difference matters. An item is
    /// found by turning its name into a path, so its file name is forced; a
    /// character is not, and 875 of 1,824 shipped files are named something other
    /// than their declaration. This offers what the other 949 do, and a caller
    /// with a reason to differ should say so.
    /// </remarks>
    public static string ProposePath(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return NpcFolder + name.ToLowerInvariant() + ".mnpc";
    }

    /// <summary>Copies a template character under a new name and graph object.</summary>
    /// <param name="template">A shipped <c>.mnpc</c> of the kind wanted.</param>
    /// <param name="name">The new declaration's name.</param>
    /// <param name="graphObject">The archive path of its actor graph object.</param>
    /// <param name="displayName">The name to show, or null to keep the template's.</param>
    public static Result<CharacterDerivation> Derive(
        SourceFile template, string name, string graphObject, string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(graphObject);

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A new character needs a name.");
        }

        if (!graphObject.EndsWith(".mgraphobject", StringComparison.OrdinalIgnoreCase))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A character is drawn through a .mgraphobject and '{graphObject}' is not one. The graph object names the model; the .mmb is named by it, not here."));
        }

        Result<JuiceDocument> read = JuiceDocument.Read(template);
        if (!read.TryGetValue(out JuiceDocument? document, out Refusal? refusal))
        {
            return refusal;
        }

        // Refused rather than added. 181 of 1,824 shipped declarations carry no
        // graph object — they are tuning data, or derive it from a parent — and
        // copying one to make a character that draws would be a mistake this can
        // see and the author cannot, once the file is written.
        if (!document.TryGetField(GraphField, out JuiceField field) || field.IsBlock)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{document.DeclaredClass} {document.DeclaredName}' carries no {GraphField}, so it is not a template for a character that draws."));
        }

        string uid = ItemEdit.MintUid($"npc {name}");
        Result<JuiceDocument> renamed = document.WithDeclaration(name, uid);
        if (!renamed.TryGetValue(out JuiceDocument? under, out Refusal? bad))
        {
            return bad;
        }

        Result<JuiceDocument> pointed = under.WithField(GraphField, $"\"{graphObject}\"");
        if (!pointed.TryGetValue(out JuiceDocument? built, out Refusal? failed))
        {
            return failed;
        }

        string? inherits = Parent(document);

        if (displayName is null)
        {
            return Result.Ok(new CharacterDerivation(
                built.Bytes, uid, graphObject, null, null, inherits));
        }

        Result<JuiceDocument> named = ItemEdit.WithUiName(built, name, displayName);
        return named.TryGetValue(out JuiceDocument? shown, out Refusal? unnamed)
            ? Result.Ok(new CharacterDerivation(
                shown.Bytes, uid, graphObject, ItemEdit.MintUid(name + " name"), displayName, inherits))
            : unnamed;
    }

    /// <summary>
    /// The declaration this one derives from, where its header names one.
    /// </summary>
    /// <remarks>
    /// Reported rather than acted on. A copy keeps the clause, so the new
    /// character inherits whatever the template did — including a graph object
    /// or a behaviour the copy never mentions, which is invisible in the file
    /// and worth saying.
    /// </remarks>
    private static string? Parent(JuiceDocument document)
    {
        string text = System.Text.Encoding.Latin1.GetString(document.Bytes.Span);
        int at = document.UidRange.Offset + document.UidRange.Length;
        int line = text.IndexOf('\n', at);
        if (line < 0)
        {
            return null;
        }

        int colon = text.IndexOf(':', at);
        if (colon < 0 || colon > line)
        {
            return null;
        }

        string parent = text[(colon + 1)..line].Trim();
        return parent.Length == 0 ? null : parent;
    }
}
