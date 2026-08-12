using System;
using System.Collections.Generic;
using System.Globalization;
using Perianth.Formats.Diagnostics;
using Perianth.Pipeline;

namespace Perianth.Cli;

/// <summary>
/// Turns a command line into an <see cref="ExportRequest"/>.
/// </summary>
/// <remarks>
/// Grammar only: which token names which field, and whether a value was
/// supplied at all. What the fields may say together is
/// <see cref="ExportRequest.Validate"/>'s, because it is as true of a window as
/// of a command line and only one of the two should own it.
/// </remarks>
public static class ExportArguments
{
    /// <summary>Parses <paramref name="arguments"/>, which exclude the verb.</summary>
    public static Result<ExportRequest> Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        string? mmb = null;
        string? cameldata = null;
        string? output = null;
        string? setupAnim = null;
        List<string> clipAnims = [];
        bool separateAnimations = false;
        bool allowMissingParts = false;
        string? gapAnim = null;
        bool pruneEmptyNodes = true;
        List<WornModel> with = [];
        bool readFromArchives = false;
        string? mouthAnim = null;
        int? mouthState = null;
        string? eyesAnim = null;
        int? eyeState = null;
        string? pupilsAnim = null;
        int? pupilState = null;
        string? eyebrowsAnim = null;
        int? eyebrowState = null;
        string pupilPosition = "authored-state";
        string? lipsyncDatabase = null;
        string? speechId = null;
        string? wemRoot = null;
        string? vgmstreamCli = null;
        List<double> blinkAt = [];
        string? editordata = null;
        string? contentRoot = null;
        string? sdfRoot = null;
        bool sourceSpace = false;
        bool allowUnposed = false;
        bool animate = false;
        double time = 0.0;
        bool json = false;

