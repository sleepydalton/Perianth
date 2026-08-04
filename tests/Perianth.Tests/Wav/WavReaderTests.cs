using System;
using System.Collections.Generic;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Wav;
using Xunit;

namespace Perianth.Tests.Wav;

/// <summary>
/// Validates the WAV the external decoder is expected to produce. Every buffer
/// here is assembled by hand from the RIFF layout.
/// </summary>
public sealed class WavReaderTests
{
    [Fact]
    public void A_linear_pcm_wav_reports_its_timing()
    {
        // One channel, 48 kHz, 16-bit, four frames: 8 data bytes / (1 * 2) = 4.
        byte[] wav = Wav(format: 1, channels: 1, rate: 48000, bits: 16, dataBytes: 8);

        WavInfo info = WavReader.Read(wav).Value;

        Assert.Equal(1, info.Channels);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(4, info.SampleCount);
    }

    [Fact]
    public void The_frame_count_divides_by_channels_and_width()
    {
        // Two channels, 16-bit: frame size is 4, so 16 data bytes are 4 frames.
        byte[] wav = Wav(format: 1, channels: 2, rate: 44100, bits: 16, dataBytes: 16);

        Assert.Equal(4, WavReader.Read(wav).Value.SampleCount);
    }

    [Fact]
    public void A_non_pcm_format_is_unsupported()
    {
        byte[] wav = Wav(format: 3, channels: 1, rate: 48000, bits: 32, dataBytes: 8);

        Result<WavInfo> result = WavReader.Read(wav);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Unsupported, result.Refusal.Kind);
    }

    [Fact]
    public void A_buffer_that_is_not_riff_wave_is_malformed()
    {
        Result<WavInfo> result = WavReader.Read([0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B]);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void A_truncated_chunk_is_malformed()
    {
        byte[] wav = Wav(format: 1, channels: 1, rate: 48000, bits: 16, dataBytes: 8);

        // Cut into the data chunk so its declared size overruns the buffer.
        Result<WavInfo> result = WavReader.Read(wav.AsSpan(0, wav.Length - 4));

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    // --- fixtures ------------------------------------------------------------

    private static byte[] Wav(ushort format, ushort channels, uint rate, ushort bits, int dataBytes)
    {
        int blockAlign = channels * (bits / 8);
        List<byte> fmt = [];
        U16(fmt, format);
        U16(fmt, channels);
        U32(fmt, rate);
        U32(fmt, rate * (uint)blockAlign);
        U16(fmt, (ushort)blockAlign);
        U16(fmt, bits);

        List<byte> body = [.. "WAVE"u8];
        Chunk(body, "fmt ", [.. fmt]);
        Chunk(body, "data", new byte[dataBytes]);

        List<byte> file = [.. "RIFF"u8];
        U32(file, (uint)body.Count);
        file.AddRange(body);
        return [.. file];
    }

    private static void Chunk(List<byte> target, string id, byte[] payload)
    {
        target.AddRange(System.Text.Encoding.ASCII.GetBytes(id));
        U32(target, (uint)payload.Length);
        target.AddRange(payload);
        if ((payload.Length & 1) == 1)
        {
            target.Add(0);
        }
    }

    private static void U16(List<byte> target, ushort value)
    {
        target.Add((byte)(value & 0xFF));
        target.Add((byte)(value >> 8));
    }

    private static void U32(List<byte> target, uint value)
    {
        target.Add((byte)(value & 0xFF));
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)((value >> 16) & 0xFF));
        target.Add((byte)(value >> 24));
    }
}
