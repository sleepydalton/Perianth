using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Perianth.Core;
using Perianth.Formats.Diagnostics;
using Perianth.Pipeline;

namespace Perianth.Cli;

internal static class Program
{
    internal const int Success = 0;
    internal const int Refused = 2;

    private const string Usage = """
        perianth - a deterministic asset exporter for South Park: The Fractured But Whole.

        Usage:
          perianth export  --mmb PATH --cameldata PATH --out PATH [options]
          perianth extract --sdf-root DIR --path VIRTUAL --out DIR [options]
          perianth texture --from PNG --original DDS --out DIR --name TEXT [options]
          perianth material --editordata FILE --repoint OLD=NEW --out DIR --name TEXT
          perianth geometry --mmb PATH --cameldata PATH --from GLB --out DIR --name TEXT
          perianth item    --template MITEM --name TEXT --model VIRTUAL [route] --out DIR
          perianth character --graph-template MGRAPHOBJECT --list
          perianth character --graph-template MGRAPHOBJECT --npc-template MNPC
                           --name TEXT --model VIRTUAL --out DIR
          perianth prop    --layer MLAYER --list
          perianth prop    --layer MLAYER --template NAME --name TEXT
                           --graph-object VIRTUAL --at X,Y,Z --out DIR
          perianth patch   --make --edited FILE --original FILE --out DIR
          perianth patch   --make --new --edited FILE --replaces VIRTUAL --out DIR
          perianth patch   --apply --patch FILE --original FILE --out DIR --name TEXT
          perianth costume --sdf-root DIR --list [--slot NAME]
          perianth costume --sdf-root DIR --wear NAME [--wear NAME ...]

        Export options:
          --mmb PATH         model geometry
          --cameldata PATH   the geometry's companion pools and constants
          --out PATH         the GLB to write
          --clip-anim PATH   an animation to play against the setup. Repeatable,
                             and needs --animate. Several play in order down one
                             timeline, so pressing play shows all of them, and
                             naming the same one twice repeats it
          --separate-animations  keep each as its own animation instead, which is
                             the right shape to build with and the harder one to
                             look at: a viewer stashes them as separate tracks
          --source-space     omit the source-to-glTF presentation root
          --allow-unposed    export the complete part list even with a setup beside the inputs
          --allow-missing-parts  pose with a hierarchy that does not account for
                             the whole model, omitting and naming the parts it
                             cannot place. For a model with no setup of its own,
                             where a relative's hierarchy is the only one there is
          --gap-anim PATH    a second hierarchy, consulted only for the parts
                             --setup-anim cannot name, so a body posed by one
                             relative can take its head from another. A still
                             only: borrowed parts are placed rather than
                             attached, so they cannot follow an animation
          --with PATH        another model drawn into the same file, posed by the
                             same --setup-anim. Repeatable: this is how a
                             character is exported wearing its equipment. Its
                             .cameldata sits beside it under the same name, and
                             its .editordata too when materials are on. A still
                             only, for now: each model brings its own copy of
                             the hierarchy, so an animation would move one and
                             leave the rest standing
          --keep-empty-nodes keep hierarchy nodes that draw nothing and animate
                             nothing. They are left out by default: a rig carries
                             a joint for every part the game might show, so one
                             character is 3,865 nodes to draw 37 meshes, and a
                             dressed animated one ran at half speed in Blender
                             until they were dropped. Nothing drawn changes
          --json             one line of machine-readable result instead of prose

        Extract options:
          --sdf-root DIR     the directory holding sdf.sdftoc and its archives
          --path VIRTUAL     a file inside the archives, or a folder to take whole
          --character PATH   a model's whole asset set: companions, setup, atlases, clips
          --find TEXT        print the paths containing TEXT, and write nothing
          --out DIR          the directory to extract into, mirroring the archive paths
          --list             print what would be written, and write nothing
          --flat             write the files without their folders, names only
          --limit N          refuse a folder holding more than N files (default 2000, 0 for no limit)
          --json             one line of machine-readable result instead of prose

        Texture options:
          --from FILE        an edited image: a PNG (8-bit RGB or RGBA) to convert,
                             or a DDS you already edited, taken verbatim. Repeatable
          --original DDS     the extracted file it replaces, whose archive path is
                             read from the provenance recorded beside it
          --replaces VIRTUAL the archive path outright, for a file not extracted here
          --out DIR          where to write the mod folder
          --name TEXT        the mod's name, which is also its folder
          --author TEXT      who made it (default: unknown)
          --version TEXT     its version (default: 1.0.0)
          --description TEXT one line about it (default: the name)
          --no-mips          write one level only, rather than the halved chain
          --preload-custom-assets  set the loader's wider asset support; leave it
                             off unless the mod needs it, as it may cause crashes
          --json             one line of machine-readable result instead of prose

        A texture is written uncompressed, which the engine loads, so editing one
        needs no block-compression plugin: work from a PNG and this converts it,
        or edit the extracted .dds in any editor that reads one and hand it
        straight back — a DDS is passed through rather than re-encoded. Give one
        --original or --replaces per --from, in the same order. The mod folder
        holds manifest.ini and the game's own paths, ready for FractureLoader.

        Material options:
          --editordata FILE  the extracted material sheet to edit
          --repoint OLD=NEW  bind NEW wherever OLD is bound, repeatable
          --retint TEX=R,G,B recolour the parts painted with TEX, repeatable
          --only-tint R,G,B  narrow every --retint to the parts already that colour
          --assign PATH      bind PATH on the parts named by --section, whatever
                             they carried; the way to paint one part
          --channel NAME     which channel --assign binds (default DiffuseColor)
          --section N        which parts to change, repeatable. Also restricts
                             --repoint
          --replaces VIRTUAL the archive path outright, for a file not extracted here
          --out DIR          where to write the mod folder
          --name TEXT        the mod's name, which is also its folder
          --author/--version/--description/--preload-custom-assets  as for texture
          --sdf-root DIR     the game's archives, to tell a texture it already
                             ships from one your mod has to supply
          --verify DIR       check a finished mod folder instead of editing:
                             refuses if it names a texture nothing provides
          --dry-run          say what would change, and write nothing
          --json             one line of machine-readable result instead of prose

        A model's parts are painted two ways, and which edit does anything
        depends on the part. A part bound to a scanned sheet of coloured paper
        carries its colour in the image, so --repoint changes it. A part bound
        to tex_white16_d.dds is a blank sheet coloured entirely by its tint, so
        --retint changes it and repointing would swap one blank sheet for
        another. Colours are three decimals, because eight bits per channel
        cannot express the values the shipped files hold.

        One texture is usually bound by dozens or hundreds of parts, so
        --dry-run first: "210 sections" when you expected one is the difference
        between the mod you meant and a model repainted throughout.

        Repointing names a texture by a path typed once here and again when the
        texture is added, and a path differing by a character binds a file that
        does not exist — which the game reports by drawing the wrong thing. So
        writing says which repointed paths nothing provides yet, and
        --verify DIR checks a finished mod folder and refuses if any remain.
        Run it before installing.

        Geometry options:
          --mmb PATH --cameldata PATH   the model to change
          --from GLB         the mesh you edited in Blender
          --out DIR --name TEXT   where to write the mod folder, and its name
          --own-uvs          store the texture layout the mesh brought, on parts
                             that would otherwise work theirs out from position
          --dry-run          say what would change, and write nothing
          --json             one line of machine-readable result instead of prose

        A texture is painted on by one of two rules. 86% of parts work out where
        each bit of image goes from where each point sits — a projection, which
        is exactly right for the flat cut-outs all the game's art is, and wrong
        for anything with sides, where one image is smeared down all of them.
        The other 14% store the answer, and those take the layout your mesh
        brings without being asked.

        --own-uvs switches a part from the first rule to the second. It is off by
        default because switching every part would grow the file and lose the
        free reprojection a later reshape gets for nothing. A part that brought a
        layout and was left working its own out is reported on every run, so the
        choice is never made silently.

        Item options:
          --template MITEM   a shipped item of the slot wanted, to copy
          --name TEXT        the new item's name, which is also its file's
          --model VIRTUAL    the archive path of its .mmb
          --display-name TEXT  what the menu shows, with --locpack
          --locpack FILE     the extracted menus.locpack the name resolves through
          --out DIR --mod-name TEXT   where to write the mod folder, and its name
          --author/--version/--description/--preload-custom-assets  as for texture
          --dry-run          say what would be written, and write nothing
          --json             one line of machine-readable result instead of prose

        And one or more routes by which the player gets it:
          --vendors FILE --shop NAME [--game-state STATE]   sold
          --inventory FILE --setting NAME [--count N]       given outright
          --loot FILE --table NAME [--chance F]
              [--quantity-min N] [--quantity-max N]         found
          --recipes FILE --recipe-template NAME --recipe-name NAME
              --recipe-item UID [--ingredient UID:COUNT ...]  crafted

        The template's declared class is the slot — CostumeItemStreetHairLow and
        its 25 siblings — so choosing what to copy is choosing where it is worn,
        and nothing here sets a slot. Everything the template said that is not
        named above is copied exactly, which is what keeps this small against a
        schema of 887 classes.

        Nothing inside an item says where it comes from: shops, chests and
        recipes name items rather than the other way round. So an item with no
        route is declared and unobtainable, and this says so rather than
        refusing — the file is still worth writing while the route is decided.
        Crafting is the one that does not stand alone: a recipe is itself an
        item the player holds, so make that item too and give it a route.

        Each economy file must be one this tool extracted, because its archive
        path is read from the recorded provenance rather than guessed from where
        it sits. A file written one folder out is a mod the game ignores while
        looking as though it worked.

        Character options:
          --graph-template FILE  an extracted actor .mgraphobject, to copy
          --list             the assets that graph object names
          --npc-template FILE  an extracted .mnpc, to copy
          --name TEXT        the new character's declared name
          --model VIRTUAL    the .mmb it draws
          --anim-system VIRTUAL  the .manimsys it animates through
          --repoint OLD=NEW  move any other entry by name, repeatable
          --display-name TEXT  what the game shows, with --locpack
          --locpack FILE     the extracted menus.locpack the name resolves through
          --content-root DIR a folder extracted from the archives, so the model
                             and the rig can be read and checked against each other
          --graph-path VIRTUAL  where to write the graph object
          --npc-path VIRTUAL   where to write the definition
          --out DIR --mod-name TEXT   where to write the mod folder, and its name
          --author/--version/--description/--preload-custom-assets  as for texture
          --dry-run          say what would be written, and write nothing
          --json             one line of machine-readable result instead of prose

        A character is two files. The graph object is what draws it, and making
        a new one is a string-table substitution: copy a shipped one and change
        the paths, leaving the graph itself alone. The .mnpc is what names that
        graph object and gives the character a behaviour, a faction and a voice.

        --model and --anim-system are conveniences over --repoint that ask the
        template which entry they mean. That works because 1,236 of 1,250 actors
        name exactly one .mmb; the rest refuse and say to name the entry
        outright, rather than picking one.

        Copying keeps the template's `: Parent` clause where it has one, so the
        new character inherits whatever that declared — including fields the copy
        never mentions. It is reported for that reason.

        Prop options:
          --layer MLAYER     an extracted layerdata.mlayer from camel/maps
          --list             what that layer holds, and what each entity draws
          --template NAME    a prop already in it, to copy
          --name TEXT        what to call the copy
          --graph-object VIRTUAL  the .mgraphobject it draws
          --at X,Y,Z         where it stands
          --out DIR --mod-name TEXT   where to write the mod folder, and its name
          --author/--version/--description/--preload-custom-assets  as for texture
          --dry-run          say what would be written, and write nothing
          --json             one line of machine-readable result instead of prose

        A prop is a layer entity carrying a matrix and naming a .mgraphobject,
        which is what names the model — so placing one is a text edit and needs
        no model writing at all. The record is copied rather than built: a prop
        carries 21 fields and the game uses 22 different sets of them, so every
        line not named above comes from the template exactly.

        That means the template decides more than it looks. Its sphereRadius is
        a culling bound describing *its* model, so a larger prop given it will
        vanish while still on screen — and rendering the mod offline will not
        show that, because an offline render does not cull. Its depth group and
        rotation come across too. Run --list first: none of this is visible in
        the layer afterwards.

        The copy is placed in the template's own chunk, so it lands in a part of
        the map the game already loads props from.

        Costume options:
          --sdf-root DIR     the game's archives, where the item list lives
          --content-root DIR a folder extracted from them, instead of the archives
          --list             everything wearable, or one --slot NAME of it
          --wear NAME        an entry to put on, repeatable
          --json             machine-readable instead of prose

        `costume` draws nothing. It says what an outfit would draw and what it
        would leave out, because those decisions are invisible in a GLB: a
        hairstyle that produced no model and one nobody chose look identical in
        the file. A headpiece names which cuts of a hairstyle may be worn under
        it, so the answer to "why is my hair short" is which cut survived, and
        the answer to "where did it go" is that none did.

        An extraction records where every file came from, so that a modified file
        can later be compared against its original, and its layout is the one the
        loose-file mod loader reads and --content-root resolves against. --flat
        gives that up in exchange for short paths, which is what a Windows path
        limit or a single file to edit calls for.

        Materials (--editordata with --content-root/--sdf-root), setup and clip
        animation (--setup-anim/--clip-anim/--animate/--time), facial layers
        (--mouth/eyes/pupils/eyebrows-anim and their states, --pupil-position),
        lip-sync (--lipsync-database/--speech-id) and audio (--wem-root/
        --vgmstream-cli) are supported. Options this build cannot honour are
        rejected rather than ignored.
        """;

    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    /// <summary>
    /// The whole command, with its two output streams passed in so that the exit
    /// codes and the shape of what is printed can be checked without starting a
    /// process.
    /// </summary>
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0)
        {
            output.WriteLine(Usage);
            return Success;
        }

        if (string.Equals(args[0], "extract", StringComparison.Ordinal))
        {
            return ExtractCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "texture", StringComparison.Ordinal))
        {
            return TextureCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "material", StringComparison.Ordinal))
        {
            return MaterialCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "geometry", StringComparison.Ordinal))
        {
            return GeometryCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "item", StringComparison.Ordinal))
        {
            return ItemCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "character", StringComparison.Ordinal))
        {
            return CharacterCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "prop", StringComparison.Ordinal))
        {
            return PropCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "patch", StringComparison.Ordinal))
        {
            return PatchCommand.Run(args[1..], output, error);
        }

        if (string.Equals(args[0], "costume", StringComparison.Ordinal))
        {
            return CostumeCommand.Run(args[1..], output, error);
        }

        if (!string.Equals(args[0], "export", StringComparison.Ordinal))
        {
            return Fail(
                Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"{args[0]} is not a command this build provides.")),
                json: false,
                output: null,
                error);
        }

        string[] rest = args[1..];
        Result<ExportRequest> arguments = ExportArguments.Parse(rest);
        if (!arguments.IsSuccess)
        {
            return Fail(arguments.Refusal, Array.IndexOf(rest, "--json") >= 0, output: null, error, output);
        }

        Result<ExportOutcome> outcome = ExportPipeline.Run(arguments.Value);
        return outcome.IsSuccess
            ? Report(outcome.Value, arguments.Value, output, error)
            : Fail(outcome.Refusal, arguments.Value.Json, arguments.Value.Out, error, output);
    }

    private static int Report(ExportOutcome outcome, ExportRequest arguments, TextWriter output, TextWriter error)
    {
        if (arguments.Json)
        {
            output.WriteLine(Json("exported", outcome, refusal: null, arguments.Out));
            return Success;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Exported {outcome.Counts.Meshes} meshes, {outcome.Counts.Vertices} vertices, {outcome.Counts.Triangles} triangles."));

        // Warnings go to standard error so that standard output stays the result.
        foreach (Diagnostic diagnostic in outcome.Diagnostics)
        {
            error.WriteLine(diagnostic.Message);
        }

        if (outcome.Audio is AudioReport audio)
        {
            string channels = audio.Channels == 1 ? "channel" : "channels";
            string locale = audio.Locale.Length > 0 ? $" ({audio.Locale})" : string.Empty;
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Audio {audio.Output}: {audio.SampleCount} samples, {audio.SampleRate} Hz, {audio.Channels} {channels}, {G9(audio.DurationSeconds)} seconds from {audio.Source}{locale}"));

            if (audio.LipsyncEndSeconds is double end && audio.LipsyncDeltaSeconds is double delta)
            {
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Lip-sync endpoint: {G9(end)} seconds; audio delta: {Signed(delta)} seconds"));
            }
        }

        return Success;
    }

    private static string G9(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    private static string Signed(double value) => (value >= 0 ? "+" : string.Empty) + G9(value);

    /// <summary>
    /// Reports a refusal that carries no export result, for a verb that names
    /// its own noun.
    /// </summary>
    /// <remarks>
    /// A caller that asked for JSON gets the refusal as JSON. Reducing it to
    /// prose there would leave the kind and the identifier only in a sentence,
    /// which is the thing a front end must not have to parse back out.
    /// </remarks>
    internal static int Fail(Refusal refusal, bool json, TextWriter output, TextWriter error, string verb)
    {
        if (json)
        {
            using MemoryStream buffer = new();
            using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("schema_version", "perianth-refusal.v1");
                writer.WriteString("status", "refused");
                writer.WriteString("id", refusal.DiagnosticId);
                writer.WriteString("refusal_kind", Kind(refusal.Kind));
                writer.WriteString("message", refusal.Message);
                writer.WriteEndObject();
            }

            output.WriteLine(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            return Refused;
        }

        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"{verb} refused ({Kind(refusal.Kind)}): {refusal.Message}"));
        return Refused;
    }


    private static int Fail(Refusal refusal, bool json, string? output, TextWriter error, TextWriter? standardOutput = null)
    {
        if (json && standardOutput is not null)
        {
            standardOutput.WriteLine(Json("refused", outcome: null, refusal, output));
            return Refused;
        }

        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture, $"Export refused ({Kind(refusal.Kind)}): {refusal.Message}"));
        return Refused;
    }

    /// <summary>
    /// Writes the section 12.1 result schema as one line.
    /// </summary>
    private static string Json(string status, ExportOutcome? outcome, Refusal? refusal, string? output)
    {
        using MemoryStream buffer = new();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });

        writer.WriteStartObject();
        writer.WriteString("schema_version", "perianth-export-result.v1");
        writer.WriteString("status", status);
        writer.WriteString("output", output ?? string.Empty);
        writer.WriteBoolean("partial_export", outcome?.PartialExport ?? false);

        writer.WriteStartObject("counts");
        writer.WriteNumber("meshes", outcome?.Counts.Meshes ?? 0);
        writer.WriteNumber("vertices", outcome?.Counts.Vertices ?? 0);
        writer.WriteNumber("triangles", outcome?.Counts.Triangles ?? 0);
        writer.WriteEndObject();

        writer.WriteStartArray("diagnostics");
        if (refusal is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("id", refusal.DiagnosticId);
            writer.WriteString("severity", "error");
            writer.WriteString("refusal_kind", Kind(refusal.Kind));
            writer.WriteString("message", refusal.Message);
            writer.WriteEndObject();
        }

        foreach (Diagnostic diagnostic in outcome?.Diagnostics ?? [])
        {
            writer.WriteStartObject();
            writer.WriteString("id", diagnostic.Id);
            writer.WriteString("severity", diagnostic.Severity == DiagnosticSeverity.Error ? "error" : "warning");
            writer.WriteString("message", diagnostic.Message);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        if (outcome?.Audio is AudioReport audio)
        {
            writer.WriteStartObject("audio");
            writer.WriteString("output", audio.Output);
            writer.WriteString("source", audio.Source);
            writer.WriteString("locale", audio.Locale);
            writer.WriteNumber("channels", audio.Channels);
            writer.WriteNumber("sample_rate", audio.SampleRate);
            writer.WriteNumber("sample_count", audio.SampleCount);
            writer.WriteNumber("duration_seconds", audio.DurationSeconds);
            WriteNullableNumber(writer, "lipsync_end_seconds", audio.LipsyncEndSeconds);
            WriteNullableNumber(writer, "lipsync_delta_seconds", audio.LipsyncDeltaSeconds);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNull("audio");
        }

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNullableNumber(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is double number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>The three compatibility strings a caller branches on.</summary>
    private static string Kind(RefusalKind kind) => kind switch
    {
        RefusalKind.Malformed => "malformed",
        RefusalKind.Unsupported => "unsupported",
        _ => "resource",
    };
}
