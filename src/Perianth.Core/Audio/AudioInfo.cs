namespace Perianth.Core.Audio;

/// <summary>
/// A decoded speech WAV and the facts a caller reports about it.
/// </summary>
/// <param name="SourceName">The WEM file name the audio was decoded from.</param>
/// <param name="Locale">The voice locale segment of its path, or empty.</param>
/// <param name="Wav">The complete uncompressed WAV bytes, to publish beside the GLB.</param>
/// <param name="Channels">Interleaved channel count.</param>
/// <param name="SampleRate">Frames per second.</param>
/// <param name="SampleCount">Frames.</param>
public sealed record AudioInfo(
    string SourceName,
    string Locale,
    byte[] Wav,
    int Channels,
    int SampleRate,
    int SampleCount)
{
    /// <summary>The clip length in seconds.</summary>
    public double DurationSeconds => (double)SampleCount / SampleRate;
}

/// <summary>A resolved WEM: the file to decode and its voice locale.</summary>
/// <param name="Path">The absolute path of the chosen WEM.</param>
/// <param name="Locale">The voice locale segment of its path, or empty.</param>
public readonly record struct WemSelection(string Path, string Locale);
