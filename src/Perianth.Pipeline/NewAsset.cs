using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using Perianth.Core;
using Perianth.Core.Content;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Juice;
using Perianth.Formats.Mmb;
using Perianth.Gltf;

namespace Perianth.Pipeline;

/// <summary>What making something new produced.</summary>
/// <param name="Files">Everything the mod must carry, at the game's own paths.</param>
/// <param name="Notes">What the author should know that the files cannot show.</param>
/// <param name="Summary">One line saying what happened.</param>
public sealed record NewAssetOutcome(
    ImmutableArray<ModFile> Files, ImmutableArray<Diagnostic> Notes, string Summary);

/// <summary>Which of the game's three ways of handing something over.</summary>
/// <remarks>
/// Crafting is the fourth and is not here, because it is not the same shape:
/// a recipe is a whole declaration rather than an entry in one, and it names a
/// second item — the recipe the player holds — which needs a route of its own.
/// <c>ItemEdit.Craft</c> does it; a caller offering it must ask for that second
/// item too.
/// </remarks>
public enum ObtainKind
{
    /// <summary>In the player's inventory when a new game starts.</summary>
    Inventory,

    /// <summary>On sale, from a named shop, from a named point in the story.</summary>
    Shop,

    /// <summary>Dropped, from a named loot table.</summary>
    Loot,
}

/// <summary>How the player comes by a new costume piece.</summary>
/// <param name="File">The economy file to edit, as an archive path.</param>
/// <param name="Bytes">Its contents.</param>
/// <param name="Declaration">The shop, setting or table to add it to, by name.</param>
/// <param name="Kind">Which of the three.</param>
/// <param name="GameState">For a shop, the story state it appears from.</param>
/// <param name="Chance">For loot, how likely it is, above 0 and up to 1.</param>
/// <param name="Least">For loot, the fewest dropped.</param>
/// <param name="Most">For loot, the most.</param>
public sealed record ObtainRoute(
    string File,
    byte[] Bytes,
    string Declaration,
    ObtainKind Kind,
    string GameState = "",
    double Chance = 1.0,
    int Least = 1,
    int Most = 1);

/// <summary>
/// Turns a mesh made elsewhere into something the game has.
/// </summary>
/// <remarks>
/// <para>
/// The sequence behind the window's <em>New</em> tab, and the one caller that
/// legitimately spans both halves of the project: it needs <c>Core</c> to edit
/// the game's files and <c>Gltf</c> to read the author's, which is exactly why
/// <c>Pipeline</c> exists.
/// </para>
/// <para>
/// <b>One choice resolves the files.</b> An author picks the thing their new
/// thing is like — a hat, a prop standing somewhere, a character — and the chain
/// from that to a model is followed here rather than typed: an item names its
/// model, a prop entity names a graph object which names a model, and a
/// character's definition names a graph object which names a model. Finding
/// those files is the friction the window exists to remove.
/// </para>
/// <para>
/// <b>Nothing is made from nothing.</b> Geometry is applied over a shipped
/// model, and the declarations are copies of shipped ones with a few fields
/// changed. That is the same rule every operation under this one keeps, and it
/// is what makes a five-field pane possible against a schema of 887 classes.
/// </para>
/// </remarks>
public static class NewAsset
{
    /// <summary>Where a new model and its companions go.</summary>
    /// <remarks>
    /// A folder of its own, so nothing here can be mistaken for the game's own
    /// art and no shipped file is at risk of being overwritten by a name
    /// collision.
    /// </remarks>
    public static string ModelPath(string name) =>
        "camel/baked/assets/perianth/" + Stem(name) + ".mmb";

    /// <summary>Where a new graph object goes.</summary>
    public static string GraphPath(string name, bool actor) =>
        (actor ? "camel/graph objects/actor/" : "camel/graph objects/prop/") + Stem(name) + ".mgraphobject";

