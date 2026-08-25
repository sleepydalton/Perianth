using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using Perianth.Core.Content;
using Perianth.Core.Geometry;
using Perianth.Formats.Cameldata;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Perianth.Formats.Mmb;
using Perianth.Gltf;
using Xunit;

namespace Perianth.Tests.Content;

/// <summary>
/// Export a real model, import it back unedited, and require the cameldata to
/// come out byte-identical.
/// </summary>
/// <remarks>
/// <para>
/// The strongest check available for the reshape stage, and the only one that
/// exercises the whole path at once: the assembler resolving positions out of the
/// pool, the GLB writer laying them down, the GLB reader picking them up, the
/// edit mapping them back to their slots, and the cameldata writer serializing
/// the result. A fault anywhere in that chain moves a byte.
/// </para>
/// <para>
/// It is what makes the mapping a property rather than a claim. Vertex <em>i</em>
/// of <c>mode3-record-N</c> is local identifier <em>i</em> of record N; if that
/// were off by one anywhere, positions would land in the wrong slots and the file
/// would differ. No fixture can say this, because the thing being checked is the
/// agreement between two halves over real data.
/// </para>
/// <para>
/// Skipped unless <c>PERIANTH_CORPUS</c> is set, so the default suite stays
/// asset-free.
/// </para>
/// </remarks>
public sealed class GeometryIdentityTests(ITestOutputHelper output)
{
    private const string CorpusVariable = "PERIANTH_CORPUS";
    // The whole corpus, not a sample. 40 covered the direct path amply but held
    // only 12 of the 153 indexed records, and those are the newest write path
    // and the one with two arrays to get wrong. Four minutes, behind a variable
    // the default suite does not set.
    private const int ModelLimit = 400;

    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void A_model_exported_and_imported_unchanged_writes_the_cameldata_it_started_with()
    {
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus models");
            return;
        }

        List<string> failures = [];
        int models = 0;
        int identical = 0;
        long parts = 0;
        long slots = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mmb", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string companion = Path.ChangeExtension(path, ".cameldata");
            if (!File.Exists(companion) || models >= ModelLimit)
            {
                continue;
            }

            if (Read(path) is not { } model || Read(companion) is not { } camel)
            {
                continue;
            }

            Result<MmbModel> mmb = MmbReader.Read(model);
            Result<CameldataFile> cameldata = CameldataReader.Read(camel);
            if (mmb.IsRefused || cameldata.IsRefused || cameldata.Value is not Mode3Cameldata mode3)
            {
                continue;
            }

            Result<GeometryModel> assembled = GeometryAssembler.Assemble(mmb.Value, cameldata.Value);
            if (assembled.IsRefused)
            {
                continue;
            }

            models++;
            string name = Path.GetFileName(path);

