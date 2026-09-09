using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeManagerTests
{
    [Fact]
    public async Task InstallAsync_VerifiesInstallsAndActivatesRuntime()
    {
        var root = TemporaryRoot();
        try
        {
            var package = CreateArchive(("package/php.exe", "runtime"));
            var checksum = Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            using var client = new HttpClient(new StaticHandler(package));
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "php", "PHP", "8.4.0", "https://example.test/php.zip", checksum, "php.exe", "package");

            await manager.InstallAsync(definition);

            Assert.True(File.Exists(Path.Combine(root, "runtime", "php", "8.4.0", "php.exe")));
            Assert.True(File.Exists(Path.Combine(root, "runtime", "php", "current", "php.exe")));
            var installed = Assert.Single(manager.GetInstalled("php", "php.exe"));
            Assert.True(installed.IsActive);
            Assert.True(installed.IsValid);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_ActivatesBundledRuntimeWithoutDownloading()
    {
        var root = TemporaryRoot();
        try
        {
            var bundledBin = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(bundledBin);
            File.WriteAllText(Path.Combine(bundledBin, "mysqld.exe"), "runtime");

            using var client = new HttpClient(new ThrowingHandler());
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "mysql",
                "MySQL",
                "8.4.11",
                null,
                null,
                Path.Combine("bin", "mysqld.exe"),
                "mysql-8.4.11-winx64");

            await manager.InstallAsync(definition);

            Assert.True(File.Exists(Path.Combine(root, "runtime", "mysql", "current", "bin", "mysqld.exe")));
            var installed = Assert.Single(manager.GetInstalled("mysql", Path.Combine("bin", "mysqld.exe")));
            Assert.True(installed.IsActive);
            Assert.True(installed.IsValid);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_BundledOnlyRuntimeFailsClearlyWhenPayloadIsMissing()
    {
        var root = TemporaryRoot();
        try
        {
            using var client = new HttpClient(new ThrowingHandler());
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "mysql",
                "MySQL",
                "8.4.11",
                null,
                null,
                Path.Combine("bin", "mysqld.exe"),
                "mysql-8.4.11-winx64");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(definition));

            Assert.Contains("bundled runtime is missing", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_RejectsPackageWithWrongHash()
    {
        var root = TemporaryRoot();
        try
        {
            var package = CreateArchive(("package/php.exe", "runtime"));
            using var client = new HttpClient(new StaticHandler(package));
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "php", "PHP", "8.4.0", "https://example.test/php.zip", new string('0', 64), "php.exe", "package");

            await Assert.ThrowsAsync<InvalidDataException>(() => manager.InstallAsync(definition));
            Assert.Empty(manager.GetInstalled("php", "php.exe"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ExtractZipSafely_RejectsTraversal()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escape.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("bad");
            }

            Assert.Throws<InvalidDataException>(() => RuntimeManager.ExtractZipSafely(archivePath, Path.Combine(root, "extract")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static byte[] CreateArchive(params (string Path, string Content)[] files)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(file.Path);
                using var writer = new StreamWriter(entry.Open());
                writer.Write(file.Content);
            }
        }
        return memory.ToArray();
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class StaticHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bundled runtime installation must not use HTTP.");
    }
}
