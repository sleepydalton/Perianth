using System;
using System.IO;
using System.Text.Json;
using Perianth.Gui;
using Xunit;

namespace Perianth.Tests.Gui;

/// <summary>
/// Checks that preferences never stop the window opening.
/// </summary>
/// <remarks>
/// Everything stored is a convenience the tool works without, so every failure
/// path has to end in "no preferences" rather than in an error. Each test uses
/// its own file: running the suite must not disturb the preferences of whoever
/// is running it, and tests that shared one real path raced each other.
/// </remarks>
public sealed class SettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-settings-");

    private string Path => System.IO.Path.Combine(_directory.FullName, "settings.json");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void What_was_saved_comes_back()
    {
        Settings written = new()
        {
            ArchiveRoot = "/games/sp/camel/sdf/pc/data",
            WorkingFolder = "/home/someone/work",
            VgmstreamCli = "/opt/vgmstream/vgmstream-cli",
            Locale = "german",
            Dark = true,
        };

        Assert.False(written.Save(Path).IsRefused);

        Settings read = Settings.Load(Path);
        Assert.Equal(written.ArchiveRoot, read.ArchiveRoot);
        Assert.Equal(written.WorkingFolder, read.WorkingFolder);
        Assert.Equal(written.VgmstreamCli, read.VgmstreamCli);
        Assert.Equal("german", read.Locale);
        Assert.True(read.Dark);
    }

    [Fact]
    public void Nothing_saved_yet_is_not_an_error()
    {
        File.Delete(Path);

        Settings read = Settings.Load(Path);

        Assert.Null(read.ArchiveRoot);
        Assert.False(read.Dark);
    }

    [Fact]
    public void A_corrupt_file_reads_as_no_preferences()
    {
        // The one that matters: a stray file must not be the reason someone
        // cannot start the tool.
        File.WriteAllText(Path, "{ this is not json");

        Settings read = Settings.Load(Path);

        Assert.Null(read.ArchiveRoot);
        Assert.Null(read.VgmstreamCli);
    }

    [Fact]
    public void A_file_of_the_wrong_shape_reads_as_no_preferences()
    {
        File.WriteAllText(Path, """{"archive_root": 17, "dark": "yes"}""");

        Settings read = Settings.Load(Path);

        Assert.Null(read.ArchiveRoot);
        Assert.False(read.Dark);
    }

    [Fact]
    public void The_file_is_valid_json_with_the_keys_it_claims()
    {
        new Settings { ArchiveRoot = "/a", Locale = "italian" }.Save(Path);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path));

        Assert.Equal("/a", document.RootElement.GetProperty("archive_root").GetString());
        Assert.Equal("italian", document.RootElement.GetProperty("locale").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("vgmstream_cli").ValueKind);
    }
}
