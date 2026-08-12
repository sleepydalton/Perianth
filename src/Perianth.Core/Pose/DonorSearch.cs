using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Geometry;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;

namespace Perianth.Core.Pose;

/// <summary>
/// One hierarchy offered as a pose for a model that has no setup of its own.
/// </summary>
/// <param name="VirtualPath">The ANIM this candidate is.</param>
/// <param name="Names">How many of the model's parts it can name at all.</param>
/// <param name="Poses">How many it actually draws, which is what a user sees.</param>
/// <param name="Adds">
/// For a gap filler, how many parts it contributes that the primary cannot name.
/// Zero means it is redundant however well it scores otherwise.
/// </param>
/// <param name="Disagreement">
/// How far, in model units, this hierarchy places the parts it shares with the
/// primary from where the primary puts them. Null where they share none.
/// </param>
/// <param name="Declared">
/// Whether the game's own actor definition names this hierarchy for this model.
/// </param>
public sealed record DonorCandidate(
    string VirtualPath, int Names, int Poses, int Adds, double? Disagreement, bool Declared = false);

/// <summary>
/// Finds the hierarchies that could pose a model which has none of its own.
/// </summary>
/// <remarks>
/// <para>
/// Twenty-nine of the game's 918 characters ship without a setup ANIM. Nothing in
/// such a model's own files leads to a hierarchy that fits it, and there are 665
/// to choose from, so the choice is the thing to remove rather than to present.
/// </para>
/// <para>
/// <b>Never rank on coverage alone.</b> The hierarchy naming the most of one
/// model's head was a crowd rig — it poses several characters spread across a
/// scene, so it named nearly every head node and threw the parts tens of units
/// apart. Ranked on coverage it came first; the export was wreckage. Coverage
/// says a hierarchy <em>can</em> name the parts, and nothing about where it puts
/// them, so <see cref="DonorCandidate.Disagreement"/> is carried beside it and a
/// caller must show both.
/// </para>
/// <para>
/// Naming every candidate is cheap and posing one is not, so the count comes
/// first over everything and only a shortlist is posed. That ordering is what
/// makes this usable while a window waits on it.
/// </para>
/// <para>
/// <b>The game's own answer is a tiebreak, not an override.</b> An actor
/// definition names an animation system, and the system names a setup, so for
/// some models there is a recorded answer that no search had to find. It is
/// ranked below agreement rather than above it because that record was measured
/// and found wanting: the four characters it reaches that no naming convention
/// does are all wired to one *test* system naming another character's setup,
/// which names none of one model's 1,349 parts and a quarter to a third of the
/// others'. A record that can be a developer's placeholder must not be able to
/// promote a hierarchy that visibly scatters the parts — so it orders within the
/// agreeing candidates and never past them.
/// </para>
/// </remarks>
public static class DonorSearch
{
    /// <summary>How many of the shortlist get posed rather than merely counted.</summary>
    public const int Shortlist = 12;

    /// <summary>
    /// Ranks hierarchies by how much of <paramref name="model"/> each one poses.
    /// </summary>
    /// <param name="declared">
    /// Hierarchies the game's own actor definition names for this model, which a
    /// caller reads from the model's animation system. Optional, and a hint
    /// rather than an answer — see the remarks on <see cref="DonorSearch"/> for
    /// why it cannot outrank agreement.
    /// </param>
    public static ImmutableArray<DonorCandidate> Primaries(
        GeometryModel model,
        IEnumerable<(string Path, AnimFile Anim)> candidates,
        int shortlist = Shortlist,
        IReadOnlySet<string>? declared = null)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(candidates);

        List<(string Path, AnimFile Anim, int Names)> counted = [];
        foreach ((string path, AnimFile anim) in candidates)
        {
            int names = Named(model, anim);
            if (names > 0)
            {
                counted.Add((path, anim, names));
            }
        }

        List<(string Path, int Names, PosedScene Scene)> posed = [];
        foreach ((string path, AnimFile anim, int names) in counted.OrderByDescending(c => c.Names).Take(shortlist))
        {
            Result<PosedScene> scene = SetupPose.Pose(model, anim, null, 0.0, allowMissingParts: true);
            if (scene.IsSuccess)
            {
                posed.Add((path, names, scene.Value));
            }
        }

