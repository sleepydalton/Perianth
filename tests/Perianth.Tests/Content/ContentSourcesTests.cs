using System;
using System.IO;
using Perianth.Core.Content;
using Perianth.Formats.Diagnostics;
using Perianth.Tests.Sdf;
using Xunit;

namespace Perianth.Tests.Content;

public sealed class ContentSourcesTests : IDisposable
{
    private readonly DirectoryInfo _loose = Directory.CreateTempSubdirectory("perianth-loose-");
    private readonly DirectoryInfo _sdf = Directory.CreateTempSubdirectory("perianth-sdfroot-");

    public void Dispose()
    {
        _loose.Delete(recursive: true);
        _sdf.Delete(recursive: true);
    }

    [Fact]
    public void A_loose_file_is_read_when_it_is_present()
    {
        byte[] payload = [1, 2, 3, 4];
        WriteLoose("tex/one.dds", payload);

        using ContentSources sources = new(_loose.FullName, null);
        Assert.Equal(payload, ReadPresent(sources, "tex/one.dds"));
    }

    [Fact]
    public void Loose_content_takes_precedence_over_the_archives()
    {
        byte[] looseBytes = [10, 20, 30];
        byte[] archiveBytes = [40, 50, 60];
        WriteLoose("tex/one.dds", looseBytes);
        WriteArchive("tex/one.dds", archiveBytes);

        using ContentSources sources = new(_loose.FullName, _sdf.FullName);
        Assert.Equal(looseBytes, ReadPresent(sources, "tex/one.dds"));
    }

    [Fact]
    public void The_archive_is_consulted_only_when_the_loose_path_is_absent()
    {
        byte[] archiveBytes = [7, 7, 7];
        WriteArchive("tex/only.dds", archiveBytes);

        using ContentSources sources = new(_loose.FullName, _sdf.FullName);
        Assert.Equal(archiveBytes, ReadPresent(sources, "tex/only.dds"));
    }

    [Fact]
    public void A_path_no_source_holds_is_absent_rather_than_a_refusal()
    {
        WriteLoose("tex/one.dds", [1]);
        WriteArchive("tex/two.dds", [2]);

        using ContentSources sources = new(_loose.FullName, _sdf.FullName);
        Result<byte[]?> result = sources.Read("tex/missing.dds");

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public void A_loose_texture_reached_through_a_symlink_out_of_the_root_refuses()
    {
        // Falling back to the archives here would export something other than
        // what the link pointed at, so it is a refusal, not an absence.
        byte[] outsideBytes = [9, 9, 9];
        string outside = Path.Combine(_sdf.FullName, "outside.dds");
        File.WriteAllBytes(outside, outsideBytes);

        string linkDir = Path.Combine(_loose.FullName, "tex");
        Directory.CreateDirectory(linkDir);
        string link = Path.Combine(linkDir, "one.dds");

        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("symbolic links are not available in this environment");
            return;
        }

        using ContentSources sources = new(_loose.FullName, _sdf.FullName);
        Result<byte[]?> result = sources.Read("tex/one.dds");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
    }

    [Fact]
    public void No_source_at_all_reports_absence()
    {
        using ContentSources sources = new(null, null);
        Assert.False(sources.HasAny);

        Result<byte[]?> result = sources.Read("tex/one.dds");
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    private void WriteLoose(string normalizedPath, byte[] bytes)
    {
        string full = _loose.FullName;
        foreach (string component in normalizedPath.Split('/'))
        {
            full = Path.Combine(full, component);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private void WriteArchive(string normalizedPath, byte[] bytes)
    {
        SdfContainerBuilder container = new();
        long offset = container.AppendToArchive(bytes);
        container.Index = new SdfIndexBuilder()
            .Literal(normalizedPath)
            .Terminal(chunkCount: 1)
            .Chunk(bytes.Length, offset)
            .Build();
        container.Write(_sdf.FullName);
    }

    private static byte[] ReadPresent(ContentSources sources, string path)
    {
        Result<byte[]?> result = sources.Read(path);
        Assert.True(result.IsSuccess, result.IsRefused ? result.Refusal.Message : "no outcome");
        Assert.NotNull(result.Value);
        return result.Value;
    }
}
