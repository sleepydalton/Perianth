using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;

namespace Perianth.Pipeline;

/// <summary>
/// One export, described completely.
/// </summary>
/// <remarks>
/// <para>
/// The settings only, with no notion of where they came from: a command line
/// parses one, and a window fills one in. Only options this build can honour
/// appear. An option the frozen grammar defines but nothing here implements
/// would be worse than an unknown one, because it would be silently ignored and
/// the export would quietly not be what was asked for.
/// </para>
/// <para>
/// <see cref="Validate"/> holds the rules between the fields, and they live here
/// rather than in the command line's argument grammar because they are not
/// grammar: "an atlas and its state are meaningless apart" is true of a window
/// too. A front end that had to restate them would restate them differently.
/// </para>
/// </remarks>
public sealed record ExportRequest
{
    /// <summary>The model geometry.</summary>
    public required string Mmb { get; init; }

    /// <summary>The companion pools and constants.</summary>
    public required string Cameldata { get; init; }

    /// <summary>Where the GLB is published.</summary>
    public required string Out { get; init; }

    /// <summary>Omit the source-to-glTF presentation root.</summary>
    public bool SourceSpace { get; init; }

    /// <summary>
    /// Export the complete part list even when this model's own setup ANIM sits
    /// unpassed beside the inputs.
    /// </summary>
    /// <remarks>
    /// Without it, a setup ANIM sitting beside the geometry that turns out to rig
    /// this very model refuses the unposed export as a forgotten argument. This
    /// says the overlaid-states part list was meant; it does not suppress the
    /// unposed warning or change the scene name.
    /// </remarks>
    public bool AllowUnposed { get; init; }

    /// <summary>Emit one line of machine-readable result instead of prose.</summary>
    public bool Json { get; init; }

    /// <summary>The setup ANIM that places and selects the model's parts.</summary>
    public string? SetupAnim { get; init; }

    /// <summary>The clip ANIM whose channels override the setup over time.</summary>
    public string? ClipAnim { get; init; }

    /// <summary>Emit the clip as native glTF animation rather than a single sampled pose.</summary>
    public bool Animate { get; init; }

    /// <summary>The time in seconds to sample a single pose at; nonzero needs a setup.</summary>
    public double Time { get; init; }

    /// <summary>The mouth facial atlas ANIM, overlaid on the setup pose.</summary>
    public string? MouthAnim { get; init; }

    /// <summary>The one-based mouth state selecting a zero-based atlas sample; 1..24.</summary>
    public int? MouthState { get; init; }

    /// <summary>The eyes facial atlas ANIM.</summary>
    public string? EyesAnim { get; init; }

    /// <summary>The one-based eye state; 1..11.</summary>
    public int? EyeState { get; init; }

    /// <summary>The pupils facial atlas ANIM.</summary>
    public string? PupilsAnim { get; init; }

    /// <summary>The one-based pupil state; 1..13.</summary>
    public int? PupilState { get; init; }

    /// <summary>The eyebrows facial atlas ANIM.</summary>
    public string? EyebrowsAnim { get; init; }

    /// <summary>The one-based eyebrow state; 1..6.</summary>
    public int? EyebrowState { get; init; }

    /// <summary>
    /// How the pupil layer places the pupils: <c>authored-state</c> applies the
    /// atlas translation, <c>mesh-neutral</c> suppresses it to reach the
    /// mesh-authored placement.
    /// </summary>
    public string PupilPosition { get; init; } = "authored-state";

    /// <summary>The BVM lip-sync database that drives the mouth atlas over time.</summary>
    public string? LipsyncDatabase { get; init; }

    /// <summary>The numeric speech ID selecting one schedule from the database and the WEM to decode.</summary>
    public string? SpeechId { get; init; }

    /// <summary>Root of an extracted WEM tree to resolve the speech audio from; writes a WAV beside the GLB.</summary>
    public string? WemRoot { get; init; }

    /// <summary>Path to the vgmstream-cli executable that decodes the WEM; found on PATH when absent.</summary>
    public string? VgmstreamCli { get; init; }

    /// <summary>The times, in seconds, at which to inject an explicit 1/12-second blink.</summary>
    public ImmutableArray<double> BlinkAt { get; init; } = [];

    /// <summary>The model's editordata, giving materials and texture paths.</summary>
    public string? Editordata { get; init; }

    /// <summary>Root of an unpacked content tree, tried before the archives.</summary>
    public string? ContentRoot { get; init; }

    /// <summary>Directory holding sdf.sdftoc and its archives.</summary>
    public string? SdfRoot { get; init; }

    /// <summary>
    /// Checks the rules between the fields, and returns the request unchanged
    /// when it satisfies them.
    /// </summary>
    /// <remarks>
    /// Every refusal names the companion argument rather than the failure, so a
    /// caller learns what to add. That wording is the frozen contract from
    /// specification §12 and is compared verbatim, so it is not prose to improve.
    /// </remarks>
    public static Result<ExportRequest> Validate(ExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ClipAnim is not null && request.SetupAnim is null)
        {
            return Refusal.Unsupported("--clip-anim overrides a setup hierarchy over time, so it needs --setup-anim.");
        }

        if (request.SetupAnim is null && request.Time != 0.0)
        {
            return Refusal.Unsupported("--time samples a pose from a hierarchy, so a nonzero time needs --setup-anim.");
        }