        // The candidates are each other's reference. A hierarchy meant for one
        // character agrees with every other such hierarchy about where a shared
        // part sits; a crowd rig, which poses several characters spread across a
        // scene, disagrees with all of them. So the odd one out is found without
        // needing to know in advance which is which.
        //
        // Ranking on parts drawn alone put a crowd rig first on a real model --
        // it draws 77 where the right answer draws 46 -- and the export was
        // wreckage. There is no reference to check a *primary* against except
        // its rivals, which is why this is a consensus rather than a comparison.
        ImmutableArray<DonorCandidate>.Builder ranked = ImmutableArray.CreateBuilder<DonorCandidate>();
        foreach ((string path, int names, PosedScene scene) in posed)
        {
            List<double> apart = [];
            foreach ((string _, int _, PosedScene other) in posed)
            {
                if (!ReferenceEquals(other, scene) &&
                    BorrowedPose.Disagreement(scene, other) is (double median, double _))
                {
                    apart.Add(median);
                }
            }

            apart.Sort();
            double? consensus = apart.Count == 0 ? null : apart[apart.Count / 2];
            ranked.Add(new DonorCandidate(
                path, names, scene.Keep.Length, Adds: 0, Disagreement: consensus,
                Declared: declared?.Contains(path) == true));
        }

        return
        [
            .. ranked
                .OrderBy(c => c.Disagreement is null or <= Agreeing ? 0 : 1)
                .ThenBy(c => c.Declared ? 0 : 1)
                .ThenByDescending(c => c.Poses),
        ];
    }

    /// <summary>
    /// Ranks hierarchies by what they add to <paramref name="primary"/>, and by
    /// whether they agree with it about where the shared parts go.
    /// </summary>
    /// <remarks>
    /// Ordered so that a candidate adding nothing is never offered, and one that
    /// disagrees with the primary sorts below one that does not however much it
    /// adds. A gap filler that adds the most and lands its parts elsewhere is the
    /// exact failure this ordering exists to prevent.
    /// </remarks>
    public static ImmutableArray<DonorCandidate> GapFillers(
        GeometryModel model,
        AnimFile primary,
        IEnumerable<(string Path, AnimFile Anim)> candidates,
        int shortlist = Shortlist)
    {
        System.ArgumentNullException.ThrowIfNull(model);
        System.ArgumentNullException.ThrowIfNull(primary);
        System.ArgumentNullException.ThrowIfNull(candidates);

        Result<PosedScene> primaryPosed = SetupPose.Pose(model, primary, null, 0.0, allowMissingParts: true);
        if (!primaryPosed.IsSuccess)
        {
            return [];
        }

        // The parts the primary is silent about. Only these are the donor's to
        // speak for -- see BorrowedPose for why "cannot name" and not "does not
        // show".
        HashSet<string> gap = [];
        for (int part = 0; part < model.Parts.Length; part++)
        {
            string binding = model.Parts[part].HierarchyBindingName;
            if (!primary.TryGetNode(binding, out _))
            {
                gap.Add(binding);
            }
        }

        if (gap.Count == 0)
        {
            return [];
        }

        List<(string Path, AnimFile Anim, int Covers)> counted = [];
        foreach ((string path, AnimFile anim) in candidates)
        {
            int covers = gap.Count(binding => anim.TryGetNode(binding, out _));
            if (covers > 0)
            {
                counted.Add((path, anim, covers));
            }
        }

        ImmutableArray<DonorCandidate>.Builder ranked = ImmutableArray.CreateBuilder<DonorCandidate>();
        foreach ((string path, AnimFile anim, int covers) in counted.OrderByDescending(c => c.Covers).Take(shortlist))
        {
            Result<PosedScene> donorPosed = SetupPose.Pose(model, anim, null, 0.0, allowMissingParts: true);
            if (!donorPosed.IsSuccess)
            {
                continue;
            }

            Result<PosedScene> merged = BorrowedPose.Pose(model, primary, anim, null, 0.0);
            int adds = merged.IsSuccess ? merged.Value.Keep.Length - primaryPosed.Value.Keep.Length : 0;
            (double Median, double Worst)? apart =
                BorrowedPose.Disagreement(primaryPosed.Value, donorPosed.Value);

            ranked.Add(new DonorCandidate(
                path, covers, donorPosed.Value.Keep.Length, adds, apart?.Median));
        }

        return
        [
            .. ranked
                .Where(c => c.Adds > 0)
                .OrderBy(c => c.Disagreement is null or <= Agreeing ? 0 : 1)
                .ThenByDescending(c => c.Adds),
        ];
    }

    /// <summary>
    /// Below this, two hierarchies are placing shared parts in the same spot.
    /// </summary>
    /// <remarks>
    /// Two compatible single-character rigs measured 0.000 apart and a crowd rig
    /// 10.2, so nothing sits near this line: it separates two populations rather
    /// than trimming one. It is a sort key and a warning, never a refusal — the
    /// user can see the result, and a threshold that hid a working combination
    /// would be worse than one that shows a bad one.
    /// </remarks>
    public const double Agreeing = 0.5;

    private static int Named(GeometryModel model, AnimFile anim)
    {
        int named = 0;
        foreach (GeometryPart part in model.Parts)
        {
            if (anim.TryGetNode(part.HierarchyBindingName, out _))
            {
                named++;
            }
        }

        return named;
    }
}
