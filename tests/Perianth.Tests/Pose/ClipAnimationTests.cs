using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using Perianth.Core.Geometry;
using Perianth.Core.Pose;
using Perianth.Formats.Anim;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Pose;

public sealed class ClipAnimationTests : IDisposable
{
    private readonly DirectoryInfo _directory =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"clip-{Guid.NewGuid():N}"));

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_clip_with_no_changing_channels_refuses_rather_than_holding_a_still()
    {
        // The setup rests the node; the clip names it but drives nothing (all
        // sentinels) and its visibility never changes, so there is no animation
        // to emit -- a still is not a clip.
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["root"], scai: [Active], parents: [Root]);
        AnimFile clip = Anim(hierarchy: false, sampleCount: 2, fps: 24,
            names: ["root"], scai: [Active], parents: null);

        GeometryModel model = Model(("root", 0));

        Result<AnimatedScene> result = ClipAnimation.Animate(model, setup, clip);
        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_clip_with_no_samples_refuses()
    {
        AnimFile setup = Anim(hierarchy: true, sampleCount: 1, fps: 24,
            names: ["root"], scai: [Active], parents: [Root]);
        AnimFile clip = Anim(hierarchy: false, sampleCount: 0, fps: 24,
            names: ["root"], scai: [Active], parents: null);

        Assert.True(ClipAnimation.Animate(Model(("root", 0)), setup, clip).IsRefused);
    }

    // --- fixtures ------------------------------------------------------------

    private const int Root = -1;
    private const int Active = 0xFFFF;

    private AnimFile Anim(bool hierarchy, int sampleCount, float fps, string[] names, int[] scai, int[]? parents)
    {
        List<byte> bytes = [.. new byte[0x3C]];
        "ANIM"u8.CopyTo(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bytes)[..4]);
        Write32(bytes, 0x08, BitConverter.SingleToInt32Bits(fps));
        Write32(bytes, 0x10, sampleCount);
        Write32(bytes, 0x1C, 5);
        Write32(bytes, 0x24, names.Length);

        bytes.AddRange(Chunk("SCAI", U16(scai)));
        bytes.AddRange(Chunk("NAME", Names(names)));
        if (parents is not null)
        {
            bytes.AddRange(Chunk("PRNT", U16([.. parents.Select(p => p < 0 ? 0xFFFF : p)])));
        }

        string path = Path.Combine(_directory.FullName, $"a{Guid.NewGuid():N}.anim");
        File.WriteAllBytes(path, [.. bytes]);
        return AnimReader.Read(SourceFileReader.Read(path).Value, hierarchy).Value;
    }

    private static GeometryModel Model(params (string Binding, int Ordinal)[] parts)
    {
        ImmutableArray<GeometryPart> built =
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
        ];

        return new GeometryModel(3, built, false);
    }

    private static byte[] Chunk(string tag, byte[] payload) => [.. Encoding.ASCII.GetBytes(tag), .. payload];

    private static byte[] U16(int[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)(values[i] & 0xFF);
            bytes[(i * 2) + 1] = (byte)((values[i] >> 8) & 0xFF);
        }

        return bytes;
    }

    private static byte[] Names(string[] names)
    {
        List<byte> bytes = [];
        foreach (string name in names)
        {
            bytes.AddRange(Encoding.Latin1.GetBytes(name));
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static void Write32(List<byte> bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value & 0xFF);
        bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
        bytes[offset + 2] = (byte)((value >> 16) & 0xFF);
        bytes[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
