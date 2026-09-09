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

    [Fact]
    public void GetInstalled_IgnoresRollbackBackupDirectories()
    {
        var root = TemporaryRoot();
        try
        {
            var runtimeRoot = Path.Combine(root, "runtime", "php");
            var installedPath = Path.Combine(runtimeRoot, "8.4.0");
            var versionBackup = Path.Combine(runtimeRoot, "8.3.0.backup-0123456789abcdef");
            var currentBackup = Path.Combine(runtimeRoot, "current.backup-fedcba9876543210");

            Directory.CreateDirectory(installedPath);
            Directory.CreateDirectory(versionBackup);
            Directory.CreateDirectory(currentBackup);
            File.WriteAllText(Path.Combine(installedPath, "php.exe"), "runtime");
            File.WriteAllText(Path.Combine(versionBackup, "php.exe"), "stale");
            File.WriteAllText(Path.Combine(currentBackup, "php.exe"), "stale");

            using var manager = new RuntimeManager(root, new HttpClient(new StaticHandler(Array.Empty<byte>())));
            var installed = manager.GetInstalled("php", "php.exe");

            var runtime = Assert.Single(installed);
            Assert.Equal("8.4.0", runtime.Version);
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
}
