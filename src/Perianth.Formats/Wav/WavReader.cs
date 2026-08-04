using System;
using Perianth.Formats.Binary;
using Perianth.Formats.Diagnostics;

namespace Perianth.Formats.Wav;

/// <summary>The timing fields of a decoded WAV: what the sidecar reports.</summary>
/// <param name="Channels">Interleaved channel count.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="SampleCount">Frames, each one sample per channel.</param>
public readonly record struct WavInfo(int Channels, int SampleRate, int SampleCount);

/// <summary>
/// Validates the uncompressed WAV that the external decoder produces.
/// </summary>
/// <remarks>
/// This is not a general RIFF reader. It confirms the one shape the pipeline
/// accepts — a linear-PCM <c>WAVE</c> with sane timing — so an audio sidecar is
/// never published from a file that is compressed, truncated, or nonsensical. The
/// frame count is derived the way the reference does: the data chunk size divided
/// by the channel count times the rounded-up sample width, not the stored block
/// alignment.
/// </remarks>
public static class WavReader
{
    /// <summary>Reads and validates <paramref name="bytes"/> as a linear-PCM WAV.</summary>
    public static Result<WavInfo> Read(System.ReadOnlySpan<byte> bytes)
    {
        SpanReader reader = new(bytes);

        if (!reader.TryReadBytes(4, out System.ReadOnlySpan<byte> riff) || !riff.SequenceEqual("RIFF"u8)
            || !reader.TryReadUInt32(out _)
            || !reader.TryReadBytes(4, out System.ReadOnlySpan<byte> wave) || !wave.SequenceEqual("WAVE"u8))
        {
            return Refusal.Malformed("The decoder did not produce a RIFF/WAVE file.");
        }

        ushort format = 0;
        int channels = 0;
        int sampleRate = 0;
        int sampleWidth = 0;
        long dataSize = -1;

        while (reader.Remaining >= 8)
        {
            if (!reader.TryReadBytes(4, out System.ReadOnlySpan<byte> id)
                || !reader.TryReadUInt32(out uint size)
                || !reader.TryReadBytes(size, out System.ReadOnlySpan<byte> chunk))
            {
                return Refusal.Malformed("The decoder produced a truncated WAV.");
            }

            if (id.SequenceEqual("fmt "u8))
            {
                if (chunk.Length < 16)
                {
                    return Refusal.Malformed("The decoder produced a WAV with a truncated format chunk.");
                }

                SpanReader fmt = new(chunk);
                fmt.TryReadUInt16(out format);
                fmt.TryReadUInt16(out ushort channelCount);
                fmt.TryReadUInt32(out uint rate);
                fmt.TryReadUInt32(out _);           // byte rate
                fmt.TryReadUInt16(out _);           // block align
                fmt.TryReadUInt16(out ushort bits);
                channels = channelCount;
                sampleRate = (int)rate;
                sampleWidth = (bits + 7) / 8;
            }
            else if (id.SequenceEqual("data"u8))
            {
                dataSize = size;
            }

            // Chunks are padded to an even boundary.
            if ((size & 1) == 1 && reader.Remaining >= 1)
            {
                reader.TrySkip(1);
            }
        }

        if (format == 0 || dataSize < 0)
        {
            return Refusal.Malformed("The decoder produced a WAV without a format or data chunk.");
        }

        // Only linear PCM: the reference's WAV reader accepts nothing else, and a
        // compressed sidecar would misreport its own timing.
        if (format != 1)
        {
            return Refusal.Unsupported(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The decoder produced WAV format {format}, which is not linear PCM."));
        }

        int frameSize = channels * sampleWidth;
        if (channels <= 0 || sampleRate <= 0 || frameSize <= 0)
        {
            return Refusal.Malformed("The decoder produced a WAV with invalid timing fields.");
        }

        return Result.Ok(new WavInfo(channels, sampleRate, (int)(dataSize / frameSize)));
    }
}