    /// <summary>Makes a costume piece: a model, an item, and how it is come by.</summary>
    public static Result<NewAssetOutcome> CostumePiece(
        ContentSources content,
        byte[] glb,
        string name,
        string? displayName,
        string itemTemplate,
        ObtainRoute? route,
        bool ownUv0 = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(itemTemplate);

        Result<SourceFile> template = Fetch(content, itemTemplate);
        if (!template.TryGetValue(out SourceFile? item, out Refusal? unread))
        {
            return unread;
        }

        Result<string> named = ModelOf(item);
        if (!named.TryGetValue(out string? templateModel, out Refusal? unnamed))
        {
            return unnamed;
        }

        Result<Built> made = Geometry(content, glb, name, templateModel, ownUv0);
        if (!made.TryGetValue(out Built? built, out Refusal? unbuilt))
        {
            return unbuilt;
        }

        // The declared name is the stem, not what the author typed: an item's
        // file name is how the game finds it (Roadmap §10.95), so the two must
        // agree. Whatever they typed becomes the name it is shown under.
        Result<ItemDerivation> derived = ItemEdit.Derive(
            item, Stem(name), ModelPath(name), displayName ?? name);
        if (!derived.TryGetValue(out ItemDerivation? made2, out Refusal? underived))
        {
            return underived;
        }

        List<ModFile> files = [.. built.Files, new ModFile(ItemEdit.ProposePath(Stem(name)), made2.Item)];
        List<Diagnostic> notes = [.. built.Notes];

        if (made2.ExtraPieces > 0)
        {
            notes.Add(Note(string.Create(
                CultureInfo.InvariantCulture,
                $"That template has {made2.ExtraPieces} variants, which are what actually show. Pick one of those instead.")));
        }

        if (route is null)
        {
            notes.Add(Note(
                "Nothing gives this to the player yet, so it will exist in the game but never appear. "
                + "Pick a way to get it, or add one later with 'perianth item'."));
        }
        else
        {
            Result<ModFile> obtained = Obtain(route, made2.Uid);
            if (!obtained.TryGetValue(out ModFile? entry, out Refusal? unobtained))
            {
                return unobtained;
            }

            files.Add(entry);
        }

        notes.Add(UnlistedItem);

        return Result.Ok(new NewAssetOutcome(
            [.. files],
            [.. notes],
            string.Create(CultureInfo.InvariantCulture, $"{built.Summary}; item {made2.Uid}")));
    }

    /// <summary>Makes a prop and stands it somewhere on the map.</summary>
    public static Result<NewAssetOutcome> Prop(
        ContentSources content,
        byte[] glb,
        string name,
        string layerPath,
        byte[] layerBytes,
        string entity,
        PropPosition position,
        bool ownUv0 = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(layerPath);
        ArgumentNullException.ThrowIfNull(layerBytes);
        ArgumentNullException.ThrowIfNull(entity);

        SourceFile layer = SourceFile.FromMemory(layerPath, layerBytes);
        Result<ImmutableArray<LayerEntity>> held = PropPlace.List(layer);
        if (!held.TryGetValue(out ImmutableArray<LayerEntity> entities, out Refusal? unlisted))
        {
            return unlisted;
        }

        LayerEntity? chosen = entities.FirstOrDefault(e => e.Name.Equals(entity, StringComparison.Ordinal));
        if (chosen?.Resource is not string graphTemplate)
        {
            return Refusal.Unsupported($"'{entity}' draws nothing, so there is nothing to copy.");
        }

        Result<Rebuilt> rebuilt = FromGraph(content, glb, name, graphTemplate, actor: false, ownUv0);
        if (!rebuilt.TryGetValue(out Rebuilt? parts, out Refusal? unrebuilt))
        {
            return unrebuilt;
        }

        Result<PropPlacement> placed = PropPlace.Beside(
            layer, entity, Stem(name), GraphPath(name, actor: false), position);
        if (!placed.TryGetValue(out PropPlacement? placement, out Refusal? unplaced))
        {
            return unplaced;
        }

        List<ModFile> files = [.. parts.Files, new ModFile(layerPath, placement.Layer)];
        List<Diagnostic> notes = [.. parts.Notes, .. placement.Diagnostics];

        return Result.Ok(new NewAssetOutcome(
            [.. files],
            [.. notes],
            string.Create(CultureInfo.InvariantCulture, $"{parts.Summary}; standing in the map")));
    }