        if (request.Animate && (request.SetupAnim is null || request.ClipAnim is null))
        {
            return Refusal.Unsupported("--animate emits a clip, so it needs both --setup-anim and --clip-anim.");
        }

        if (request.Animate && request.Time != 0.0)
        {
            return Refusal.Unsupported("--animate emits the whole clip, so it cannot be combined with a nonzero --time.");
        }

        // A blink is an explicit event injected into an animation on the eye atlas.
        if (request.BlinkAt.Length > 0 && !request.Animate)
        {
            return Refusal.Unsupported("--blink-at injects events into an animation, so it requires --animate.");
        }

        if (request.BlinkAt.Length > 0 && request.EyesAnim is null)
        {
            return Refusal.Unsupported("--blink-at plays on the eye atlas, so it requires --eyes-anim.");
        }

        // Each facial system is an atlas and the one-based state that reads it. A
        // facial atlas overlays an expression on a hierarchy, so any needs a setup;
        // an atlas and its state are meaningless apart; and each state is bounded to
        // the vocabulary its atlas holds.
        (string Anim, string? AnimPath, string State, int? StateValue, int Low, int High)[] facial =
        [
            ("--mouth-anim", request.MouthAnim, "--mouth-state", request.MouthState, 1, 24),
            ("--eyes-anim", request.EyesAnim, "--eye-state", request.EyeState, 1, 11),
            ("--pupils-anim", request.PupilsAnim, "--pupil-state", request.PupilState, 1, 13),
            ("--eyebrows-anim", request.EyebrowsAnim, "--eyebrow-state", request.EyebrowState, 1, 6),
        ];

        if (Array.Exists(facial, f => f.AnimPath is not null || f.StateValue is not null) && request.SetupAnim is null)
        {
            return Refusal.Unsupported("A facial atlas overlays states on a hierarchy, so it needs --setup-anim.");
        }

        foreach ((string anim, string? animPath, string state, int? stateValue, int low, int high) in facial)
        {
            if (stateValue is not null && (stateValue < low || stateValue > high))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"{state} selects one of the atlas's authored states, so it must be in {low}..{high}."));
            }

            if ((stateValue is null) != (animPath is null))
            {
                // Lip sync drives the mouth atlas from the schedule rather than a
                // fixed state, so it is the one atlas allowed without its state.
                if (anim == "--mouth-anim" && animPath is not null && request.LipsyncDatabase is not null)
                {
                    continue;
                }

                // Blink drives the eye atlas from explicit events; the eye state is
                // then only the optional hold between them.
                if (anim == "--eyes-anim" && animPath is not null && request.BlinkAt.Length > 0)
                {
                    continue;
                }

                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"{anim} and {state} name an atlas and the state to read from it, so they must be supplied together."));
            }
        }

        if (request.PupilPosition is not ("authored-state" or "mesh-neutral"))
        {
            return Refusal.Unsupported("--pupil-position must be authored-state or mesh-neutral.");
        }

        // Without the atlas there is no translation to suppress, so the flag would
        // silently do nothing — the same trap as a half-supplied facial pair.
        if (request.PupilPosition == "mesh-neutral" && request.PupilsAnim is null)
        {
            return Refusal.Unsupported("--pupil-position mesh-neutral suppresses the pupil atlas translation, so it requires --pupils-anim.");
        }

        // Lip sync selects one schedule by ID and plays it on the mouth atlas, so
        // it needs both, and the fixed mouth state would contradict the schedule.
        if (request.LipsyncDatabase is not null && request.SpeechId is null)
        {
            return Refusal.Unsupported("--lipsync-database plays one schedule chosen by ID, so it requires --speech-id.");
        }

        if (request.LipsyncDatabase is not null && request.MouthAnim is null)
        {
            return Refusal.Unsupported("--lipsync-database drives the mouth atlas, so it requires --mouth-anim.");
        }

        if (request.LipsyncDatabase is not null && request.MouthState is not null)
        {
            return Refusal.Unsupported("--mouth-state fixes the mouth, so it cannot be combined with the --lipsync-database schedule.");
        }

        // A speech ID selects a schedule to play, audio to decode, or both.
        if (request.SpeechId is not null && request.LipsyncDatabase is null && request.WemRoot is null)
        {
            return Refusal.Unsupported("--speech-id names a schedule or audio to resolve, so it requires --lipsync-database or --wem-root.");
        }

        if (request.WemRoot is not null && request.SpeechId is null)
        {
            return Refusal.Unsupported("--wem-root resolves the audio for a speech ID, so it requires --speech-id.");
        }

        if (request.VgmstreamCli is not null && request.WemRoot is null)
        {
            return Refusal.Unsupported("--vgmstream-cli decodes the resolved WEM, so it requires --wem-root.");
        }

        if (request.Editordata is not null && request.ContentRoot is null && request.SdfRoot is null)
        {
            // Materials need the paths and the bytes. Naming the missing half
            // beats exporting an untextured model the caller did not ask for.
            return Refusal.Unsupported(
                "--editordata gives materials and texture paths, so it needs --content-root or --sdf-root to read the textures from.");
        }

        // Writing over an input would destroy the thing being read.
        foreach (string input in new[] { request.Mmb, request.Cameldata })
        {
            if (SamePath(input, request.Out))
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture,
                    $"The output path {request.Out} is also an input, and an export must not overwrite what it reads."));
            }
        }

        return Result.Ok(request);
    }

    private static bool SamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