        for (int i = 0; i < arguments.Count; i++)
        {
            switch (arguments[i])
            {
                case "--mmb":
                    if (!TryTake(arguments, ref i, out mmb))
                    {
                        return Missing("--mmb");
                    }

                    break;

                case "--cameldata":
                    if (!TryTake(arguments, ref i, out cameldata))
                    {
                        return Missing("--cameldata");
                    }

                    break;

                case "--out":
                    if (!TryTake(arguments, ref i, out output))
                    {
                        return Missing("--out");
                    }

                    break;

                case "--setup-anim":
                    if (!TryTake(arguments, ref i, out setupAnim))
                    {
                        return Missing("--setup-anim");
                    }

                    break;

                // Repeatable: several play in order down one timeline, in the
                // order given here, unless --separate-animations keeps them apart.
                case "--clip-anim":
                    if (!TryTake(arguments, ref i, out string? clipAnim))
                    {
                        return Missing("--clip-anim");
                    }

                    clipAnims.Add(clipAnim!);
                    break;

                // The inputs are archive paths, so nothing is written but the GLB.
                case "--from-archives":
                    readFromArchives = true;
                    break;

                case "--separate-animations":
                    separateAnimations = true;
                    break;

                case "--animate":
                    animate = true;
                    break;

                case "--mouth-anim":
                    if (!TryTake(arguments, ref i, out mouthAnim))
                    {
                        return Missing("--mouth-anim");
                    }

                    break;

                case "--mouth-state":
                    if (!TryTakeState(arguments, ref i, "--mouth-state", out mouthState, out Refusal? mouthStateError))
                    {
                        return mouthStateError;
                    }

                    break;

                case "--eyes-anim":
                    if (!TryTake(arguments, ref i, out eyesAnim))
                    {
                        return Missing("--eyes-anim");
                    }

                    break;

                case "--eye-state":
                    if (!TryTakeState(arguments, ref i, "--eye-state", out eyeState, out Refusal? eyeStateError))
                    {
                        return eyeStateError;
                    }

                    break;

                case "--pupils-anim":
                    if (!TryTake(arguments, ref i, out pupilsAnim))
                    {
                        return Missing("--pupils-anim");
                    }

                    break;

                case "--pupil-state":
                    if (!TryTakeState(arguments, ref i, "--pupil-state", out pupilState, out Refusal? pupilStateError))
                    {
                        return pupilStateError;
                    }

                    break;

                case "--eyebrows-anim":
                    if (!TryTake(arguments, ref i, out eyebrowsAnim))
                    {
                        return Missing("--eyebrows-anim");
                    }

                    break;

                case "--eyebrow-state":
                    if (!TryTakeState(arguments, ref i, "--eyebrow-state", out eyebrowState, out Refusal? eyebrowStateError))
                    {
                        return eyebrowStateError;
                    }

                    break;

                case "--pupil-position":
                    if (!TryTake(arguments, ref i, out string? pupilPositionValue))
                    {
                        return Missing("--pupil-position");
                    }

                    pupilPosition = pupilPositionValue!;
                    break;

                case "--lipsync-database":
                    if (!TryTake(arguments, ref i, out lipsyncDatabase))
                    {
                        return Missing("--lipsync-database");
                    }

                    break;

                case "--speech-id":
                    if (!TryTake(arguments, ref i, out speechId))
                    {
                        return Missing("--speech-id");
                    }

                    break;

                case "--wem-root":
                    if (!TryTake(arguments, ref i, out wemRoot))
                    {
                        return Missing("--wem-root");
                    }

                    break;

                case "--vgmstream-cli":
                    if (!TryTake(arguments, ref i, out vgmstreamCli))
                    {
                        return Missing("--vgmstream-cli");
                    }

                    break;

                case "--blink-at":
                    if (!TryTake(arguments, ref i, out string? blinkText))
                    {
                        return Missing("--blink-at");
                    }

                    if (!double.TryParse(blinkText, NumberStyles.Float, CultureInfo.InvariantCulture, out double blink))
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture, $"--blink-at {blinkText} is not a number of seconds."));
                    }

                    blinkAt.Add(blink);
                    break;

                case "--time":
                    if (!TryTake(arguments, ref i, out string? timeText))
                    {
                        return Missing("--time");
                    }

                    if (!double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out time))
                    {
                        return Refusal.Unsupported(string.Create(
                            CultureInfo.InvariantCulture, $"--time {timeText} is not a number of seconds."));
                    }

                    break;

                case "--editordata":
                    if (!TryTake(arguments, ref i, out editordata))
                    {
                        return Missing("--editordata");
                    }

                    break;

                case "--content-root":
                    if (!TryTake(arguments, ref i, out contentRoot))
                    {
                        return Missing("--content-root");
                    }

                    break;

                case "--sdf-root":
                    if (!TryTake(arguments, ref i, out sdfRoot))
                    {
                        return Missing("--sdf-root");
                    }

                    break;

                case "--source-space":
                    sourceSpace = true;
                    break;

                // Two ways to draw another model into this one, differing only
                // in whether it takes the character's own parts off underneath.
                // A garment does; something worn on the face does not, and
                // replacing there deletes the head rather than the hat.
                case "--with":
                    if (!TryTake(arguments, ref i, out string? alongside) || alongside is null)
                    {
                        return Missing("--with");
                    }

                    with.Add(new WornModel(alongside, Replaces: true));
                    break;

                case "--over":
                    if (!TryTake(arguments, ref i, out string? onTop) || onTop is null)
                    {
                        return Missing("--over");
                    }

                    with.Add(new WornModel(onTop, Replaces: false));
                    break;

                case "--keep-empty-nodes":
                    pruneEmptyNodes = false;
                    break;

                case "--gap-anim":
                    if (!TryTake(arguments, ref i, out gapAnim))
                    {
                        return Missing("--gap-anim");
                    }

                    break;

                case "--allow-missing-parts":
                    allowMissingParts = true;
                    break;

                case "--allow-unposed":
                    allowUnposed = true;
                    break;

                case "--json":
                    json = true;
                    break;

                default:
                    return Refusal.Unsupported(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{arguments[i]} is not an option this build accepts."));
            }
        }

        if (output is null)
        {
            return Refusal.Unsupported("--out is required and names the GLB to write.");
        }

        if (mmb is null || cameldata is null)
        {
            // Naming the companion rather than the failure: the caller needs to
            // know which argument to add, not that something was absent.
            return Refusal.Unsupported(
                "--mmb and --cameldata are both required, and name the model geometry and its companion.");
        }

        return ExportRequest.Validate(new ExportRequest
        {
            Mmb = mmb,
            Cameldata = cameldata,
            Out = output,
            SetupAnim = setupAnim,
            ClipAnims = [.. clipAnims],
            SeparateAnimations = separateAnimations,
            AllowMissingParts = allowMissingParts,
            GapAnim = gapAnim,
            PruneEmptyNodes = pruneEmptyNodes,
            With = [.. with],
            ReadFromArchives = readFromArchives,
            Animate = animate,
            Time = time,
            MouthAnim = mouthAnim,
            MouthState = mouthState,
            EyesAnim = eyesAnim,
            EyeState = eyeState,
            PupilsAnim = pupilsAnim,
            PupilState = pupilState,
            EyebrowsAnim = eyebrowsAnim,
            EyebrowState = eyebrowState,
            PupilPosition = pupilPosition,
            LipsyncDatabase = lipsyncDatabase,
            SpeechId = speechId,
            WemRoot = wemRoot,
            VgmstreamCli = vgmstreamCli,
            BlinkAt = [.. blinkAt],
            Editordata = editordata,
            ContentRoot = contentRoot,
            SdfRoot = sdfRoot,
            SourceSpace = sourceSpace,
            AllowUnposed = allowUnposed,
            Json = json,
        });
    }

    private static bool TryTakeState(
        IReadOnlyList<string> arguments, ref int index, string option, out int? state,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Refusal? error)
    {
        state = null;
        if (!TryTake(arguments, ref index, out string? text))
        {
            error = Missing(option);
            return false;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            error = Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"{option} {text} is not a whole number."));
            return false;
        }

        state = parsed;
        error = null;
        return true;
    }

    private static bool TryTake(IReadOnlyList<string> arguments, ref int index, out string? value)
    {
        if (index + 1 >= arguments.Count)
        {
            value = null;
            return false;
        }

        value = arguments[++index];
        return true;
    }

    private static Refusal Missing(string option) => Refusal.Unsupported(string.Create(
        CultureInfo.InvariantCulture, $"{option} needs a value."));
}