    /// <summary>Makes a character: a model, a graph object and a definition.</summary>
    public static Result<NewAssetOutcome> Character(
        ContentSources content,
        byte[] glb,
        string name,
        string? displayName,
        string npcTemplate,
        bool ownUv0 = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(glb);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(npcTemplate);

        Result<SourceFile> fetched = Fetch(content, npcTemplate);
        if (!fetched.TryGetValue(out SourceFile? npc, out Refusal? unread))
        {
            return unread;
        }

        Result<JuiceDocument> read = JuiceDocument.Read(npc);
        if (!read.TryGetValue(out JuiceDocument? definition, out Refusal? unparsed))
        {
            return unparsed;
        }

        if (!definition.TryGetField("myGraphObjectFile", out JuiceField field) || field.IsBlock)
        {
            return Refusal.Unsupported($"'{definition.DeclaredName}' draws nothing, so there is nothing to copy.");
        }

        string graphTemplate = Unquoted(Text(definition, field));

        Result<Rebuilt> rebuilt = FromGraph(content, glb, name, graphTemplate, actor: true, ownUv0);
        if (!rebuilt.TryGetValue(out Rebuilt? parts, out Refusal? unrebuilt))
        {
            return unrebuilt;
        }

        Result<CharacterDerivation> derived = CharacterEdit.Derive(
            npc, Stem(name), GraphPath(name, actor: true), displayName);
        if (!derived.TryGetValue(out CharacterDerivation? character, out Refusal? underived))
        {
            return underived;
        }

        List<ModFile> files =
        [
            .. parts.Files,
            new ModFile(CharacterEdit.ProposePath(Stem(name)), character.Npc),
        ];

        List<Diagnostic> notes = [.. parts.Notes];

        if (character.Inherits is string parent)
        {
            notes.Add(Note($"Also inherits from '{parent}'."));
        }

        notes.Add(UnlistedCharacter);

        return Result.Ok(new NewAssetOutcome(
            [.. files],
            [.. notes],
            string.Create(CultureInfo.InvariantCulture, $"{parts.Summary}; character {character.Uid}")));
    }

    /// <summary>The model, cameldata and editordata a new thing draws with.</summary>
    private sealed record Built(
        ImmutableArray<ModFile> Files, ImmutableArray<Diagnostic> Notes, string Summary);

    /// <summary>Those, plus the graph object that names them.</summary>
    private sealed record Rebuilt(
        ImmutableArray<ModFile> Files, ImmutableArray<Diagnostic> Notes, string Summary);

    /// <summary>
    /// Applies the author's mesh over a shipped model and writes the three files
    /// that go together.
    /// </summary>
    /// <remarks>
    /// The companions travel because they are found by name beside the model: a
    /// <c>.mmb</c> at a new path with no <c>.cameldata</c> has no vertex
    /// positions at all, and with no <c>.editordata</c> has no materials. The
    /// editordata is copied unchanged, so the new thing is painted like the one
    /// it was based on until somebody repaints it.
    /// </remarks>
    private static Result<Built> Geometry(
        ContentSources content, byte[] glb, string name, string templateModel, bool ownUv0)
    {
        string @base = templateModel[..^".mmb".Length];

        Result<SourceFile> model = Fetch(content, templateModel);
        if (!model.TryGetValue(out SourceFile? modelFile, out Refusal? noModel))
        {
            return noModel;
        }

        Result<MmbModel> parsed = MmbReader.Read(modelFile);
        if (!parsed.TryGetValue(out MmbModel? mmb, out Refusal? unparsed))
        {
            return unparsed;
        }

        Result<SourceFile> companion = Fetch(content, @base + ".cameldata");
        if (!companion.TryGetValue(out SourceFile? cameldataFile, out Refusal? noCameldata))
        {
            return noCameldata;
        }

        Result<CameldataFile> cameldata = CameldataReader.Read(cameldataFile);
        if (!cameldata.TryGetValue(out CameldataFile? camel, out Refusal? unreadable))
        {
            return unreadable;
        }

        Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb);
        if (!meshes.TryGetValue(out ImmutableArray<GlbMesh> read, out Refusal? noMeshes))
        {
            return noMeshes;
        }

        Result<GeometryImportResult> applied = GeometryImport.Apply(
            modelFile,
            mmb,
            camel,
            [.. read.Select(m => new EditedPart(m.Name, m.Positions, m.PoolSlots, m.Indices, m.Uv0))],
            ownUv0);
        if (!applied.TryGetValue(out GeometryImportResult? edit, out Refusal? unapplied))
        {
            return unapplied;
        }