            Result<byte[]> glb = GlbWriter.Write(assembled.Value, new GlbWriteOptions());
            if (glb.IsRefused)
            {
                failures.Add($"{name}: export refused: {glb.Refusal.Message}");
                continue;
            }

            Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb.Value);
            if (meshes.IsRefused)
            {
                failures.Add($"{name}: reading the GLB back refused: {meshes.Refusal.Message}");
                continue;
            }

            Result<GeometryEditResult> edited = GeometryEdit.Reshape(
                mmb.Value, mode3, [.. meshes.Value.Select(m => new EditedPart(m.Name, m.Positions, m.PoolSlots, m.Indices))]);
            if (edited.IsRefused)
            {
                failures.Add($"{name}: reshaping refused: {edited.Refusal.Message}");
                continue;
            }

            parts += edited.Value.Parts;
            slots += edited.Value.Slots;

            // Nothing was edited, so nothing may have moved. This catches a
            // rounding fault the byte comparison would also catch, but says which
            // half is wrong rather than only that they disagree.
            if (edited.Value.Slots != 0 || edited.Value.Depths != 0)
            {
                failures.Add(
                    $"{name}: an unedited round trip moved {edited.Value.Slots} slots and {edited.Value.Depths} depths");
                continue;
            }

            Result<byte[]> written = CameldataWriter.Write(edited.Value.Cameldata);
            if (written.IsRefused)
            {
                failures.Add($"{name}: writing refused: {written.Refusal.Message}");
                continue;
            }

            if (written.Value.AsSpan().SequenceEqual(File.ReadAllBytes(companion)))
            {
                identical++;
            }
            else
            {
                failures.Add($"{name}: the cameldata came back different after an unedited round trip");
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{models} models round-tripped, {identical} byte-identical; {parts} parts, {slots} slots moved (0 expected)"));

        Assert.True(models > 0, $"no model pairs found under {root}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} models did not survive an unedited round trip" +
                string.Concat(failures.Take(10).Select(f => Environment.NewLine + "  " + f)));
        }

        Assert.Equal(models, identical);
    }

    [Fact]
    public void A_model_replaced_with_the_geometry_it_already_had_comes_back_byte_for_byte()
    {
        // The same oracle one rung up, and it covers strictly more: the MMB is
        // written now, the local identifiers are rebuilt from scratch by welding
        // the positions, and the packed depth stream is written a field at a
        // time. If the weld ordered identifiers differently from the file, or the
        // bit writer disagreed with the reader by one bit, these bytes would
        // differ.
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus models");
            return;
        }

        List<string> failures = [];
        int models = 0;
        int identical = 0;
        int skippedParts = 0;
        int streamed = 0;
        int indexedParts = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mmb", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string companion = Path.ChangeExtension(path, ".cameldata");
            if (!File.Exists(companion) || models >= ModelLimit)
            {
                continue;
            }

            if (Read(path) is not { } modelFile || Read(companion) is not { } camel)
            {
                continue;
            }

            Result<MmbModel> mmb = MmbReader.Read(modelFile);
            Result<CameldataFile> cameldata = CameldataReader.Read(camel);
            if (mmb.IsRefused || cameldata.IsRefused || cameldata.Value is not Mode3Cameldata mode3)
            {
                continue;
            }

            Result<GeometryModel> assembled = GeometryAssembler.Assemble(mmb.Value, cameldata.Value);
            if (assembled.IsRefused)
            {
                continue;
            }

            Result<byte[]> glb = GlbWriter.Write(assembled.Value, new GlbWriteOptions());
            Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb.Value);
            if (glb.IsRefused || meshes.IsRefused)
            {
                continue;
            }

            // Two populations the operation refuses, left out here rather than
            // counted as failures -- and counted, so the exclusions stay visible
            // rather than quietly shrinking what this test covers.
            //
            // Feeding every mesh at once is what this test does and what nobody
            // else does: an author names the parts they want replaced. So one
            // unreplaceable part must not be read as the model being
            // unreplaceable.
            HashSet<string> carried = [];
            for (int i = 0; i < mode3.Constants.Length; i++)
            {
                if (mode3.Constants[i].UsesUnifiedUv0)
                {
                    carried.Add(string.Create(CultureInfo.InvariantCulture, $"mode3-record-{i}"));
                }
            }

            for (int i = 0; i < mmb.Value.Parts.Length; i++)
            {
                // Indexed parts are no longer left out: they are written by
                // the second payload path, and this is the oracle for it.
                MmbGeometryDescriptor descriptor = mmb.Value.Parts[i].Descriptor;
                long identifiers = (long)descriptor.VertexCount * sizeof(ushort);
                long end = descriptor.IsIndexed ? descriptor.IndexOffset : descriptor.PayloadLength;
                if (end != identifiers)
                {
                    carried.Add(string.Create(CultureInfo.InvariantCulture, $"mode3-record-{i}"));
                    streamed++;
                }

                if (descriptor.IsIndexed)
                {
                    indexedParts++;
                }
            }

            List<EditedPart> edits = [];
            foreach (GlbMesh mesh in meshes.Value)
            {
                if (carried.Contains(mesh.Name))
                {
                    skippedParts++;
                    continue;
                }

                edits.Add(new EditedPart(mesh.Name, mesh.Positions, default, mesh.Indices));
            }

            if (edits.Count == 0)
            {
                continue;
            }

            models++;
            string name = Path.GetFileName(path);

            Result<GeometryReplacement> replaced = GeometryReplace.Replace(modelFile, mmb.Value, mode3, edits);
            if (replaced.IsRefused)
            {
                failures.Add($"{name}: replacing refused: {replaced.Refusal.Message}");
                continue;
            }

            Result<byte[]> written = CameldataWriter.Write(replaced.Value.Cameldata);
            if (written.IsRefused)
            {
                failures.Add($"{name}: writing refused: {written.Refusal.Message}");
                continue;
            }

            bool modelSame = replaced.Value.Model.AsSpan().SequenceEqual(File.ReadAllBytes(path));
            bool cameldataSame = written.Value.AsSpan().SequenceEqual(File.ReadAllBytes(companion));

            if (modelSame && cameldataSame)
            {
                identical++;
            }
            else
            {
                failures.Add(
                    $"{name}: replacing with its own geometry changed the " +
                    (modelSame ? "cameldata" : cameldataSame ? "model" : "model and the cameldata"));
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{models} models replaced with their own geometry, {identical} byte-identical; {indexedParts} indexed parts written, {skippedParts} left out, {streamed} of them for carrying a per-vertex stream"));

        Assert.True(models > 0, $"no model pairs found under {root}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} models did not survive being replaced by themselves" +
                string.Concat(failures.Take(10).Select(f => Environment.NewLine + "  " + f)));
        }

        Assert.Equal(models, identical);
    }

    [Fact]
    public void An_unedited_model_taken_through_the_chooser_is_read_as_a_reshape_throughout()
    {
        // What the two tests above cannot say, because each is told which
        // operation to run. Here nothing is told: every mesh goes through the
        // predicate that decides, over real arrangements rather than a fixture's
        // three vertices.
        //
        // The claim is exact, and both halves of it matter. Every part must be
        // read as keeping its corners -- an unedited model has changed nothing,
        // so a part routed to the rebuild would mean the predicate reads a
        // shipped arrangement as torn. And the model must come back byte for
        // byte, which is the reshape's whole distinction: it writes no payload,
        // so a mod carries the game's own MMB rather than one this rewrote.
        string root = Environment.GetEnvironmentVariable(CorpusVariable) ?? string.Empty;
        if (root.Length == 0)
        {
            Assert.Skip($"set {CorpusVariable} to read the corpus models");
            return;
        }

        List<string> failures = [];
        int models = 0;
        long reshaped = 0;

        foreach (string path in Directory
                     .EnumerateFiles(root, "*.mmb", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string companion = Path.ChangeExtension(path, ".cameldata");
            if (!File.Exists(companion) || models >= ModelLimit)
            {
                continue;
            }

            if (Read(path) is not { } modelFile || Read(companion) is not { } camel)
            {
                continue;
            }

            Result<MmbModel> mmb = MmbReader.Read(modelFile);
            Result<CameldataFile> cameldata = CameldataReader.Read(camel);
            if (mmb.IsRefused || cameldata.IsRefused || cameldata.Value is not Mode3Cameldata)
            {
                continue;
            }

            Result<GeometryModel> assembled = GeometryAssembler.Assemble(mmb.Value, cameldata.Value);
            if (assembled.IsRefused)
            {
                continue;
            }

            Result<byte[]> glb = GlbWriter.Write(assembled.Value, new GlbWriteOptions());
            Result<ImmutableArray<GlbMesh>> meshes = GlbReader.Read(glb.Value);
            if (glb.IsRefused || meshes.IsRefused)
            {
                continue;
            }

            models++;
            string name = Path.GetFileName(path);

            Result<GeometryImportResult> applied = GeometryImport.Apply(
                modelFile,
                mmb.Value,
                cameldata.Value,
                [.. meshes.Value.Select(m => new EditedPart(m.Name, m.Positions, m.PoolSlots, m.Indices))]);

            if (applied.IsRefused)
            {
                failures.Add($"{name}: importing refused: {applied.Refusal.Message}");
                continue;
            }

            GeometryImportResult edit = applied.Value;
            reshaped += edit.Reshaped;

            if (edit.Rebuilt != 0)
            {
                failures.Add($"{name}: {edit.Rebuilt} unedited parts were read as needing to be redrawn");
                continue;
            }

            if (edit.Moved)
            {
                failures.Add($"{name}: an unedited model reported {edit.Slots} slots and {edit.Depths} depths moved");
                continue;
            }

            if (!edit.Model.AsSpan().SequenceEqual(File.ReadAllBytes(path)))
            {
                failures.Add($"{name}: the model came back different though no part was redrawn");
            }
        }

        _output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{models} models through the chooser, {reshaped} parts reshaped, 0 rebuilt (0 expected)"));

        Assert.True(models > 0, $"no model pairs found under {root}");

        if (failures.Count > 0)
        {
            Assert.Fail(
                $"{failures.Count} models were not read as unedited" +
                string.Concat(failures.Take(10).Select(f => Environment.NewLine + "  " + f)));
        }
    }

    private static SourceFile? Read(string path)
    {
        Result<SourceFile> source = SourceFileReader.Read(path);
        return source.IsSuccess ? source.Value : null;
    }
}
