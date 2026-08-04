using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// One half-open time interval of a facial layer: the atlas sample it selects
/// while <paramref name="Start"/> ≤ t &lt; <paramref name="End"/>.
/// </summary>
public readonly record struct FacialInterval(double Start, double End, int Sample);

/// <summary>
/// One exact-name facial ANIM layer overlaid on the body pose.
/// </summary>
/// <remarks>
/// A facial atlas is an ordinary ANIM without a required hierarchy. It binds
/// complete node names exactly and overrides only the setup's animated selectors
/// — static and sentinel atlas channels claim nothing. A fixed layer holds one
/// sample for the whole export; an interval layer (blink, lip-sync) selects a
/// sample per half-open span and falls back to <paramref name="DefaultSample"/>
/// outside them.
/// </remarks>
/// <param name="Name">The layer's system name, used in reporting and refusals ("mouth", "eyes"…).</param>
/// <param name="Atlas">The parsed atlas ANIM the samples are read from.</param>
/// <param name="Intervals">The half-open spans, empty for a fixed layer.</param>
/// <param name="DefaultSample">The sample used outside every interval, or none.</param>
/// <param name="SuppressTranslation">
/// Selects the bank and applies every other channel but leaves the node's
/// composed translation alone. The atlas's own translation is what moves a pupil
/// off the position its mesh authors, so suppressing it is the only way to reach
/// that authored placement — no atlas sample encodes it.
/// </param>
/// <param name="RequireCompleteIntervals">
/// Every interval must fall within the body clip. An explicit blink is placed by
/// the caller against a specific timeline, so a blink past the clip's end is a
/// mistake to refuse, unlike a schedule that merely runs long.
/// </param>
public sealed record FacialLayer(
    string Name,
    AnimFile Atlas,
    ImmutableArray<FacialInterval> Intervals,
    int? DefaultSample,
    bool SuppressTranslation,
    bool RequireCompleteIntervals = false)
{
    /// <summary>A fixed layer holding <paramref name="sample"/> for the whole export.</summary>
    public static FacialLayer Fixed(string name, AnimFile atlas, int sample, bool suppressTranslation = false) =>
        new(name, atlas, [], sample, suppressTranslation);

    /// <summary>
    /// A lip-sync layer whose mouth samples follow a key schedule: each adjacent
    /// pair spans <c>[start/24, end/24)</c> at the earlier pair's selector minus
    /// one, and sample 20 holds outside every interval.
    /// </summary>
    /// <remarks>
    /// Zero-length spans are dropped and negative key times are valid pre-roll, so
    /// the schedule is walked pairwise and only forward-going pairs form intervals;
    /// the final pair supplies an endpoint alone.
    /// </remarks>
    public static FacialLayer Lipsync(AnimFile atlas, System.Collections.Generic.IReadOnlyList<(int KeyTime, int Selector)> pairs)
    {
        System.ArgumentNullException.ThrowIfNull(pairs);

        ImmutableArray<FacialInterval>.Builder intervals = ImmutableArray.CreateBuilder<FacialInterval>();
        for (int i = 0; i + 1 < pairs.Count; i++)
        {
            (int start, int selector) = pairs[i];
            int end = pairs[i + 1].KeyTime;
            if (start < end)
            {
                intervals.Add(new FacialInterval(start / 24.0, end / 24.0, selector - 1));
            }
        }

        return new FacialLayer("lip sync", atlas, intervals.ToImmutable(), DefaultSample: 20, SuppressTranslation: false);
    }

    /// <summary>
    /// An explicit-blink layer: a proven 1/12-second Blink hold (atlas sample 1)
    /// at each requested start, holding <paramref name="defaultSample"/> — the
    /// fixed eye state, if any — between.
    /// </summary>
    /// <remarks>
    /// Each boundary is rounded to binary32 independently, so the stored span is
    /// <c>[float32(start), float32(start + 1/12))</c>; because 1/12 is not exactly
    /// representable, the encoded duration is the difference of those rounded ends,
    /// not a claim of an exact rational. Starts are sorted and must be finite,
    /// nonnegative, each able to represent its duration, and nonoverlapping.
    /// </remarks>
    public static Result<FacialLayer> Blink(AnimFile atlas, IEnumerable<double> starts, int? defaultSample)
    {
        System.ArgumentNullException.ThrowIfNull(atlas);
        System.ArgumentNullException.ThrowIfNull(starts);

        const double hold = 1.0 / 12.0;
        ImmutableArray<FacialInterval>.Builder intervals = ImmutableArray.CreateBuilder<FacialInterval>();
        double? previousEnd = null;
        foreach (double start in starts.OrderBy(s => s))
        {
            // Kinds follow the reference: a malformed set of times (non-finite,
            // negative, or overlapping) is malformed, while a time whose duration
            // the float32 grid cannot represent is a request it cannot satisfy.
            if (!double.IsFinite(start) || start < 0.0)
            {
                return Refusal.Malformed("--blink-at values must be finite and nonnegative.");
            }

            double encodedStart = (float)start;
            double encodedEnd = (float)(start + hold);
            if (encodedEnd <= encodedStart)
            {
                return Refusal.Unsupported(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"A blink at {start:g9} seconds cannot represent its 1/12-second duration."));
            }

            if (previousEnd is double prior && encodedStart < prior)
            {
                return Refusal.Malformed("--blink-at intervals must not overlap.");
            }

            // CLI Blink state 2 is atlas sample 1.
            intervals.Add(new FacialInterval(encodedStart, encodedEnd, 1));
            previousEnd = encodedEnd;
        }

        return Result.Ok(new FacialLayer(
            "explicit blink", atlas, intervals.ToImmutable(), defaultSample, SuppressTranslation: false, RequireCompleteIntervals: true));
    }

    /// <summary>The atlas sample active at <paramref name="seconds"/>, or none.</summary>
    public int? SampleAt(double seconds)
    {
        foreach (FacialInterval interval in Intervals)
        {
            if (interval.Start <= seconds && seconds < interval.End)
            {
                return interval.Sample;
            }
        }

        return DefaultSample;
    }

    /// <summary>Every interval boundary, both ends, in interval order.</summary>
    public IEnumerable<double> Boundaries()
    {
        foreach (FacialInterval interval in Intervals)
        {
            yield return interval.Start;
            yield return interval.End;
        }
    }
}
