using System;
using System.IO;
using Perianth.Core.Io;
using Perianth.Formats.Diagnostics;
using Xunit;

namespace Perianth.Tests.Io;

public sealed class AtomicFileTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("perianth-atomic-");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void The_bytes_arrive_at_the_destination()
    {
        string path = Path.Combine(_directory.FullName, "out.glb");

        Result<int> result = AtomicFile.Publish(path, [1, 2, 3, 4]);

        Assert.True(result.IsSuccess);
        Assert.Equal(4, result.Value);
        Assert.Equal<byte[]>([1, 2, 3, 4], File.ReadAllBytes(path));
    }

    [Fact]
    public void Publishing_replaces_an_existing_file()
    {
        string path = Path.Combine(_directory.FullName, "out.glb");
        File.WriteAllBytes(path, [9, 9, 9, 9, 9, 9]);

        Assert.True(AtomicFile.Publish(path, [1, 2]).IsSuccess);
        Assert.Equal<byte[]>([1, 2], File.ReadAllBytes(path));
    }

    [Fact]
    public void No_temporary_is_left_behind()
    {
        string path = Path.Combine(_directory.FullName, "out.glb");

        Assert.True(AtomicFile.Publish(path, [1, 2]).IsSuccess);

        Assert.Single(_directory.GetFiles("*", SearchOption.AllDirectories));
    }

    [Fact]
    public void An_unwritable_destination_refuses_and_leaves_what_was_there()
    {
        // A directory cannot be replaced by a file.
        string path = Path.Combine(_directory.FullName, "occupied");
        Directory.CreateDirectory(path);

        Result<int> result = AtomicFile.Publish(path, [1, 2]);

        Assert.True(result.IsRefused);
        Assert.Equal(RefusalKind.Resource, result.Refusal.Kind);
        Assert.True(Directory.Exists(path));
        Assert.Empty(_directory.GetFiles("*", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void The_published_file_is_readable_by_whoever_the_umask_allows()
    {
        // The trap this guards: Path.GetTempFileName creates with mode 0600, and
        // that mode survives the rename, so every export would be owner-only —
        // invisible on the machine that made it. Creating the temporary through
        // a FileStream lets the kernel apply the umask, which is what finished
        // output should carry.
        //
        // The expected mode is taken from a file the runtime creates the same
        // way, so this holds under any umask rather than assuming 0022.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string probePath = Path.Combine(_directory.FullName, "probe");
        using (FileStream probe = new(probePath, FileMode.CreateNew, FileAccess.Write))
        {
            probe.WriteByte(0);
        }

        UnixFileMode expected = File.GetUnixFileMode(probePath);

        string path = Path.Combine(_directory.FullName, "out.glb");
        Assert.True(AtomicFile.Publish(path, [1, 2]).IsSuccess);

        Assert.Equal(expected, File.GetUnixFileMode(path));
    }

    [Fact]
    public void Publishing_nothing_anywhere_is_a_fault()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AtomicFile.Publish(Path.Combine(_directory.FullName, "x"), null!));
        Assert.Throws<ArgumentException>(() => AtomicFile.Publish("  ", [1]));
    }
}
