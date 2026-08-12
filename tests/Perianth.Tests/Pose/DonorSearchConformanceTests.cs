using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Sdf;
using Xunit;

namespace Perianth.Tests.Pose;

/// <summary>
/// The donor ranking against the game's own hierarchies.
/// </summary>
/// <remarks>
/// <para>
/// Skips without the archives, as the DDS and SDF suites do, so an ordinary
/// <c>dotnet test</c> stays asset-free.
/// </para>
/// <para>
/// This exists because the synthetic tests passed while the ranking was wrong.
/// They proved the ordering rule fired when a candidate was known to disagree;
/// they could not show that a real crowd rig would be *detected* as disagreeing,
/// and it was not — the primary list ranked on parts drawn alone and put the
/// crowd rig first, which is what a user saw. The failure was in the population,
/// not the rule, and only real hierarchies have that population.
/// </para>
/// </remarks>
public sealed class DonorSearchConformanceTests
{
    private const string RootVariable = "PERIANTH_SDF_ROOT";

    /// <summary>
    /// The virtual path, without extension, of a model that has no setup ANIM of
    /// its own. Named by the environment rather than here: which model that is
    /// is the game's content, and belongs with the census that found it.
    /// </summary>
    private const string ModelVariable = "PERIANTH_SETUPLESS_MODEL";

    [Fact]
    public void A_hierarchy_that_scatters_shared_parts_never_leads_the_ranking()
    {
        string root = Environment.GetEnvironmentVariable(RootVariable) ?? string.Empty;
        string path = Environment.GetEnvironmentVariable(ModelVariable) ?? string.Empty;
        if (root.Length == 0 || path.Length == 0)
        {
            Assert.Skip($"set {RootVariable} and {ModelVariable} to run the donor ranking against the archives");
            return;
        }

        using SdfContentSource source = new(root);
        GeometryModel model = Geometry(source, path);

        ImmutableArray<DonorCandidate> primaries = DonorSearch.Primaries(model, Setups(root));
        Assert.NotEmpty(primaries);

        // Every candidate the ranking is willing to put first must agree with the
        // consensus. A hierarchy that scatters the parts may appear in the list,
        // and must never lead it.
        Assert.True(
            primaries[0].Disagreement is null or <= DonorSearch.Agreeing,
            $"the top hierarchy {primaries[0].VirtualPath} disagrees by {primaries[0].Disagreement}");

        // The failure a user reported, stated without naming the rig that caused
        // it: something drew MORE of this model than the right answer did, so
        // ranking on parts alone put it first. Every such candidate must have
        // been detected as disagreeing, and none of them may lead — which is a
        // stronger claim than checking the one hierarchy already known to fail,
        // and it keeps holding when the archives change under it.
        ImmutableArray<DonorCandidate> greedier =
            [.. primaries.Where(c => c.Poses > primaries[0].Poses)];

        Assert.True(
            greedier.Length > 0,
            "this test is only meaningful while something outdraws the right answer, which is what made it win");

        foreach (DonorCandidate c in greedier)
        {
            Assert.True(
                c.Disagreement > DonorSearch.Agreeing,
                $"{c.VirtualPath} draws more yet was not detected as placing shared parts elsewhere");
        }
    }

    private static GeometryModel Geometry(SdfContentSource source, string model)
    {
        MmbModel mmb = MmbReader.Read(
            SourceFile.FromMemory(model + ".mmb", source.Read(model + ".mmb").Value.Bytes)).Value;
        CameldataFile cameldata = CameldataReader.Read(
            SourceFile.FromMemory(model + ".cameldata", source.Read(model + ".cameldata").Value.Bytes)).Value;
        return GeometryAssembler.Assemble(mmb, cameldata).Value;
    }

    private static IEnumerable<(string Path, AnimFile Anim)> Setups(string root)
    {
        using SdfContentSource source = new(root);
        foreach (SdfPathEntry entry in source.Paths().Value)
        {
            if (!entry.Path.EndsWith("_setup.anim", StringComparison.Ordinal))
            {
                continue;
            }

            Result<SdfContent> raw = source.Read(entry.Path);
            if (!raw.TryGetValue(out SdfContent bytes, out _) || !bytes.IsPresent)
            {
                continue;
            }

            Result<AnimFile> anim = AnimReader.Read(
                SourceFile.FromMemory(entry.Path, bytes.Bytes), hierarchy: true);
            if (anim.IsSuccess)
            {
                yield return (entry.Path, anim.Value);
            }
        }
    }
}
