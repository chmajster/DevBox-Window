using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeCatalogValidationTests
{
    [Fact]
    public void SaveCatalogEntry_RejectsNonHexSha256()
    {
        var root = TemporaryRoot();
        try
        {
            using var service = new RuntimePlatformService(root);
            var package = Package("php", "9.0.0") with
            {
                DownloadUrl = "https://example.test/php.zip",
                Sha256 = new string('z', 64)
            };

            Assert.Throws<InvalidDataException>(() => service.SaveCatalogEntry(package));
            Assert.False(File.Exists(Path.Combine(root, "config", "runtime-catalog.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData("..", "9.0.0", "php-cgi.exe")]
    [InlineData("php", "..", "php-cgi.exe")]
    [InlineData("php", "9.0.0", "../outside.exe")]
    public void SaveCatalogEntry_RejectsTraversal(string key, string version, string executable)
    {
        var root = TemporaryRoot();
        try
        {
            using var service = new RuntimePlatformService(root);
            var package = Package(key, version) with { ExecutableRelativePath = executable };

            Assert.Throws<InvalidDataException>(() => service.SaveCatalogEntry(package));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ImportLocalArchive_ActivationFailure_RollsBackNewVersion()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "runtime.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("package/php-cgi.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("runtime");
            }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath))).ToLowerInvariant();
            var runtimeRoot = Path.Combine(root, "runtime", "php");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(Path.Combine(runtimeRoot, "current"), "blocks activation");

            using var service = new RuntimePlatformService(root);
            var package = Package("php", "9.0.0") with { ArchiveRootDirectory = "package" };

            await Assert.ThrowsAnyAsync<IOException>(() =>
                service.ImportLocalArchiveAsync(package, archivePath, hash, activate: true));

            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "9.0.0")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static RuntimePackageEntry Package(string key, string version) => new()
    {
        Key = key,
        DisplayName = "Fixture",
        Version = version,
        Architecture = "any",
        ExecutableRelativePath = "php-cgi.exe",
        Recommended = false
    };

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
