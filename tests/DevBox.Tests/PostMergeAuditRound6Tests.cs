using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound6Tests
{
    [Fact]
    public void ProjectStackProfiles_ConcurrentWritersDoNotLoseUpdates()
    {
        var root = NewRoot();
        try
        {
            Parallel.For(0, 24, index =>
            {
                var key = $"round6-{index:D2}";
                new ProjectStackProfileService(root).SaveCustomProfile(new ProjectStackProfile(
                    key,
                    key,
                    ProjectKind.EmptyPhp,
                    null,
                    null,
                    "none",
                    false,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    "round 6 concurrency regression"));
            });

            var keys = new ProjectStackProfileService(root).GetProfiles()
                .Select(profile => profile.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < 24; index++)
                Assert.Contains($"round6-{index:D2}", keys);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ProjectStackProfiles_NullCollectionsAreReportedAsInvalidData()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "project-profiles.json"), """
            [{
              "Key":"broken",
              "DisplayName":"Broken",
              "Kind":"EmptyPhp",
              "DatabaseEngine":"none",
              "Https":false,
              "Addons":null,
              "Services":[]
            }]
            """);

            Assert.Throws<InvalidDataException>(() => new ProjectStackProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void AddonMarketplace_SaveLocalCatalogPersistsRelativePaths()
    {
        var root = NewRoot();
        try
        {
            var addons = new AddonCatalog(root).GetAddons();
            using var marketplace = new AddonMarketplaceService(root);

            marketplace.SaveLocalCatalog(addons);

            var path = Path.Combine(root, "config", "addons.local.json");
            Assert.True(File.Exists(path));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var item = document.RootElement.EnumerateArray().Single();
            var install = item.GetProperty("installRelativePath").GetString()!;
            var entryPoint = item.GetProperty("entryPointRelativePath").GetString()!;
            Assert.False(Path.IsPathRooted(install));
            Assert.False(Path.IsPathRooted(entryPoint));
            Assert.Equal("www/phpmyadmin", install.Replace('\\', '/'));
            Assert.Equal("www/phpmyadmin/index.php", entryPoint.Replace('\\', '/'));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void TaskCenter_CorruptHistoryIsQuarantinedInsteadOfBreakingStartup()
    {
        var root = NewRoot();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            var history = Path.Combine(logs, "task-center-history.json");
            File.WriteAllText(history, "{ not valid json");

            using var center = new PlatformTaskCenter(root);

            Assert.Empty(center.GetTasks());
            Assert.False(File.Exists(history));
            Assert.Single(Directory.GetFiles(logs, "task-center-history.json.invalid-*.bak"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task RuntimeImport_ActivationFailureRollsBackNewVersion()
    {
        var root = NewRoot();
        try
        {
            var archivePath = Path.Combine(root, "runtime.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("tool.exe");
                await using var stream = entry.Open();
                await stream.WriteAsync("fixture"u8.ToArray());
            }
            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            var runtimeRoot = Path.Combine(root, "runtime", "fixture");
            Directory.CreateDirectory(runtimeRoot);
            File.WriteAllText(Path.Combine(runtimeRoot, "current"), "blocks current directory activation");
            var package = new RuntimePackageEntry
            {
                Key = "fixture",
                DisplayName = "Fixture",
                Version = "1.0.0",
                Architecture = "any",
                ExecutableRelativePath = "tool.exe"
            };

            using var service = new RuntimePlatformService(root);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                service.ImportLocalArchiveAsync(package, archivePath, sha, activate: true));

            Assert.False(Directory.Exists(Path.Combine(runtimeRoot, "1.0.0")));
            Assert.True(File.Exists(Path.Combine(runtimeRoot, "current")));
        }
        finally { TryDelete(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
