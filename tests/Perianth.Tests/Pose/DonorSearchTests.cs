using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Pose;

/// <summary>
/// Choosing a hierarchy for a model that has none of its own.
/// </summary>
public sealed class DonorSearchTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"donor-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_hierarchy_that_scatters_the_parts_ranks_below_one_that_does_not()
    {
        // The rule this exists for, and the failure it was written against: the
        // hierarchy naming the most of a real model's head was a crowd rig, which
        // poses several characters spread across a scene. On coverage alone it
        // came first and the export was wreckage. Here the scattered candidate
        // covers strictly more -- two parts against one -- and must still lose.
        AnimFile primary = Setup(["body"], [Root], [Shown]);
        AnimFile agrees = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        AnimFile scattered = Setup(["body", "head", "hair"], [Root, Root, Root], [Shown, Shown, Shown], away: 40.0);
        GeometryModel model = Model(("body", 0), ("head", 1), ("hair", 2));

        ImmutableArray<DonorCandidate> ranked = DonorSearch.GapFillers(
            model, primary, [("agrees.anim", agrees), ("scattered.anim", scattered)]);

        Assert.Equal("agrees.anim", ranked[0].VirtualPath);
        Assert.True(ranked[0].Disagreement <= DonorSearch.Agreeing);

        // The scattered one is still offered, with its disagreement stated: the
        // user can see the result, so this ranks rather than refuses.
        DonorCandidate bad = ranked.Single(c => c.VirtualPath == "scattered.anim");
        Assert.True(bad.Adds > ranked[0].Adds);
        Assert.True(bad.Disagreement > 1.0);
    }

    [Fact]
    public void A_hierarchy_that_adds_nothing_is_not_offered()
    {
        // However well it scores otherwise: a donor whose parts the primary
        // already names has nothing to contribute, and offering it would waste
        // the one choice this feature asks a user to make.
        AnimFile primary = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        AnimFile redundant = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        GeometryModel model = Model(("body", 0), ("head", 1));

        Assert.Empty(DonorSearch.GapFillers(model, primary, [("redundant.anim", redundant)]));
    }

    [Fact]
    public void Primaries_rank_by_what_they_actually_draw()
    {
        // Naming a part and drawing it are different: a hierarchy that names
        // everything and hides most of it is a worse pose than one that names
        // less and shows it.
        AnimFile draws = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        AnimFile hides = Setup(["body", "head"], [Root, Root], [Shown, Hidden]);
        GeometryModel model = Model(("body", 0), ("head", 1));

        ImmutableArray<DonorCandidate> ranked = DonorSearch.Primaries(
            model, [("hides.anim", hides), ("draws.anim", draws)]);

        Assert.Equal("draws.anim", ranked[0].VirtualPath);
        Assert.Equal(2, ranked[0].Poses);
    }

    [Fact]
    public void The_hierarchy_the_game_names_is_preferred_among_equals()
    {
        // Two hierarchies that pose the model equally well. The game's own actor
        // definition names one of them, and that is the only thing separating
        // them, so it decides.
        AnimFile named = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        AnimFile other = Setup(["body", "head"], [Root, Root], [Shown, Shown]);
        GeometryModel model = Model(("body", 0), ("head", 1));

        ImmutableArray<DonorCandidate> ranked = DonorSearch.Primaries(
            model,
            [("other.anim", other), ("named.anim", named)],
            declared: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "named.anim" });

        Assert.Equal("named.anim", ranked[0].VirtualPath);
        Assert.True(ranked[0].Declared);
        Assert.False(ranked[1].Declared);
    }

    [Fact]
    public void The_hierarchy_the_game_names_still_loses_to_one_that_agrees()
    {
        // The measured reason this is a tiebreak and not an answer. The four
        // characters the game's record reaches that no naming convention does
        // are wired to a test system naming another character's setup. So a
        // declared hierarchy that scatters the shared parts must still rank
        // below one that places them where everything else does.
        // Four that agree, so the consensus has something to be a consensus of.
        // Three candidates is not enough: a median over two comparisons takes
        // the larger, and every candidate then looks as far out as the outlier.
        // The real population is a shortlist of twelve.
        AnimFile scatters = Setup(["body", "head"], [Root, Root], [Shown, Shown], away: 40.0);
        GeometryModel model = Model(("body", 0), ("head", 1));

        List<(string, AnimFile)> candidates = [("scatters.anim", scatters)];
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            candidates.Add(($"{name}.anim", Setup(["body", "head"], [Root, Root], [Shown, Shown])));
        }

        ImmutableArray<DonorCandidate> ranked = DonorSearch.Primaries(
            model,
            candidates,
            declared: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scatters.anim" });

        DonorCandidate declared = ranked.Single(c => c.Declared);
        Assert.NotEqual(0, ranked.IndexOf(declared));
        Assert.True(declared.Disagreement > DonorSearch.Agreeing);
    }

    [Fact]
    public void A_hierarchy_naming_none_of_the_model_is_not_a_candidate()
    {
        AnimFile stranger = Setup(["nothing_in_common"], [Root], [Shown]);
        GeometryModel model = Model(("body", 0));

        Assert.Empty(DonorSearch.Primaries(model, [("stranger.anim", stranger)]));
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Shown = 0xFFFF;
    private const int Hidden = 0xFFFE;

    /// <summary>
    /// A translation as the format stores one: three 20-bit fixed-point values
    /// sharing a 4-bit exponent, in eight bytes.
    /// </summary>
    /// <remarks>
    /// Exponent 15 is the largest the four bits hold, giving a multiplier of
    /// 2^-4, so a whole-number offset is encoded as sixteen times itself.
    /// </remarks>
    private static byte[] Packed(double x, double y, double z)
    {
        const int exponent = 15;
        double multiplier = Math.ScaleB(1.0, exponent - 19);
        int Fixed(double v) => (int)Math.Round(v / multiplier);

        int fx = Fixed(x), fy = Fixed(y), fz = Fixed(z);
        short hx = (short)(fx >> 4), hy = (short)(fy >> 4), hz = (short)(fz >> 4);
        int packed = ((fx & 0xF) << 12) | ((fy & 0xF) << 8) | ((fz & 0xF) << 4) | exponent;

        return
        [
            (byte)(hx & 0xFF), (byte)((hx >> 8) & 0xFF),
            (byte)(hy & 0xFF), (byte)((hy >> 8) & 0xFF),
            (byte)(hz & 0xFF), (byte)((hz >> 8) & 0xFF),
            (byte)(packed & 0xFF), (byte)((packed >> 8) & 0xFF),
        ];
    }

    private AnimFile Setup(string[] names, int[] parents, int[] scai, double away = 0.0)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(24f));
        Write32(bytes, 0x10, 1);
        Write32(bytes, 0x1C, 5);
        Write32(bytes, 0x24, names.Length);

        // Ordered DTRA, TRAI, SCAI, NAME, PRNT: a blob runs to the next tag that
        // is present, so the order is what bounds it.
        if (away != 0.0)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes("DTRA"));
            bytes.AddRange(Packed(away, 0, 0));

            bytes.AddRange(Encoding.ASCII.GetBytes("TRAI"));
            foreach (string _ in names)
            {
                bytes.Add(0x00);
                bytes.Add(0x80);   // 0x8000: the first static entry
            }
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("SCAI"));
        foreach (int v in scai)
        {
            bytes.Add((byte)(v & 0xFF));
            bytes.Add((byte)((v >> 8) & 0xFF));
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("NAME"));
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        bytes.AddRange(Encoding.ASCII.GetBytes("PRNT"));
        foreach (int p in parents)
        {
            int v = p < 0 ? 0xFFFF : p;
            bytes.Add((byte)(v & 0xFF));
            bytes.Add((byte)((v >> 8) & 0xFF));
        }

        string path = Path.Combine(_directory.FullName, $"s{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        return AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy: true).Value;
    }

    private static GeometryModel Model(params (string Binding, int Ordinal)[] parts) =>
        new(3,
        [
            .. parts.Select(p => new GeometryPart(
                p.Ordinal,
                $"mode3-record-{p.Ordinal}",
                $"label:{p.Binding}",
                p.Binding,
                [new Vector3D(0, 0, 0), new Vector3D(1, 0, 0), new Vector3D(0, 1, 0)],
                [0, 1, 2],
                [],
                [new Vector3D(0, 0, 1), new Vector3D(0, 0, 1), new Vector3D(0, 0, 1)])),
        ], false);

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
