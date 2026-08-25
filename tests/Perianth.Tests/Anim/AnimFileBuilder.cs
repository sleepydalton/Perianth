using System;
using System.Collections.Generic;
using System.Text;

namespace Perianth.Tests.Anim;

/// <summary>
/// Builds a minimal but complete ANIM, for tests that need a rig rather than a
/// codec.
/// </summary>
/// <remarks>
/// Every node states nothing, which is the ordinary case — 92.1% of the corpus's
/// setup nodes state no channel at all — so the file is a node table and little
/// else. That is exactly what a caller asking "does this rig declare this node"
/// needs, and nothing more. Invented throughout, as the repository requires.
/// </remarks>
internal static class AnimFileBuilder
{
    /// <summary>A version-14 setup declaring <paramref name="names"/>, all rooted.</summary>
    internal static byte[] Setup(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<byte> bytes = [];
        bytes.AddRange("ANIM"u8);
        AddU32(bytes, 0x0003000e);
        AddU32(bytes, 0x41C00000);          // 24 fps
        AddU32(bytes, 0x3E2AAAAB);
        AddU32(bytes, 1);                   // samples
        AddU32(bytes, 0);                   // working-buffer size, unread here
        AddU32(bytes, 2);
        AddU32(bytes, 5);                   // three-byte packed rotations
        AddU32(bytes, 2);
        AddU32(bytes, (uint)names.Length);  // nodes
        AddU32(bytes, 0);                   // animated: translation, rotation, scale
        AddU32(bytes, 0);
        AddU32(bytes, 0);
        AddU32(bytes, 0);                   // value entries, likewise
        AddU32(bytes, 0);
        AddU32(bytes, 0);
        bytes.Add(1);                       // a PRNT chunk follows
        AddU32(bytes, 0);                   // static values, likewise
        AddU32(bytes, 0);
        AddU32(bytes, 0);
        AddU32(bytes, 0);

        Chunk(bytes, "TYPE", Repeat(5, names.Length));
        Chunk(bytes, "PRNT", U16(Repeat16(0xFFFF, names.Length)));
        Chunk(bytes, "DTRA", []);
        Chunk(bytes, "DROT", []);
        Chunk(bytes, "DSCA", []);
        foreach ((string stream, string data) in new[] { ("TRAI", "TRAD"), ("ROTI", "ROTD"), ("SCAI", "SCAD") })
        {
            Chunk(bytes, stream, U16(Repeat16(0xFFFF, names.Length)));
            Chunk(bytes, data, []);
        }

        List<byte> table = [];
        foreach (string name in names)
        {
            table.AddRange(Encoding.Latin1.GetBytes(name));
            table.Add(0);
        }

        Chunk(bytes, "NAME", [.. table]);
        foreach (string tag in new[] { "PART", "IKEF", "IKEA" })
        {
            Chunk(bytes, tag, U32(0));
        }

        byte[] path = [.. Encoding.Latin1.GetBytes("invented/anm_rig_setup.anim"), 0];
        AddU32(bytes, (uint)path.Length);
        bytes.AddRange(path);

        bytes.Add(0);
        AddU32(bytes, 1);
        AddU32(bytes, 0xFFFFFFFF);
        AddU32(bytes, (uint)names.Length);
        AddU32(bytes, (uint)((names.Length + 7) / 8));
        bytes.AddRange(new byte[(names.Length + 7) / 8]);

        return [.. bytes];
    }

    private static byte[] Repeat(byte value, int count)
    {
        byte[] bytes = new byte[count];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static ushort[] Repeat16(ushort value, int count)
    {
        ushort[] values = new ushort[count];
        Array.Fill(values, value);
        return values;
    }

    private static void Chunk(List<byte> bytes, string tag, byte[] payload)
    {
        bytes.AddRange(Encoding.ASCII.GetBytes(tag));
        bytes.AddRange(payload);
    }

    private static byte[] U16(ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++)
        {
            bytes[i * 2] = (byte)values[i];
            bytes[(i * 2) + 1] = (byte)(values[i] >> 8);
        }

        return bytes;
    }

    private static byte[] U32(uint value) =>
        [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)];

    private static void AddU32(List<byte> bytes, uint value) => bytes.AddRange(U32(value));
}
