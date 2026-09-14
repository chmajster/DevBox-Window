using System.Buffers.Binary;
using System.IO.Compression;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ArchiveIntegrityTests
{
    [Theory]
    [InlineData(CompressionLevel.NoCompression)]
    [InlineData(CompressionLevel.Optimal)]
    public void Extract_RejectsWrongChecksumEvenWithConsistentSize(CompressionLevel compression)
    {
        using var root = new TemporaryRoot();
        var archivePath = Path.Combine(root.Path, "fixture.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        using (var output = archive.CreateEntry("payload.txt", compression).Open())
            output.Write("123456789"u8);

        // Known CRC32 test vector: changing only CRC leaves both size fields intact.
        using (var archive = ZipFile.OpenRead(archivePath))
            Assert.Equal(0xcbf43926u, archive.Entries[0].Crc32);
        var bytes = File.ReadAllBytes(archivePath);
        var headers = 0;
        for (var index = 0; index <= bytes.Length - 46; index++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
            if (signature == 0x04034b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 14, 4), 0x12345678u);
                headers++;
            }
            else if (signature == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index + 16, 4), 0x12345678u);
                headers++;
            }
        }
        Assert.Equal(2, headers);
        File.WriteAllBytes(archivePath, bytes);
        Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(
            archivePath, Path.Combine(root.Path, "out"), 1024, 10, "checksum fixture"));
    }

    [Fact]
    public void Extract_PreservesValidEmptyBinaryAndTimestampedEntries()
    {
        using var root = new TemporaryRoot();
        var content = new byte[200_000];
        new Random(731).NextBytes(content);
        var archivePath = Path.Combine(root.Path, "fixture.zip");
        var timestamp = new DateTimeOffset(2024, 3, 2, 12, 30, 0, TimeSpan.Zero);
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("empty.txt");
            var binary = archive.CreateEntry("payload.bin", CompressionLevel.Optimal);
            binary.LastWriteTime = timestamp;
            using var output = binary.Open();
            output.Write(content);
        }
        var destination = Path.Combine(root.Path, "out");
        ArchiveSafety.ExtractZipSafely(archivePath, destination, 250_000, 10, "valid fixture");
        Assert.Empty(File.ReadAllBytes(Path.Combine(destination, "empty.txt")));
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(destination, "payload.bin")));
        Assert.Equal(timestamp.DateTime, File.GetLastWriteTime(Path.Combine(destination, "payload.bin")));
    }

    [Theory]
    [InlineData("missing.log")]
    [InlineData("missing/rotated.log")]
    public void LogListing_IgnoresEntriesRemovedDuringRotation(string relative)
    {
        using var root = new TemporaryRoot();
        Assert.False(LogReader.IsRegularLogFile(Path.Combine(root.Path, relative)));
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "devbox-zip-integrity", Guid.NewGuid().ToString("N"));
        public TemporaryRoot() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
