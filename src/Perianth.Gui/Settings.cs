using System;
using System.IO;
using System.Text.Json;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;

namespace Perianth.Gui;

/// <summary>
/// The few things the window should still know next time it opens.
/// </summary>
/// <remarks>
/// <para>
/// One record, not one class per preference. Everything here is something the
/// user chose by hand and would resent choosing twice: where the game is, where
/// their work goes, which decoder to run, which voice to take, and whether the
/// window is dark.
/// </para>
/// <para>
/// A missing or unreadable file means "no preferences", never an error. These
/// are conveniences; the window opens and works without any of them, and a
/// corrupted file must not be the reason someone cannot start the tool.
/// </para>
/// </remarks>
public sealed record Settings
{
    /// <summary>The folder holding sdf.sdftoc.</summary>
    public string? ArchiveRoot { get; init; }

    /// <summary>Where extractions and exports are written.</summary>
    public string? WorkingFolder { get; init; }

    /// <summary>The vgmstream-cli executable, when it is not on PATH.</summary>
    public string? VgmstreamCli { get; init; }

    /// <summary>Which voice locale to take speech audio from.</summary>
    public string? Locale { get; init; }

    /// <summary>Whether the window was last in its dark theme.</summary>
    public bool Dark { get; init; }

    /// <summary>Where the file lives on this system.</summary>
    /// <remarks>
    /// Under the platform's own configuration directory rather than beside the
    /// executable, so it survives replacing the program and so a read-only
    /// install still has somewhere to write.
    /// </remarks>
    public static string Path
    {
        get
        {
            string configuration = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);

            return System.IO.Path.Combine(configuration, "perianth", "settings.json");
        }
    }

    /// <summary>Reads the settings, or returns empty ones.</summary>
    /// <param name="path">Where to read from; the user's own file by default.</param>
    /// <remarks>
    /// The path is a parameter so that a test can exercise the failure paths —
    /// absent, corrupt, wrong shape — without writing to the preferences of
    /// whoever is running the tests.
    /// </remarks>
    public static Settings Load(string? path = null)
    {
        path ??= Path;

        try
        {
            if (!File.Exists(path))
            {
                return new Settings();
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
            JsonElement root = document.RootElement;

            return new Settings
            {
                ArchiveRoot = Text(root, "archive_root"),
                WorkingFolder = Text(root, "working_folder"),
                VgmstreamCli = Text(root, "vgmstream_cli"),
                Locale = Text(root, "locale"),
                Dark = root.TryGetProperty("dark", out JsonElement dark)
                    && dark.ValueKind == JsonValueKind.True,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable preferences are no preferences. Anything else here
            // would let a stray file stop the window opening.
            return new Settings();
        }
    }

    /// <summary>Writes the settings, and says nothing if it cannot.</summary>
    /// <remarks>
    /// Failing to save a preference is not worth interrupting anyone over, and
    /// there is nothing they could do about it mid-session. The refusal is
    /// returned for a caller that wants it; the window ignores it.
    /// </remarks>
    public Result<int> Save(string? path = null)
    {
        path ??= Path;

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            Write(writer, "archive_root", ArchiveRoot);
            Write(writer, "working_folder", WorkingFolder);
            Write(writer, "vgmstream_cli", VgmstreamCli);
            Write(writer, "locale", Locale);
            writer.WriteBoolean("dark", Dark);
            writer.WriteEndObject();
        }

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal.Resource(
                "The settings directory could not be created.", DiagnosticIds.ResourceMissing);
        }

        return AtomicFile.Publish(path, buffer.ToArray());
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void Write(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
