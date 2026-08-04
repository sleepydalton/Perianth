using System;
using System.Collections.Generic;
using System.Numerics;

namespace Perianth.Tests.Cameldata;

/// <summary>
/// Builds synthetic cameldata bytes from the field layouts in specification
/// sections 5.2 and 5.3.
/// </summary>
internal sealed class CameldataBuilder
{
    public int Mode { get; set; } = 2;

    public int Flags { get; set; }

    /// <summary>Replaces the computed header word entirely.</summary>
    public uint? HeaderWord { get; set; }

    /// <summary>Declared constant count, which may disagree with what is written.</summary>
    public uint? DeclaredConstantCount { get; set; }

    public int ConstantCount { get; set; } = 1;

    public uint[] BezierWords { get; set; } = [];

    /// <summary>The very first float of the first constant, so it can be made non-finite.</summary>
    public float FirstFloat { get; set; } = 1f;

    /// <summary>Packed flags written into every mode-3 constant.</summary>
    public uint PackedFlags { get; set; }

    public Vector3[] Positions { get; set; } = [new(1, 2, 3)];

    /// <summary>Declared mode-2 position count, which may disagree with what is written.</summary>
    public uint? DeclaredPositionCount { get; set; }

    public Vector2[] Xy { get; set; } = [new(1, 2)];

    public float[] Z { get; set; } = [3f];

    public uint[] Uv0 { get; set; } = [0x0000_4000];

    public uint[] PackedZ { get; set; } = [0u];

    public byte[] Trailing { get; set; } = [];

    /// <summary>Cuts the finished file down to this many bytes.</summary>
    public int? TruncateTo { get; set; }

    public byte[] Build()
    {
        List<byte> file = [];

        uint header = HeaderWord ?? (uint)(Mode | (Flags << 15));
        AddUInt32(file, header);
        AddUInt32(file, DeclaredConstantCount ?? (uint)ConstantCount);
        AddUInt32(file, (uint)BezierWords.Length);
        foreach (uint word in BezierWords)
        {
            AddUInt32(file, word);
        }

        for (int i = 0; i < ConstantCount; i++)
        {
            if (Mode == 3)
            {
                AddMode3Constant(file, i);
            }
            else
            {
                AddMode2Constant(file, i);
            }
        }

        if (Mode == 3)
        {
            AddUInt32(file, (uint)Xy.Length);
            foreach (Vector2 value in Xy)
            {
                AddSingle(file, value.X);
                AddSingle(file, value.Y);
            }

            AddUInt32(file, (uint)Z.Length);
            foreach (float value in Z)
            {
                AddSingle(file, value);
            }

            AddUInt32(file, (uint)Uv0.Length);
            foreach (uint value in Uv0)
            {
                AddUInt32(file, value);
            }

            AddUInt32(file, (uint)PackedZ.Length);
            foreach (uint value in PackedZ)
            {
                AddUInt32(file, value);
            }
        }
        else
        {
            AddUInt32(file, DeclaredPositionCount ?? (uint)Positions.Length);
            foreach (Vector3 position in Positions)
            {
                AddSingle(file, position.X);
                AddSingle(file, position.Y);
                AddSingle(file, position.Z);
            }
        }

        file.AddRange(Trailing);

        byte[] bytes = [.. file];
        return TruncateTo is int limit && limit < bytes.Length ? bytes[..limit] : bytes;
    }

    private void AddMode2Constant(List<byte> file, int ordinal)
    {
        AddSurface(file, ordinal);
        AddMatrix(file);
        AddSingle(file, 2f);
        AddSingle(file, 0.5f);
        AddTail(file);
    }

    private void AddMode3Constant(List<byte> file, int ordinal)
    {
        AddSurface(file, ordinal);
        AddUInt32(file, 0);
        AddUInt32(file, 0);
        AddUInt32(file, 0);
        AddUInt32(file, PackedFlags);
        AddMatrix(file);
        AddSingle(file, 2f);
        AddSingle(file, 0.5f);
        AddTail(file);
    }

    private void AddSurface(List<byte> file, int ordinal)
    {
        // Surface origin, U and V, then the sixteen uninterpreted bytes at +48.
        AddSingle(file, ordinal == 0 ? FirstFloat : 1f);
        for (int i = 1; i < 12; i++)
        {
            AddSingle(file, i);
        }

        for (int i = 0; i < 16; i++)
        {
            file.Add((byte)(0xA0 + i));
        }
    }

    private static void AddMatrix(List<byte> file)
    {
        for (int i = 0; i < 16; i++)
        {
            AddSingle(file, i);
        }
    }

    private void AddTail(List<byte> file)
    {
        if (Flags == 0)
        {
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            file.Add((byte)(0xE0 + i));
        }
    }

    private static void AddSingle(List<byte> file, float value) =>
        AddUInt32(file, (uint)BitConverter.SingleToInt32Bits(value));

    private static void AddUInt32(List<byte> file, uint value)
    {
        file.Add((byte)(value & 0xFF));
        file.Add((byte)((value >> 8) & 0xFF));
        file.Add((byte)((value >> 16) & 0xFF));
        file.Add((byte)(value >> 24));
    }
}
