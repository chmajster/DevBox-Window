using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound13Tests
{
    [Fact]
    public async Task ImportLocalArchiveRejectsRuntimeKeyReparsePointAndPreservesExternalFiles()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-runtime-platform-outside", Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(root, "runtime");
        var link = Path.Combine(runtimeRoot, "php");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "keep.txt");
        File.WriteAllText(sentinel, "preserve");
        try
        {
            if (!TryCreateDirectoryLink(link, external))
                return;

            var archivePath = CreateRuntimeArchive(root);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archivePath))).ToLowerInvariant();
            var package = new RuntimePackageEntry
            {
                Key = "php",
                DisplayName = "PHP fixture",
                Version = "9.9.9",
                Architecture = "any",
                ExecutableRelativePath = "php-cgi.exe",
                ArchiveRootDirectory = "package"
            };

            using var service = new RuntimePlatformService(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ImportLocalArchiveAsync(package, archivePath, hash, activate: false));

            Assert.Equal("preserve", File.ReadAllText(sentinel));
            Assert.False(Directory.Exists(Path.Combine(external, "9.9.9")));
        }
        finally
        {
            TryDeleteLink(link);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void ConstructorRejectsConfigReparsePoint()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var config = Path.Combine(root, "config");
        try
        {
            if (!TryCreateDirectoryLink(config, external))
                return;

            Assert.Throws<InvalidOperationException>(() => new RuntimePlatformService(root));
        }
        finally
        {
            TryDeleteLink(config);
            Delete(root);
            Delete(external);
        }
    }

    private static string CreateRuntimeArchive(string root)
    {
        var path = Path.Combine(root, "runtime.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("package/php-cgi.exe");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("fixture");
        return path;
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch { }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round13", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