        Result<byte[]> written = CameldataWriter.Write(edit.Cameldata);
        if (!written.TryGetValue(out byte[]? cameldataBytes, out Refusal? unwritten))
        {
            return unwritten;
        }

        string newBase = ModelPath(name)[..^".mmb".Length];
        List<ModFile> files =
        [
            new ModFile(ModelPath(name), edit.Model),
            new ModFile(newBase + ".cameldata", cameldataBytes),
        ];

        List<Diagnostic> notes = [];

        Result<SourceFile> materials = Fetch(content, @base + ".editordata");
        if (materials.TryGetValue(out SourceFile? editordata, out Refusal? _))
        {
            files.Add(new ModFile(newBase + ".editordata", editordata.Bytes.ToArray()));
        }
        else
        {
            notes.Add(Note("The model it is based on has no materials file, so this will draw untextured."));
        }

        if (!edit.Moved)
        {
            notes.Add(Note("Your mesh matches the original exactly, so nothing about its shape changed."));
        }

        if (edit.Converted > 0)
        {
            notes.Add(Note(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Converted} part(s) now use the texture layout from your file.")));
        }

        // Nothing in the mod folder shows whether a part was painted as its
        // author laid it out or by a projection, so it is said here or nowhere.
        if (edit.LayoutIgnored > edit.LayoutUnconvertible)
        {
            notes.Add(Note(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.LayoutIgnored - edit.LayoutUnconvertible} part(s) work their texture layout out from position, so the one in your file was not used. That is right for a flat shape and wrong for a solid one, where the same image is smeared down every side. Tick \"use my file's texture layout\" to store yours instead.")));
        }

        // The box is already ticked for these, and ticking it again changes
        // nothing: which rule a part uses is written in the payload, and a part
        // that only moved has no payload written.
        if (edit.LayoutUnconvertible > 0)
        {
            notes.Add(Note(string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.LayoutUnconvertible} part(s) kept their arrangement, so they were moved rather than redrawn and could not be switched to your layout. Change their triangles as well as their points to redraw one.")));
        }

        return Result.Ok(new Built(
            [.. files],
            [.. notes],
            string.Create(
                CultureInfo.InvariantCulture,
                $"{edit.Reshaped + edit.Rebuilt + edit.Added} parts")));
    }

    /// <summary>
    /// The same, for the two kinds whose model is named by a graph object rather
    /// than directly.
    /// </summary>
    private static Result<Rebuilt> FromGraph(
        ContentSources content,
        byte[] glb,
        string name,
        string graphTemplate,
        bool actor,
        bool ownUv0)
    {
        Result<SourceFile> fetched = Fetch(content, graphTemplate);
        if (!fetched.TryGetValue(out SourceFile? graph, out Refusal? unread))
        {
            return unread;
        }

        Result<string> sole = GraphEdit.Sole(graph, ".mmb");
        if (!sole.TryGetValue(out string? templateModel, out Refusal? ambiguous))
        {
            return ambiguous;
        }

        Result<Built> built = Geometry(content, glb, name, templateModel, ownUv0);
        if (!built.TryGetValue(out Built? parts, out Refusal? unbuilt))
        {
            return unbuilt;
        }

        Result<GraphEdited> repointed = GraphEdit.Repoint(graph, [(templateModel, ModelPath(name))]);
        if (!repointed.TryGetValue(out GraphEdited? edited, out Refusal? unrepointed))
        {
            return unrepointed;
        }

        return Result.Ok(new Rebuilt(
            [.. parts.Files, new ModFile(GraphPath(name, actor), edited.Bytes)],
            parts.Notes,
            parts.Summary));
    }

    private static Result<ModFile> Obtain(ObtainRoute route, string uid)
    {
        SourceFile file = SourceFile.FromMemory(route.File, route.Bytes);

        Result<ReadOnlyMemory<byte>> edited = route.Kind switch
        {
            ObtainKind.Shop => ItemEdit.Stock(file, route.Declaration, uid, route.GameState),
            ObtainKind.Loot => ItemEdit.Drop(
                file, route.Declaration, uid, route.Chance, route.Least, route.Most),
            _ => ItemEdit.Grant(file, route.Declaration, uid),
        };

        return edited.TryGetValue(out ReadOnlyMemory<byte> bytes, out Refusal? refusal)
            ? Result.Ok(new ModFile(route.File, bytes))
            : refusal;
    }

    /// <summary>
    /// The one thing no amount of correct authoring settles, said every time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be one sentence for all three kinds -- "nobody has confirmed
    /// the game loads files it never shipped with" -- and that is wrong in both
    /// directions. The model, cameldata and editordata each go to a path of
    /// their own, and <b>a reference by path resolves to a new path</b>: proven
    /// in game, by the probe model trio and the probe texture (Roadmap §10.110).
    /// Most of what this writes is on the proven side, and saying otherwise
    /// talks an author out of work that would have worked.
    /// </para>
    /// <para>
    /// What is unproven is narrower and is always <em>the declaration</em>. The
    /// game builds its item registry by listing the items folder for
    /// <c>*.mitem</c>, and the loader hooks the file <em>open</em> rather than
    /// the listing (§10.132), so a file that exists only to be discovered is the
    /// open case. <c>.mnpc</c> is a registry type for the same reason.
    /// Replacing a shipped item is confirmed to work (§10.165), which is why
    /// each of these names that route rather than leaving the author with
    /// nothing to do instead. None of that mechanism is in the wording: an
    /// author needs the outcome, and why the game does not find a new item is
    /// this file's business rather than theirs.
    /// </para>
    /// <para>
    /// There is no prop version. A graph object is named by path and so is on
    /// the proven side, and what is unproven about placing one is the map
    /// entity -- which <c>PropPlace</c> already says, in stronger terms, having
    /// twice been installed and honoured by nothing. Two notes over one fact is
    /// how the sharper of them gets skimmed past.
    /// </para>
    /// </remarks>
    private static Diagnostic UnlistedItem => Note(
        "Adding a new item to the game's existing list may not yet work. Changing an item the "
        + "game already has works.");

    /// <inheritdoc cref="UnlistedItem"/>
    private static Diagnostic UnlistedCharacter => Note(
        "Adding a new character to the game's existing list may not yet work.");

    private static Diagnostic Note(string message) =>
        new(DiagnosticIds.InputChangedDuringRead, DiagnosticSeverity.Warning, message);

    private static Result<SourceFile> Fetch(ContentSources content, string path)
    {
        Result<byte[]?> read = content.Read(path);
        if (!read.TryGetValue(out byte[]? bytes, out Refusal? refusal))
        {
            return refusal;
        }

        return bytes is null
            ? Refusal.Resource($"'{path}' is not in the archives.", DiagnosticIds.ResourceMissing)
            : Result.Ok(SourceFile.FromMemory(path, bytes));
    }

    /// <summary>The model an item names, out of the one shape all 857 use.</summary>
    private static Result<string> ModelOf(SourceFile item)
    {
        Result<JuiceDocument> read = JuiceDocument.Read(item);
        if (!read.TryGetValue(out JuiceDocument? document, out Refusal? refusal))
        {
            return refusal;
        }

        if (!document.TryGetField("myModel", out JuiceField field) || field.IsBlock)
        {
            return Refusal.Unsupported($"'{document.DeclaredName}' wears nothing, so there is nothing to copy.");
        }

        string value = Text(document, field);
        const string Anchor = "MMAFile = \\\"";
        int at = value.IndexOf(Anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            return Refusal.Unsupported($"'{document.DeclaredName}' names its model in a way this does not recognise.");
        }

        int start = at + Anchor.Length;
        int end = value.IndexOf('\\', start);
        return end < 0
            ? Refusal.Malformed($"'{document.DeclaredName}' has an unterminated model path.")
            : Result.Ok(value[start..end]);
    }

    private static string Text(JuiceDocument document, JuiceField field) =>
        System.Text.Encoding.Latin1.GetString(
            document.Bytes.Span.Slice(field.Value.Offset, field.Value.Length));

    private static string Unquoted(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>
    /// A name reduced to what a file and a declaration may be called.
    /// </summary>
    /// <remarks>
    /// Somebody typing "My Cool Hat" means a name to show, not a path, so the
    /// spaces and capitals are taken out here rather than refused at them. The
    /// shown name keeps whatever they typed.
    /// </remarks>
    public static string Stem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        System.Text.StringBuilder built = new(name.Length);
        foreach (char c in name.ToLowerInvariant())
        {
            built.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        return built.ToString().Trim('_');
    }
}
