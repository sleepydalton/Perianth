using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Layer;

namespace Perianth.Core.Content;

/// <summary>Where a prop stands, in the map's own units.</summary>
/// <param name="X">Across.</param>
/// <param name="Y">Up.</param>
/// <param name="Z">Into the scene.</param>
public readonly record struct PropPosition(double X, double Y, double Z);

/// <summary>One entity a layer holds, as its name and where its record sits.</summary>
/// <param name="Name">The <c>name</c> field, which is what a caller copies by.</param>
/// <param name="Type">The <c>type</c> field — <c>Prop</c> and twenty-four others.</param>
/// <param name="Resource">The asset it names, where it names one.</param>
/// <param name="Chunk">Which of the layer's chunks it sits in.</param>
/// <param name="Record">The record's text, brace to brace.</param>
/// <param name="Stands">
/// Where it stands — the last column of its matrix. Carried so that a caller
/// placing a copy can start from where the original is rather than at the
/// map's origin, which is nowhere in particular.
/// </param>
public sealed record LayerEntity(
    string Name, string Type, string? Resource, int Chunk, string Record, PropPosition Stands);

/// <summary>What placing a prop produced.</summary>
/// <param name="Layer">The edited layer, ready to write.</param>
/// <param name="Uid">The new entity's uid.</param>
/// <param name="Template">The entity it was copied from.</param>
/// <param name="Chunk">The chunk it was placed in.</param>
/// <param name="Diagnostics">What the copy carried over that may not fit.</param>
public sealed record PropPlacement(
    ReadOnlyMemory<byte> Layer,
    string Uid,
    string Template,
    int Chunk,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Puts a prop into a map layer by copying one that is already there.
/// </summary>
/// <remarks>
/// <para>
/// The chain a map uses to draw a prop is: a layer entity of
/// <c>type = "Prop"</c> carries a 4x4 matrix and names a
/// <c>.mgraphobject</c>, which names the <c>.mmb</c> (Roadmap §10.97). So
/// placing one is a text edit and needs no binary writer — the BVM writer is
/// for making a <em>new</em> graph object, not for placing an existing one.
/// </para>
/// <para>
/// <b>A record is copied, never built.</b> A prop entity carries 21 fields in
/// its commonest form and the corpus holds 22 distinct field sets; inventing one
/// would mean choosing values for depth grouping, LOD behaviour, selfie
/// visibility and audio positioning that nothing here understands. Copying is
/// the same rule the item and recipe operations keep, and for the same reason:
/// every line this does not name is a line it reproduces exactly.
/// </para>
/// <para>
/// <b>The copy also decides where the prop goes in the file.</b> It is placed in
/// the template's own chunk, so it lands in a quad-tree cell the game already
/// loads props from. That is the conservative choice and the safe one: adding to
/// what is already there asks nothing of the loader that the shipped data does
/// not already ask.
/// </para>
/// <para>
/// <b>Rotation and scale come from the template.</b> Only the position is set,
/// because the matrix's other twelve numbers are a basis and a caller who wants
/// a prop turned should copy one that is turned. Offering both a position and a
/// matrix would be two ways to say one thing.
/// </para>
/// </remarks>
public static class PropPlace
{
    /// <summary>The type a prop entity declares.</summary>
    public const string PropType = "Prop";

    /// <summary>Lists what a layer holds, so a caller can choose what to copy.</summary>
    /// <remarks>
    /// Reported rather than guessed at. A layer holds up to twenty-five kinds of
    /// entity and only some are props, so "copy something from here" needs the
    /// list in front of it — the same reason the costume verb prints a decision
    /// that would otherwise be invisible.
    /// </remarks>
    public static Result<ImmutableArray<LayerEntity>> List(SourceFile layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        Result<LayerDocument> read = LayerDocument.Read(layer);
        return read.TryGetValue(out LayerDocument? document, out Refusal? refusal)
            ? Result.Ok(Entities(document))
            : refusal;
    }

    /// <summary>Copies a prop already in the layer and stands the copy somewhere else.</summary>
    /// <param name="layer">A <c>layerdata.mlayer</c>.</param>
    /// <param name="template">The entity to copy, by its <c>name</c>.</param>
    /// <param name="name">What to call the copy.</param>
    /// <param name="graphObject">The archive path of the <c>.mgraphobject</c> it draws.</param>
    /// <param name="position">Where it stands.</param>
    public static Result<PropPlacement> Beside(
        SourceFile layer, string template, string name, string graphObject, PropPosition position)
    {
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(graphObject);

        if (name.Length == 0)
        {
            return Refusal.Unsupported("A placed prop needs a name.");
        }

        if (!graphObject.EndsWith(".mgraphobject", StringComparison.OrdinalIgnoreCase))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"A prop draws a .mgraphobject and '{graphObject}' is not one. The graph object is what names the model; the .mmb is named by it, not here."));
        }

        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(position.Z))
        {
            return Refusal.Unsupported("A position must be three finite numbers.");
        }

        Result<LayerDocument> read = LayerDocument.Read(layer);
        if (!read.TryGetValue(out LayerDocument? document, out Refusal? refusal))
        {
            return refusal;
        }

        LayerEntity? found = null;
        foreach (LayerEntity entity in Entities(document))
        {
            if (entity.Name.Equals(template, StringComparison.Ordinal))
            {
                found = entity;
                break;
            }
        }

        if (found is null)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer holds no entity named '{template}' to copy."));
        }

        // Refused rather than copied anyway. A waypoint or a spawn point has
        // neither a graph object nor a shape, so the copy would be a prop in
        // name only and would draw nothing — which looks in the file exactly
        // like one that works.
        if (!found.Type.Equals(PropType, StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"'{template}' is a {found.Type}, not a {PropType}, so copying it would not place a prop."));
        }

        string body = Encoding.Latin1.GetString(document.Bytes.Span);
        if (body.Contains($"name = \"{name}\"", StringComparison.Ordinal))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The layer already holds an entity named '{name}'."));
        }

        // Deterministic, as everywhere else here: the same request must produce
        // the same bytes, or a mod is unreproducible and its patches unstable.
        string uid = ItemEdit.MintUid($"prop {name}");

        Result<string> built = Rewritten(found.Record, name, uid, graphObject, position);
        if (!built.TryGetValue(out string? record, out Refusal? bad))
        {
            return bad;
        }

        Result<LayerDocument> placed = document.WithEntity(found.Chunk, record);
        return placed.TryGetValue(out LayerDocument? edited, out Refusal? failed)
            ? Result.Ok(new PropPlacement(
                edited.Bytes, uid, template, found.Chunk, Carried(found.Record)))
            : failed;
    }

    /// <summary>
    /// Says what the copy brought with it that describes the template's model
    /// rather than the new one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copying is what keeps this small, and the price is that a few of the
    /// template's fields are about <em>its</em> geometry. <c>sphereRadius</c> is
    /// the sharp one: it is a culling bound, so a prop given a smaller one than
    /// its model needs disappears while it is on screen — and <b>an offline
    /// render cannot show it, because it does not cull</b>. That is the same
    /// failure as the stale MMB bounding box (Roadmap §10.65), reached by a
    /// different route.
    /// </para>
    /// <para>
    /// Reported rather than computed, because the radius is a property of the
    /// model and the model is named by the graph object, which is an archive
    /// path this has no way to resolve. Reported rather than refused, because a
    /// prop of roughly the template's size is the common case and is fine.
    /// </para>
    /// </remarks>
    private static ImmutableArray<Diagnostic> Carried(string record)
    {
        ImmutableArray<Diagnostic>.Builder notes = ImmutableArray.CreateBuilder<Diagnostic>();

        // Said first and on every run, because it is the only thing here that
        // makes the written file worse than useless. Twice now a layer with one
        // entity added has been installed and the whole layer drew nothing --
        // not the added prop, not the eleven that were already there (Roadmap
        // §10.149, §10.165). The file is not the suspect: it is served, its
        // chunks tile, its counts agree and every original record is unchanged
        // byte for byte, so what is missing is something outside its own bytes.
        //
        // A warning rather than a refusal, on ModCheck's argument: the edit is
        // correct as far as anything here can see, and refusing would block the
        // very experiment that would settle it. But an author who ships this
        // without being told would lose a room and have nothing to point at.
        notes.Add(new Diagnostic(
            DiagnosticIds.InputChangedDuringRead,
            DiagnosticSeverity.Warning,
            "A layer written by this tool does not yet draw properly. "
            + "Treat a placed prop as unproven, keep a backup of the map you edit, "
            + "and expect the layer to go missing rather than to gain a prop. "
            + "This is currently being worked on, thank you for your patience."));

        if (Value(record, "sphereRadius") is string radius)
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"sphereRadius {radius} was copied from the template and describes the template's model. It is a culling bound, so a prop larger than that will vanish while it is still on screen — and an offline render will not show it, because it does not cull.")));
        }

        if (Value(record, "DepthGroupChoice") is string depth
            && !depth.Equals("\"Unspecified\"", StringComparison.Ordinal))
        {
            notes.Add(new Diagnostic(
                DiagnosticIds.InputChangedDuringRead,
                DiagnosticSeverity.Warning,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"DepthGroupChoice {depth} was copied from the template, so the new prop draws in the same layer as it.")));
        }

        return notes.ToImmutable();
    }

    /// <summary>
    /// Copies a record, changing four things and leaving every other line alone.
    /// </summary>
    private static Result<string> Rewritten(
        string record, string name, string uid, string graphObject, PropPosition position)
    {
        Result<string> named = Field(record, "name", $"\"{name}\"");
        if (!named.IsSuccess) { return named.Refusal; }

        Result<string> pointed = Field(named.Value, "resource", $"F\"{graphObject}\"");
        if (!pointed.IsSuccess) { return pointed.Refusal; }

        Result<string> identified = Field(pointed.Value, "uid", $"#{uid}");
        if (!identified.IsSuccess) { return identified.Refusal; }

        return Translated(identified.Value, position);
    }

    /// <summary>Replaces one of the record's own fields, never a nested one.</summary>
    /// <remarks>
    /// Anchored at exactly two tabs. A prop's <c>children</c> block holds records
    /// with names of their own, and rewriting one of those would rename somebody
    /// else's entity while leaving this one alone.
    /// </remarks>
    private static Result<string> Field(string record, string field, string value)
    {
        string anchor = $"\n\t\t{field} = ";
        int at = record.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture,
                $"The template record carries no '{field}', so it cannot be copied as a prop."));
        }

        int start = at + anchor.Length;
        int end = record.IndexOf('\n', start);
        if (end < 0)
        {
            return Refusal.Malformed($"The template's '{field}' runs to the end of the record.");
        }

        return Result.Ok(string.Concat(record.AsSpan(0, start), value, ",", record.AsSpan(end)));
    }

    /// <summary>
    /// Moves the record's matrix, keeping its basis.
    /// </summary>
    /// <remarks>
    /// The matrix is four rows of four numbers and the translation is the last
    /// column, so only three of the sixteen change. Rewriting all sixteen would
    /// silently drop a rotation the template carried.
    /// </remarks>
    private static Result<string> Translated(string record, PropPosition position)
    {
        const string Anchor = "\n\t\tmatrix = {\n";
        int at = record.IndexOf(Anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return Refusal.Unsupported("The template record carries no matrix, so it stands nowhere.");
        }

        int start = at + Anchor.Length;
        int end = record.IndexOf("\t\t},", start, StringComparison.Ordinal);
        if (end < 0)
        {
            return Refusal.Malformed("The template's matrix is not closed.");
        }

        string[] rows = record[start..end].Split('\n');
        double[] translation = [position.X, position.Y, position.Z];
        StringBuilder built = new();
        int moved = 0;

        foreach (string row in rows)
        {
            string trimmed = row.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // The fourth row is 0 0 0 1 and carries no translation.
            if (moved >= translation.Length)
            {
                built.Append(row).Append('\n');
                continue;
            }

            string[] numbers = trimmed.TrimEnd(',').Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (numbers.Length != 4)
            {
                return Refusal.Malformed(string.Create(
                    CultureInfo.InvariantCulture,
                    $"A matrix row holds {numbers.Length} numbers rather than four."));
            }

            numbers[3] = translation[moved].ToString("R", CultureInfo.InvariantCulture);
            built.Append("\t\t\t").Append(string.Join(' ', numbers)).Append(",\n");
            moved++;
        }

        return moved == translation.Length
            ? Result.Ok(string.Concat(record.AsSpan(0, start), built.ToString(), record.AsSpan(end)))
            : Refusal.Malformed(string.Create(
                CultureInfo.InvariantCulture,
                $"The template's matrix holds {moved} rows that could carry a position."));
    }

    /// <summary>
    /// Splits each chunk into the records at its own level.
    /// </summary>
    /// <remarks>
    /// A record opens on a line that is exactly one tab and a brace, and closes
    /// on one that is a tab, a brace and a comma. Counting braces would work too
    /// and would have to understand quoting; matching the two lines the format
    /// actually uses is smaller and is what the whole corpus does.
    /// </remarks>
    private static ImmutableArray<LayerEntity> Entities(LayerDocument document)
    {
        ImmutableArray<LayerEntity>.Builder found = ImmutableArray.CreateBuilder<LayerEntity>();
        string text = Encoding.Latin1.GetString(document.Bytes.Span);

        for (int index = 0; index < document.Chunks.Length; index++)
        {
            LayerChunk chunk = document.Chunks[index];
            int from = document.BodyStart + chunk.Offset;
            string body = text.Substring(from, chunk.Size);

            int at = 0;
            while (true)
            {
                int open = body.IndexOf("\n\t{\n", at, StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                int close = body.IndexOf("\n\t},\n", open + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    break;
                }

                string record = body[(open + 1)..(close + "\n\t},\n".Length)];
                at = close + 1;

                string? name = Value(record, "name");
                string? type = Value(record, "type");
                if (name is null || type is null)
                {
                    continue;
                }

                found.Add(new LayerEntity(
                    Unquoted(name), Unquoted(type), Resource(record), index, record, Stands(record)));
            }
        }

        return found.ToImmutable();
    }

    private static string? Value(string record, string field)
    {
        string anchor = $"\n\t\t{field} = ";
        int at = record.IndexOf(anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int start = at + anchor.Length;
        int end = record.IndexOf('\n', start);
        return end < 0 ? null : record[start..end].TrimEnd(',');
    }

    /// <summary>Where a record's matrix puts it, or the origin if it has none.</summary>
    private static PropPosition Stands(string record)
    {
        const string Anchor = "\n\t\tmatrix = {\n";
        int at = record.IndexOf(Anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return default;
        }

        double[] found = new double[3];
        int start = at + Anchor.Length;

        for (int row = 0; row < 3; row++)
        {
            int end = record.IndexOf('\n', start);
            if (end < 0)
            {
                return default;
            }

            string[] numbers = record[start..end]
                .Trim()
                .TrimEnd(',')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (numbers.Length != 4
                || !double.TryParse(numbers[3], NumberStyles.Float, CultureInfo.InvariantCulture, out found[row]))
            {
                return default;
            }

            start = end + 1;
        }

        return new PropPosition(found[0], found[1], found[2]);
    }

    private static string? Resource(string record)
    {
        string? value = Value(record, "resource");
        return value is null || !value.StartsWith("F\"", StringComparison.Ordinal)
            ? null
            : value[2..].TrimEnd('"');
    }

    private static string Unquoted(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
