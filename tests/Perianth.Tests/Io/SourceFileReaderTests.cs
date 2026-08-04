using System;
using System.IO;
using Perianth.Formats.Diagnostics;
using Perianth.Formats.Io;
using Xunit;

namespace Perianth.Tests.Io;

public sealed class SourceFileReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-tests-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void A_file_reads_back_byte_for_byte()
    {
        byte[] content = [0x00, 0x7F, 0x80, 0xFF, 0x01];
        string path = Write("input.bin", content);

        Result<SourceFile> result = SourceFileReader.Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal(content, result.Value.Bytes.ToArray());
        Assert.Equal(5, result.Value.Length);
    }

    [Fact]
    public void The_callers_own_spelling_of_the_path_is_kept()
    {
        // Provenance, not cosmetics: a future writer needs the spelling it was
        // given, and nothing downstream should have to reconstruct it.
        string path = Write("input.bin", [0x01]);
        string spelled = Path.Combine(_directory.FullName, ".", "input.bin");

        Result<SourceFile> result = SourceFileReader.Read(spelled);

        Assert.True(result.IsSuccess);
        Assert.Equal(spelled, result.Value.Path, StringComparer.Ordinal);
        Assert.NotEqual(path, result.Value.Path, StringComparer.Ordinal);
    }

    [Fact]
    public void An_empty_file_reads_as_no_bytes_rather_than_refusing()
    {
        // Emptiness is a grammar's problem, not the reader's. Refusing here
        // would report the wrong kind for what is about to be a malformed file.
        string path = Write("empty.bin", []);

        Result<SourceFile> result = SourceFileReader.Read(path);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Length);
    }

    [Fact]
    public void A_missing_file_is_a_resource_refusal()
    {
        Result<SourceFile> result = SourceFileReader.Read(Path.Combine(_directory.FullName, "absent.bin"));

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);
        Assert.Equal(DiagnosticIds.ResourceMissing, result.Refusal.DiagnosticId);

        // Kind and identifier alone do not distinguish "absent" from "present
        // but unreadable", and letting the open call discover absence would
        // report the latter for the former. The message is the only place that
        // difference survives, so it is asserted here.
        Assert.Contains("does not exist", result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_directory_is_a_resource_refusal_rather_than_a_read()
    {
        Result<SourceFile> result = SourceFileReader.Read(_directory.FullName);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);
        Assert.Equal(DiagnosticIds.ResourceMissing, result.Refusal.DiagnosticId);
    }

    [Fact]
    public void An_empty_path_is_a_resource_refusal_and_a_null_path_is_a_fault()
    {
        Result<SourceFile> result = SourceFileReader.Read("");

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);

        // A null path cannot come from a file or a user; it is a bug in a caller.
        Assert.Throws<ArgumentNullException>(() => SourceFileReader.Read(null!));
    }

    [Fact]
    public void The_refusal_names_the_file_it_could_not_read()
    {
        string path = Path.Combine(_directory.FullName, "absent.bin");

        Result<SourceFile> result = SourceFileReader.Read(path);

        Assert.Contains(path, result.Refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_rewritten_during_the_read_refuses_as_changed()
    {
        string path = Write("racing.bin", [0x01, 0x02, 0x03, 0x04]);

        // Rewrite inside the window between having the bytes and re-examining
        // the file, which is the only place the guard can observe anything.
        Result<SourceFile> result = SourceFileReader.Read(path, () =>
        {
            File.WriteAllBytes(path, [0x09, 0x09]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
        });

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Malformed, result.Refusal.Kind);
        Assert.Equal(DiagnosticIds.InputChangedDuringRead, result.Refusal.DiagnosticId);
    }

    [Fact]
    public void A_file_touched_but_not_rewritten_during_the_read_still_refuses()
    {
        // Same length, different timestamp. The bytes in hand may well be fine,
        // but they cannot be shown to be, and publishing an export from bytes
        // that never existed together is the outcome being prevented.
        string path = Write("touched.bin", [0x01, 0x02, 0x03, 0x04]);

        Result<SourceFile> result = SourceFileReader.Read(path, () =>
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30)));

        Assert.True(result.IsRefused);
        Assert.Equal(DiagnosticIds.InputChangedDuringRead, result.Refusal.DiagnosticId);
    }

    [Fact]
    public void A_file_deleted_during_the_read_refuses_as_changed()
    {
        string path = Write("vanishing.bin", [0x01, 0x02]);

        Result<SourceFile> result = SourceFileReader.Read(path, () => File.Delete(path));

        Assert.True(result.IsRefused);
        Assert.Equal(DiagnosticIds.InputChangedDuringRead, result.Refusal.DiagnosticId);
    }

    [Fact]
    public void An_untouched_file_passes_the_change_guard()
    {
        // The negative case matters as much: a guard that refused every read
        // would pass every test above and be useless.
        string path = Write("stable.bin", [0x01, 0x02, 0x03, 0x04]);

        Result<SourceFile> result = SourceFileReader.Read(path, () => { });

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1024, true)]
    [InlineData((long)int.MaxValue, true)]
    [InlineData((long)int.MaxValue + 1, false)]
    [InlineData(-1, false)]
    public void A_file_larger_than_a_span_can_index_has_no_buffer(long length, bool expected)
    {
        Assert.Equal(expected, SourceFileReader.TryBufferSize(length, out int size));
        Assert.Equal(expected ? length : 0, size);
    }

    [Fact]
    public void A_snapshot_matches_only_an_identical_one()
    {
        DateTime when = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        FileSnapshot snapshot = new(true, 100, when);

        Assert.True(snapshot.Matches(new FileSnapshot(true, 100, when)));
        Assert.False(snapshot.Matches(new FileSnapshot(true, 101, when)));
        Assert.False(snapshot.Matches(new FileSnapshot(true, 100, when.AddTicks(1))));
        Assert.False(snapshot.Matches(new FileSnapshot(false, 100, when)));
        Assert.False(new FileSnapshot(false, 0, default).Matches(snapshot));
    }

    private string Write(string name, byte[] content)
    {
        string path = Path.Combine(_directory.FullName, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
