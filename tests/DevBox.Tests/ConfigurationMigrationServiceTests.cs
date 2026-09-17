using System.Text.Json;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ConfigurationMigrationServiceTests
{
    [Fact]
    public void EnsureInitialized_AdoptsLegacyConfigurationAndCreatesBackup()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "sites.json"), "[]");
            File.WriteAllText(Path.Combine(config, "services.json"), "[]");

            RuntimeLayout.EnsureInitialized(root);

            var manifestPath = Path.Combine(config, "schema-versions.json");
            Assert.True(File.Exists(manifestPath));
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.Equal(1, manifest.RootElement.GetProperty("schemaVersion").GetInt32());
            var files = manifest.RootElement.GetProperty("files");
            Assert.Equal(1, files.GetProperty("sites.json").GetInt32());
            Assert.Equal(1, files.GetProperty("services.json").GetInt32());
            Assert.Equal(1, files.GetProperty("appsettings.json").GetInt32());

            var backupBase = Path.Combine(root, "backups", "configuration-migrations");
            var backup = Assert.Single(Directory.GetDirectories(backupBase));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(backup, "config", "sites.json")));
            Assert.Equal("[]", File.ReadAllText(Path.Combine(backup, "config", "services.json")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void EnsureMigrated_IsIdempotentAndDoesNotCreateRepeatedBackups()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "sites.json"), "[]");

            RuntimeLayout.EnsureInitialized(root);
            var backupBase = Path.Combine(root, "backups", "configuration-migrations");
            var firstBackupCount = Directory.GetDirectories(backupBase).Length;

            RuntimeLayout.EnsureInitialized(root);

            Assert.Equal(firstBackupCount, Directory.GetDirectories(backupBase).Length);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void EnsureMigrated_RejectsConfigurationCreatedByNewerDevBox()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(
                Path.Combine(config, "schema-versions.json"),
                """
                {
                  "schemaVersion": 1,
                  "files": {
                    "sites.json": 2
                  }
                }
                """);

            var error = Assert.Throws<InvalidDataException>(() => ConfigurationMigrationService.EnsureMigrated(root));

            Assert.Contains("supports up to version 1", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void EnsureMigrated_InvalidManifestFailsClosedWithoutOverwritingIt()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            var manifestPath = Path.Combine(config, "schema-versions.json");
            const string invalid = "{ not-json";
            File.WriteAllText(manifestPath, invalid);

            Assert.Throws<InvalidDataException>(() => ConfigurationMigrationService.EnsureMigrated(root));

            Assert.Equal(invalid, File.ReadAllText(manifestPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "devbox-config-migrations", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }
}
