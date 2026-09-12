using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeCatalogLayeringTests
{
    [Fact]
    public void ReleaseCatalog_ProvidesVerifiedRemotePackage()
    {
        var root = NewRoot();
        try
        {
            WriteCatalog(
                Path.Combine(root, "config", "runtime-catalog.release.json"),
                "https://release.example.test/mysql.zip",
                'a');

            using var service = new RuntimePlatformService(root);
            var package = service.GetPackage("mysql", "8.4.11");

            Assert.Equal("https://release.example.test/mysql.zip", package.DownloadUrl);
            Assert.Equal(new string('a', 64), package.Sha256);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void UserCatalog_OverridesReleaseCatalog_WithoutModifyingReleaseFile()
    {
        var root = NewRoot();
        try
        {
            var releasePath = Path.Combine(root, "config", "runtime-catalog.release.json");
            WriteCatalog(releasePath, "https://release.example.test/mysql.zip", 'a');
            var releaseBefore = File.ReadAllText(releasePath);

            using var service = new RuntimePlatformService(root);
            service.SaveCatalogEntry(new RuntimePackageEntry
            {
                Key = "mysql",
                DisplayName = "Custom MySQL",
                Version = "8.4.11",
                Architecture = "x64",
                ExecutableRelativePath = "bin/mysqld.exe",
                DownloadUrl = "https://user.example.test/mysql.zip",
                Sha256 = new string('b', 64),
                ArchiveRootDirectory = "mysql-custom",
                Recommended = true
            });

            var package = service.GetPackage("mysql", "8.4.11");

            Assert.Equal("Custom MySQL", package.DisplayName);
            Assert.Equal("https://user.example.test/mysql.zip", package.DownloadUrl);
            Assert.Equal(new string('b', 64), package.Sha256);
            Assert.Equal(releaseBefore, File.ReadAllText(releasePath));
            Assert.True(File.Exists(Path.Combine(root, "config", "runtime-catalog.json")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void GetStatuses_IncludesInstalledKnownVersionMissingFromCatalog()
    {
        var root = NewRoot();
        try
        {
            var installed = Path.Combine(root, "runtime", "php", "7.4.99");
            Directory.CreateDirectory(installed);
            File.WriteAllText(Path.Combine(installed, "php-cgi.exe"), "legacy runtime");

            using var service = new RuntimePlatformService(root);
            var status = Assert.Single(service.GetStatuses("php").Where(item => item.Package.Version == "7.4.99"));

            Assert.True(status.Installed);
            Assert.True(status.Valid);
            Assert.False(status.Active);
            Assert.Equal(RuntimeSupportState.Unknown, status.SupportState);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void WriteCatalog(string path, string url, char hashCharacter)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
[
  {
    "key": "mysql",
    "displayName": "Release MySQL",
    "version": "8.4.11",
    "architecture": "x64",
    "executableRelativePath": "bin/mysqld.exe",
    "downloadUrl": "{{url}}",
    "sha256": "{{new string(hashCharacter, 64)}}",
    "archiveRootDirectory": "mysql-8.4.11-winx64",
    "recommended": true
  }
]
""");
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog-layering-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
