using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Text;
using Perianth.Core.Content;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;
using Perianth.Tests.Anim;
using Perianth.Tests.Mmb;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Whether a model's parts have anywhere to hang on the rig it is paired with.
/// </summary>
/// <remarks>
/// This exists because the absence of it produced a mod that installed, loaded
/// and drew nothing different: an in-game probe repointed a character at another
/// character's model and left the animation system alone, and the borrowed model
/// bound to 499 nodes of which the rig it was given declared 168. Roadmap
/// §10.118. Every fixture here is invented.
/// </remarks>
public sealed class RigCoverageTests : IDisposable
{
    private readonly DirectoryInfo _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rig-{Guid.NewGuid():N}"));

    public void Dispose() => _root.Delete(recursive: true);

    private const string Model = "made/up/model.mmb";
    private const string System = "made/up/rig.manimsys";
    private const string SetupPath = "made/up/anm_rig_setup.anim";

    [Fact]
    public void A_rig_declaring_every_binding_node_is_complete()
    {
        Write(Model, ModelBinding("joint"));
        Write(SetupPath, AnimFileBuilder.Setup("joint", "spare"));
        Write(System, AnimationSystem(SetupPath));

        RigCoverage coverage = Ok(AnimationSystems.Coverage(Content(), Model, System));

        Assert.True(coverage.Complete);
        Assert.Equal(1, coverage.Bindings);
        Assert.Equal(1, coverage.Declared);
        Assert.Empty(coverage.Unplaced);
        Assert.Equal(SetupPath, coverage.Setup);
    }

    [Fact]
    public void A_rig_missing_a_binding_node_names_the_parts_that_cannot_draw()
    {
        Write(Model, ModelBinding("joint"));
        Write(SetupPath, AnimFileBuilder.Setup("somethingElse"));
        Write(System, AnimationSystem(SetupPath));

        RigCoverage coverage = Ok(AnimationSystems.Coverage(Content(), Model, System));

        Assert.False(coverage.Complete);
        Assert.Equal(1, coverage.Bindings);
        Assert.Equal(0, coverage.Declared);
        Assert.Equal(["joint"], coverage.Unplaced);
    }

    [Fact]
    public void A_root_that_does_not_hold_the_model_refuses_rather_than_reporting_a_fit()
    {
        // "Cannot check" must not read as "checked and fine", which is the shape
        // of every quiet failure this project has had to correct.
        Write(SetupPath, AnimFileBuilder.Setup("joint"));
        Write(System, AnimationSystem(SetupPath));

        Result<RigCoverage> coverage = AnimationSystems.Coverage(Content(), Model, System);

        Assert.False(coverage.IsSuccess);
        Assert.Equal(RefusalKind.Resource, coverage.Refusal.Kind);
    }

    [Fact]
    public void An_animation_system_naming_no_setup_refuses()
    {
        Write(Model, ModelBinding("joint"));
        Write(System, AnimationSystem());

        Result<RigCoverage> coverage = AnimationSystems.Coverage(Content(), Model, System);

        Assert.False(coverage.IsSuccess);
        Assert.Contains("no setup", coverage.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_animation_system_naming_two_setups_refuses_rather_than_choosing()
    {
        Write(Model, ModelBinding("joint"));
        Write(SetupPath, AnimFileBuilder.Setup("joint"));
        Write("made/up/anm_other_setup.anim", AnimFileBuilder.Setup("joint"));
        Write(System, AnimationSystem(SetupPath, "made/up/anm_other_setup.anim"));

        Result<RigCoverage> coverage = AnimationSystems.Coverage(Content(), Model, System);

        Assert.False(coverage.IsSuccess);
        Assert.Equal(RefusalKind.Unsupported, coverage.Refusal.Kind);
    }

    [Fact]
    public void Every_graph_object_naming_a_model_is_reported()
    {
        // Both editing one and editing all of them are things somebody means to
        // do, so this reports the set rather than choosing between them.
        Write("camel/graph objects/actor/one.mgraphobject", Actor(Model));
        Write("camel/graph objects/actor/two.mgraphobject", Actor(Model));
        Write("camel/graph objects/actor/other.mgraphobject", Actor("made/up/elsewhere.mmb"));

        Result<ImmutableArray<string>> naming = AnimationSystems.ActorsNaming(Content(), Model);

        Assert.True(naming.IsSuccess, naming.IsSuccess ? string.Empty : naming.Refusal.Message);
        Assert.Equal(
            ["camel/graph objects/actor/one.mgraphobject", "camel/graph objects/actor/two.mgraphobject"],
            naming.Value);
    }

    [Fact]
    public void A_model_no_graph_object_names_reports_none()
    {
        Write("camel/graph objects/actor/other.mgraphobject", Actor("made/up/elsewhere.mmb"));

        Result<ImmutableArray<string>> naming = AnimationSystems.ActorsNaming(Content(), Model);

        Assert.True(naming.IsSuccess, naming.IsSuccess ? string.Empty : naming.Refusal.Message);
        Assert.Empty(naming.Value);
    }

    [Fact]
    public void An_unreadable_graph_object_does_not_hide_the_rest()
    {
        // One broken definition says nothing about this model either way, and
        // answering nothing about the other 1,249 because of it would be worse.
        Write("camel/graph objects/actor/broken.mgraphobject", [0x00, 0x01, 0x02]);
        Write("camel/graph objects/actor/one.mgraphobject", Actor(Model));

        Result<ImmutableArray<string>> naming = AnimationSystems.ActorsNaming(Content(), Model);

        Assert.True(naming.IsSuccess, naming.IsSuccess ? string.Empty : naming.Refusal.Message);
        Assert.Equal(["camel/graph objects/actor/one.mgraphobject"], naming.Value);
    }

    private ContentSources Content() => new(_root.FullName, sdfRoot: null);

    /// <summary>A BVM container whose string table names one model.</summary>
    private static byte[] Actor(string model) => Container(["MMAFile", model]);

    private void Write(string virtualPath, byte[] bytes)
    {
        string path = Path.Combine(_root.FullName, virtualPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A model of one part bound to <paramref name="node"/>.</summary>
    private static byte[] ModelBinding(string node) => new MmbFileBuilder
    {
        Label = $"{node}|shape1",
        NodeNames = [node],
        PositionEntries = [0, 1, 2],
        EntrySize = sizeof(ushort),
        VertexCount = 3,
    }.Build();

    /// <summary>A BVM container whose string table names some setups.</summary>
    private static byte[] AnimationSystem(params string[] setups)
    {
        List<string> table = ["AnimationSystem"];
        table.AddRange(setups);
        return Container(table);
    }

    /// <summary>A BVM container carrying a string table and an empty graph.</summary>
    private static byte[] Container(List<string> table)
    {
        List<byte> bytes = [0xFF, (byte)'B', (byte)'V', (byte)'M'];
        CompactInteger.Write(bytes, (uint)table.Count);
        foreach (string text in table)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(text);
            CompactInteger.Write(bytes, (uint)encoded.Length);
            bytes.AddRange(encoded);
        }

        // One empty container, which is all the reader needs past the table.
        bytes.AddRange([0x01, 0x00, 0x01, 0x02]);
        return [.. bytes];
    }

    private static RigCoverage Ok(Result<RigCoverage> result)
    {
        Assert.True(result.IsSuccess, result.IsSuccess ? string.Empty : result.Refusal.Message);
        return result.Value;
    }
}
