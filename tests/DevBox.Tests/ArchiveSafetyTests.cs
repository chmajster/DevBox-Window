using System.IO.Compression;
using System.Net;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ArchiveSafetyTests
{
    [Fact]
    public void ExtractZipSafely_RejectsAlternateDataStreamSyntax()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("package/file.txt:payload");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("blocked");
            }

            Assert.Throws<InvalidDataException>(() =>
                ArchiveSafety.ExtractZipSafely(archivePath, Path.Combine(root, "extract"), 1024 * 1024, 100, "Test"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ExtractZipSafely_RejectsSuspiciousCompressionRatioBeforeExtraction()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "bomb.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("package/large.txt", CompressionLevel.SmallestSize);
                using var stream = entry.Open();
                var block = new byte[8192];
                for (var i = 0; i < 256; i++)
                {
                    stream.Write(block);
                }
            }

            var destination = Path.Combine(root, "extract");
            Assert.Throws<InvalidDataException>(() =>
                ArchiveSafety.ExtractZipSafely(archivePath, destination, 16 * 1024 * 1024, 100, "Test"));
            Assert.False(Directory.Exists(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task DownloadToFileAsync_RejectsOversizedDeclaredContentLength()
    {
        var root = TemporaryRoot();
        try
        {
            using var client = new HttpClient(new OversizedHandler());
            var destination = Path.Combine(root, "payload.zip");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ArchiveSafety.DownloadToFileAsync(client, new Uri("https://example.test/package.zip"), destination, 1024, "Test", CancellationToken.None));
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-archive-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentLength = 2048;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
