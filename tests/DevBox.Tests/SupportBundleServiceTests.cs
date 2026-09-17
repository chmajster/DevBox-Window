using System.IO.Compression;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class SupportBundleServiceTests
{
    [Fact]
    public void Create_RedactsSecretsAndExcludesSensitiveMaterial()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var config = Path.Combine(root, "config");
            File.WriteAllText(
                Path.Combine(config, "appsettings.json"),
                """
                {
                  "apiToken": "top-secret-token",
                  "nested": { "password": "hunter2" },
                  "repository": "https://build-user:git-password@example.test/repo.git",
                  "safeValue": "keep-me"
                }
                """);
            File.WriteAllText(Path.Combine(config, "secrets.dpapi.json"), "DPAPI-SECRET-PAYLOAD");

            var ssl = Path.Combine(config, "ssl", "ca");
            Directory.CreateDirectory(ssl);
            File.WriteAllText(Path.Combine(ssl, "root.pfx"), "PFX-PRIVATE-MATERIAL");

            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "devbox.json"), "{\"token\":\"PROJECT-SECRET\"}");
            File.WriteAllText(Path.Combine(project, "devbox.lock.json"), "{\"secret\":\"LOCK-SECRET\"}");

            var logSecret = "log-password-value";
            var bearerSecret = "abcdefghijklmnopqrstuvwxyz0123456789";
            File.WriteAllText(
                Path.Combine(root, "logs", "application.log"),
                $"root={root}{Environment.NewLine}password={logSecret}{Environment.NewLine}Authorization: Bearer {bearerSecret}{Environment.NewLine}");

            var result = new SupportBundleService(root).Create();

            Assert.True(File.Exists(result.ArchivePath));
            Assert.True(result.SizeBytes > 0);
            Assert.True(result.IncludedFileCount > 0);
            Assert.True(result.IncludedLogCount >= 1);

            using var archive = ZipFile.OpenRead(result.ArchivePath);
            var names = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("manifest.json", names);
            Assert.Contains("diagnostics.json", names);
            Assert.Contains("system.json", names);
            Assert.Contains("services.json", names);
            Assert.Contains("runtime-status.json", names);
            Assert.Contains("database-status.json", names);
            Assert.Contains("config-redacted/appsettings.json", names);
            Assert.Contains("logs/application.log", names);

            Assert.DoesNotContain(names, name => name.Contains("secrets.dpapi", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains(".pfx", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("devbox.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.Contains("devbox.lock.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, name => name.StartsWith("www/", StringComparison.OrdinalIgnoreCase));

            var appSettings = ReadEntry(archive, "config-redacted/appsettings.json");
            Assert.Contains("keep-me", appSettings, StringComparison.Ordinal);
            Assert.Contains("<REDACTED>", appSettings, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret-token", appSettings, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", appSettings, StringComparison.Ordinal);
            Assert.DoesNotContain("git-password", appSettings, StringComparison.Ordinal);

            var log = ReadEntry(archive, "logs/application.log");
            Assert.Contains("<DEVBOX_ROOT>", log, StringComparison.Ordinal);
            Assert.Contains("<REDACTED>", log, StringComparison.Ordinal);
            Assert.DoesNotContain(root, log, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(logSecret, log, StringComparison.Ordinal);
            Assert.DoesNotContain(bearerSecret, log, StringComparison.Ordinal);

            var allText = string.Join("\n", archive.Entries.Select(entry => ReadEntry(archive, entry.FullName)));
            Assert.DoesNotContain("DPAPI-SECRET-PAYLOAD", allText, StringComparison.Ordinal);
            Assert.DoesNotContain("PFX-PRIVATE-MATERIAL", allText, StringComparison.Ordinal);
            Assert.DoesNotContain("PROJECT-SECRET", allText, StringComparison.Ordinal);
            Assert.DoesNotContain("LOCK-SECRET", allText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_TruncatesLargeLogsAndKeepsTail()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var logPath = Path.Combine(root, "logs", "large.log");
            var head = "HEAD-MUST-BE-TRUNCATED";
            var tail = "TAIL-MUST-REMAIN";
            File.WriteAllText(logPath, head + new string('x', 600 * 1024) + tail);

            var result = new SupportBundleService(root).Create();

            using var archive = ZipFile.OpenRead(result.ArchivePath);
            var log = ReadEntry(archive, "logs/large.log");
            Assert.StartsWith("[TRUNCATED TO LAST 524288 BYTES]", log, StringComparison.Ordinal);
            Assert.DoesNotContain(head, log, StringComparison.Ordinal);
            Assert.Contains(tail, log, StringComparison.Ordinal);
            Assert.Equal(1, result.IncludedLogCount);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_LimitsTheNumberOfIncludedLogs()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            for (var index = 0; index < 30; index++)
            {
                var path = Path.Combine(root, "logs", $"log-{index:D2}.log");
                File.WriteAllText(path, $"log {index}");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(index));
            }

            var result = new SupportBundleService(root).Create();

            Assert.Equal(20, result.IncludedLogCount);
            using var archive = ZipFile.OpenRead(result.ArchivePath);
            Assert.Equal(20, archive.Entries.Count(entry => entry.FullName.StartsWith("logs/", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_ContinuesWhenAStatusSectionIsCorrupted()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            File.WriteAllText(Path.Combine(root, "config", "services.json"), "{ invalid-json");

            var result = new SupportBundleService(root).Create();

            using var archive = ZipFile.OpenRead(result.ArchivePath);
            var services = ReadEntry(archive, "services.json");
            Assert.Contains("\"available\": false", services, StringComparison.Ordinal);
            Assert.Contains("\"section\": \"services\"", services, StringComparison.Ordinal);
            Assert.Contains("diagnostics.json", archive.Entries.Select(entry => entry.FullName));
            Assert.Contains("manifest.json", archive.Entries.Select(entry => entry.FullName));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_UsesUniqueAtomicArchives()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);

            var first = new SupportBundleService(root).Create();
            var second = new SupportBundleService(root).Create();

            Assert.NotEqual(first.ArchivePath, second.ArchivePath);
            Assert.True(File.Exists(first.ArchivePath));
            Assert.True(File.Exists(second.ArchivePath));

            var outputRoot = Path.Combine(root, "backups", "support-bundles");
            Assert.Empty(Directory.GetFiles(outputRoot, "*.tmp", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"ZIP entry not found: {name}");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "devbox-support-bundle-tests", Guid.NewGuid().ToString("N"));

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
