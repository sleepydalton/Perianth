using System.Diagnostics;
using System.Globalization;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Wav;

namespace Perianth.Core.Audio;

/// <summary>
/// Decodes one WEM to WAV by invoking the external <c>vgmstream-cli</c> tool.
/// </summary>
/// <remarks>
/// WEM is not decoded internally: the tool is run once as <c>-o out.wav in.wem</c>
/// into a private temporary, its success and its product are checked, and the WAV
/// is read back and validated before anything is published. A non-zero exit, a
/// missing product, or an invalid WAV all refuse rather than publishing partial or
/// wrong audio. The executable is taken as given or found on the PATH.
/// </remarks>
public static class VgmstreamDecoder
{
    /// <summary>Decodes <paramref name="wem"/>, returning the validated WAV and its timing.</summary>
    public static Result<AudioInfo> Decode(WemSelection wem, string? executable)
    {
        string? command = executable ?? Locate("vgmstream-cli");
        if (string.IsNullOrEmpty(command))
        {
            return Refusal.Unsupported("vgmstream-cli was not found; pass --vgmstream-cli or add it to PATH.");
        }

        if (executable is not null && !File.Exists(executable))
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"The vgmstream-cli executable {executable} does not exist."));
        }

        DirectoryInfo scratch;
        try
        {
            scratch = Directory.CreateTempSubdirectory("perianth-wem-");
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"Cannot prepare a temporary directory for audio: {ex.Message}"));
        }

        try
        {
            string wavPath = Path.Combine(scratch.FullName, "out.wav");
            Result<int> run = Run(command, wavPath, wem.Path);
            if (!run.TryGetValue(out int exit, out Refusal? runRefusal))
            {
                return runRefusal;
            }

            if (exit != 0)
            {
                return Refusal.Unsupported(string.Create(
                    CultureInfo.InvariantCulture, $"vgmstream-cli refused {Path.GetFileName(wem.Path)} (exit {exit})."));
            }

            if (!File.Exists(wavPath))
            {
                return Refusal.Malformed("vgmstream-cli completed without producing a WAV.");
            }

            byte[] bytes = File.ReadAllBytes(wavPath);
            Result<WavInfo> info = WavReader.Read(bytes);
            if (!info.TryGetValue(out WavInfo wav, out Refusal? wavRefusal))
            {
                return wavRefusal;
            }

            return Result.Ok(new AudioInfo(
                Path.GetFileName(wem.Path), wem.Locale, bytes, wav.Channels, wav.SampleRate, wav.SampleCount));
        }
        catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
        {
            return Refusal.Resource(string.Create(
                CultureInfo.InvariantCulture, $"Cannot prepare audio: {ex.Message}"));
        }
        finally
        {
            try
            {
                scratch.Delete(recursive: true);
            }
            catch (System.Exception ex) when (ex is IOException or System.UnauthorizedAccessException)
            {
                // A leftover temporary is not worth failing a completed decode.
            }
        }
    }

    private static Result<int> Run(string command, string wavPath, string wemPath)
    {
        ProcessStartInfo start = new()
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(wavPath);
        start.ArgumentList.Add(wemPath);

        try
        {
            using Process process = Process.Start(start) ?? throw new IOException("vgmstream-cli did not start.");
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return Result.Ok(process.ExitCode);
        }
        catch (System.Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or System.InvalidOperationException)
        {
            return Refusal.Unsupported(string.Create(
                CultureInfo.InvariantCulture, $"Cannot run vgmstream-cli: {ex.Message}"));
        }
    }

    /// <summary>
    /// Where the decoder is, if it is anywhere.
    /// </summary>
    /// <remarks>
    /// Public so a front end can say "audio is unavailable" while the options
    /// are still being chosen, rather than letting an export run to completion
    /// and refuse at the last step for want of a program.
    /// </remarks>
    public static string? OnPath() => Locate("vgmstream-cli");

    private static string? Locate(string name)
    {
        string? path = System.Environment.GetEnvironmentVariable("PATH");
        if (path is null)
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
