using System.IO.Compression;
using System.Text.Json;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class EnvironmentBackupServiceTests
{
    [Fact]
    public void Create_DefaultBackup_ExcludesSecretValuesTlsAndProjects()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var config = Path.Combine(root, "config");
            File.WriteAllText(Path.Combine(config, "backup-test.json"), "{\"enabled\":true}");
            File.WriteAllText(
                Path.Combine(config, "secrets.dpapi.json"),
                "{\"api-token\":\"PROTECTED-TOKEN\",\"db-password\":\"PROTECTED-PASSWORD\"}");

            var ssl = Path.Combine(config, "ssl", "ca");
            Directory.CreateDirectory(ssl);
            File.WriteAllText(Path.Combine(ssl, "private.key"), "PRIVATE-MATERIAL");

            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.php"), "<?php echo 'demo';");

            var result = new EnvironmentBackupService(root).Create();

            Assert.False(result.IncludesProjects);
            Assert.Equal(0, result.ProjectCount);
            using var archive = ZipFile.OpenRead(result.ArchivePath);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("config/backup-test.json", names);
            Assert.Contains("metadata/secrets.json", names);
            Assert.Contains("manifest.json", names);
            Assert.DoesNotContain(names, name => name.Contains("secrets.dpapi", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("config/ssl/", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("projects/", StringComparison.OrdinalIgnoreCase));

            var secretMetadata = ReadEntry(archive, "metadata/secrets.json");
            Assert.Contains("api-token", secretMetadata, StringComparison.Ordinal);
            Assert.Contains("db-password", secretMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain("PROTECTED-TOKEN", secretMetadata, StringComparison.Ordinal);
            Assert.DoesNotContain("PROTECTED-PASSWORD", secretMetadata, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_WithProjects_IncludesProjectTrees()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var first = Path.Combine(root, "www", "alpha");
            var second = Path.Combine(root, "www", "beta", "src");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllText(Path.Combine(first, "index.php"), "alpha");
            File.WriteAllText(Path.Combine(second, "app.php"), "beta");

            var result = new EnvironmentBackupService(root).Create(includeProjects: true);

            Assert.True(result.IncludesProjects);
            Assert.Equal(2, result.ProjectCount);
            using var archive = ZipFile.OpenRead(result.ArchivePath);
            Assert.NotNull(archive.GetEntry("projects/alpha/index.php"));
            Assert.NotNull(archive.GetEntry("projects/beta/src/app.php"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_ProducesUniqueAtomicArchives()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = new EnvironmentBackupService(root);

            var first = service.Create();
            var second = service.Create();

            Assert.NotEqual(first.ArchivePath, second.ArchivePath);
            Assert.True(File.Exists(first.ArchivePath));
            Assert.True(File.Exists(second.ArchivePath));
            Assert.Empty(Directory.GetFiles(
                Path.Combine(root, "backups", "environment-backups"),
                ".devbox-environment-*.tmp"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"Missing archive entry: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string TemporaryRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "devbox-environment-backup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
