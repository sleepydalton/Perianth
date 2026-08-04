using System;
using System.Buffers.Binary;

namespace Perianth.Tests.Dds;

/// <summary>
/// Builds synthetic DDS files so the grammar can be tested without a corpus.
/// </summary>
internal sealed class DdsFileBuilder
{
    private const int HeaderLength = 128;

    public string Magic { get; set; } = "DDS ";

    public uint HeaderSize { get; set; } = 124;

    public uint PixelFormatSize { get; set; } = 32;

    public uint Width { get; set; } = 4;

    public uint Height { get; set; } = 4;

    public uint MipMapCount { get; set; } = 1;

    public uint Caps2 { get; set; }

    public uint PixelFlags { get; set; } = 0x4;

    public string FourCc { get; set; } = "DXT1";

    public uint BitCount { get; set; } = 32;

    /// <summary>Channel masks, which only an uncompressed pixel format reads.</summary>
    public uint RedMask { get; set; }

    public uint GreenMask { get; set; }

    public uint BlueMask { get; set; }

    public uint AlphaMask { get; set; }

    /// <summary>Set to emit a DX10 extension header after the legacy one.</summary>
    public Dx10Extension? Dx10 { get; set; }

    /// <summary>Level-zero payload. Null means "exactly the right length, zeroed".</summary>
    public byte[]? Payload { get; set; }

    /// <summary>Bytes per block, used only to size a null payload.</summary>
    public int BlockBytes { get; set; } = 8;

    public byte[] Build()
    {
        int extension = Dx10 is null ? 0 : 20;
        byte[] payload = Payload ?? new byte[Width / 4 * (Height / 4) * BlockBytes];
        byte[] bytes = new byte[HeaderLength + extension + payload.Length];

        for (int i = 0; i < Magic.Length && i < 4; i++)
        {
            bytes[i] = (byte)Magic[i];
        }

        Write(bytes, 4, HeaderSize);
        Write(bytes, 12, Height);
        Write(bytes, 16, Width);
        Write(bytes, 28, MipMapCount);
        Write(bytes, 76, PixelFormatSize);
        Write(bytes, 80, PixelFlags);

        for (int i = 0; i < FourCc.Length && i < 4; i++)
        {
            bytes[84 + i] = (byte)FourCc[i];
        }

        Write(bytes, 88, BitCount);
        Write(bytes, 92, RedMask);
        Write(bytes, 96, GreenMask);
        Write(bytes, 100, BlueMask);
        Write(bytes, 104, AlphaMask);
        Write(bytes, 112, Caps2);

        if (Dx10 is { } dx10)
        {
            Write(bytes, 128, dx10.DxgiFormat);
            Write(bytes, 132, dx10.ResourceDimension);
            Write(bytes, 140, dx10.ArraySize);
        }

        payload.CopyTo(bytes.AsSpan(HeaderLength + extension));
        return bytes;
    }

    private static void Write(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);

    internal sealed record Dx10Extension(
        uint DxgiFormat = 98,
        uint ResourceDimension = 3,
        uint ArraySize = 1);
}
