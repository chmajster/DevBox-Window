using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound12Tests
{
    [Theory]
    [InlineData("mysql", "mysql")]
    [InlineData("mysql", "information_schema")]
    [InlineData("mariadb", "sys")]
    [InlineData("postgresql", "postgres")]
    [InlineData("postgresql", "template0")]
    [InlineData("postgresql", "template1")]
    public async Task RestoreRejectsSystemDatabaseBeforeReadingBackup(string engine, string database)
    {
        var root = NewRoot();
        try
        {
            CreateServerFixture(root, engine, "16.0");
            using var service = new DatabaseRuntimeService(root);
            _ = service.Register(engine, "16.0");

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(
                engine,
                "16.0",
                database,
                Path.Combine(root, "missing-backup.sql")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public void ConstructorRejectsReparseConfigDirectory()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-db-runtime-config-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var config = Path.Combine(root, "config");
        try
        {
            if (!TryCreateDirectoryLink(config, external))
                return;

            Assert.Throws<InvalidOperationException>(() => new DatabaseRuntimeService(root));
            Assert.Empty(Directory.EnumerateFiles(external));
        }
        finally
        {
            TryDeleteLink(config);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void RegisterRejectsRuntimeVersionThroughReparsePoint()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-db-runtime-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(external, "bin"));
        File.WriteAllText(Path.Combine(external, "bin", "mysqld.exe"), "fixture");
        var mysqlRoot = Path.Combine(root, "runtime", "mysql");
        Directory.CreateDirectory(mysqlRoot);
        var link = Path.Combine(mysqlRoot, "8.4.0");
        try
        {
            if (!TryCreateDirectoryLink(link, external))
                return;

            using var service = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidOperationException>(() => service.Register("mysql", "8.4.0"));
        }
        finally
        {
            TryDeleteLink(link);
            Delete(root);
            Delete(external);
        }
    }

    private static void CreateServerFixture(string root, string engine, string version)
    {
        var normalized = engine == "postgresql" ? "postgresql" : engine;
        var bin = Path.Combine(root, "runtime", normalized, version, "bin");
        Directory.CreateDirectory(bin);
        var executable = engine == "postgresql" ? "postgres.exe" : "mysqld.exe";
        File.WriteAllText(Path.Combine(bin, executable), "fixture");
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
        var root = Path.Combine(Path.GetTempPath(), "devbox-round12", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
