using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound11Tests
{
    [Fact]
    public async Task RuntimeManager_RemoveRejectsRuntimeKeyReparsePointAndPreservesExternalData()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-runtime-outside", Guid.NewGuid().ToString("N"));
        var runtimeRoot = Path.Combine(root, "runtime");
        var link = Path.Combine(runtimeRoot, "php");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(Path.Combine(external, "8.4.0"));
        var sentinel = Path.Combine(external, "8.4.0", "keep.txt");
        File.WriteAllText(sentinel, "preserve");
        try
        {
            if (!TryCreateDirectoryLink(link, external))
                return;

            using var manager = new RuntimeManager(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.RemoveAsync("php", "8.4.0"));
            Assert.Equal("preserve", File.ReadAllText(sentinel));
        }
        finally
        {
            TryDeleteLink(link);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void RuntimeManager_GetInstalledRejectsReparseCurrentDirectory()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-runtime-current-outside", Guid.NewGuid().ToString("N"));
        var phpRoot = Path.Combine(root, "runtime", "php");
        var current = Path.Combine(phpRoot, "current");
        Directory.CreateDirectory(phpRoot);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, ".devbox-version"), "8.4.0");
        try
        {
            if (!TryCreateDirectoryLink(current, external))
                return;

            using var manager = new RuntimeManager(root);
            Assert.Throws<InvalidOperationException>(() => manager.GetInstalled("php", "php-cgi.exe"));
        }
        finally
        {
            TryDeleteLink(current);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public async Task DatabaseManager_RejectsCredentialTempDirectoryReparsePoint()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-mysql-temp-outside", Guid.NewGuid().ToString("N"));
        var mysqlBin = Path.Combine(root, "runtime", "mysql", "current", "bin");
        Directory.CreateDirectory(mysqlBin);
        File.WriteAllText(Path.Combine(mysqlBin, "mysql.exe"), "fixture");
        var tmp = Path.Combine(root, "tmp");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(external);
        var link = Path.Combine(tmp, "mysql");
        try
        {
            if (!TryCreateDirectoryLink(link, external))
                return;

            var manager = new DatabaseManager(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ListDatabasesAsync(
                new DatabaseConnectionOptions("127.0.0.1", 3306, "root", "secret")));
            Assert.Empty(Directory.EnumerateFiles(external, "client-*.cnf"));
        }
        finally
        {
            TryDeleteLink(link);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void ProjectManifest_RejectsMalformedTestDomain()
    {
        var root = NewRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            var service = new ProjectWorkspaceService(
                root,
                new SiteManager(root),
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));
            var manifest = new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                "demo",
                "bad..test",
                ProjectKind.EmptyPhp,
                null,
                null,
                "none",
                "demo",
                false,
                Array.Empty<string>());

            Assert.Throws<InvalidDataException>(() => service.SaveManifest(project, manifest));
        }
        finally
        {
            Delete(root);
        }
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
        var root = Path.Combine(Path.GetTempPath(), "devbox-round11", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
