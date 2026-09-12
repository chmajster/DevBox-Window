from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, got {count}: {old[:120]!r}")
    file.write_text(text.replace(old, new, 1), encoding="utf-8")


# 1) Project stack profile mutations were not serialized across processes/instances,
# so concurrent saves/removes could overwrite each other's updates.
replace_once(
    "src/DevBox.Core/Services/ProjectStackProfileService.cs",
    '''        Validate(profile);\n\n        var profiles = LoadCustomProfiles().ToList();\n''',
    '''        Validate(profile);\n        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));\n\n        var profiles = LoadCustomProfiles().ToList();\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectStackProfileService.cs",
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        var profiles = LoadCustomProfiles().ToList();\n''',
    '''        ArgumentException.ThrowIfNullOrWhiteSpace(key);\n        using var mutationLock = CrossProcessFileLock.Acquire(_profilesPath + ".lock", TimeSpan.FromSeconds(15));\n        var profiles = LoadCustomProfiles().ToList();\n''')
replace_once(
    "src/DevBox.Core/Services/ProjectStackProfileService.cs",
    '''        if (!SafeKeyRegex().IsMatch(profile.Key)) throw new InvalidDataException("Profile key contains unsupported characters.");\n        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100) throw new InvalidDataException("Profile display name is invalid.");\n        if (profile.Kind == ProjectKind.Unknown) throw new InvalidDataException("Profile must select a supported project kind.");\n        if (profile.DatabaseEngine is not ("mysql" or "mariadb" or "postgresql" or "none")) throw new InvalidDataException("Profile database engine is invalid.");\n        if (profile.Addons.Any(addon => !SafeKeyRegex().IsMatch(addon))) throw new InvalidDataException("Profile contains an invalid addon key.");\n        if (profile.Services.Any(service => !SafeKeyRegex().IsMatch(service))) throw new InvalidDataException("Profile contains an invalid managed-service key.");\n''',
    '''        if (!SafeKeyRegex().IsMatch(profile.Key ?? string.Empty)) throw new InvalidDataException("Profile key contains unsupported characters.");\n        if (string.IsNullOrWhiteSpace(profile.DisplayName) || profile.DisplayName.Length > 100) throw new InvalidDataException("Profile display name is invalid.");\n        if (profile.Kind == ProjectKind.Unknown) throw new InvalidDataException("Profile must select a supported project kind.");\n        if (profile.DatabaseEngine is not ("mysql" or "mariadb" or "postgresql" or "none")) throw new InvalidDataException("Profile database engine is invalid.");\n        if (profile.Addons is null || profile.Services is null) throw new InvalidDataException("Profile addon and service collections cannot be null.");\n        if (profile.Addons.Any(addon => !SafeKeyRegex().IsMatch(addon ?? string.Empty))) throw new InvalidDataException("Profile contains an invalid addon key.");\n        if (profile.Services.Any(service => !SafeKeyRegex().IsMatch(service ?? string.Empty))) throw new InvalidDataException("Profile contains an invalid managed-service key.");\n''')

# 2) SaveLocalCatalog serialized AddonDefinition's absolute paths back into a manifest
# that explicitly requires DevBox-relative paths, making the public API fail for
# catalog-derived add-ons.
replace_once(
    "src/DevBox.Core/Services/AddonMarketplaceService.cs",
    '''    private static object ToManifestEntry(AddonDefinition addon) => new\n    {\n        key = addon.Key,\n        displayName = addon.DisplayName,\n        description = addon.Description,\n        installRelativePath = addon.InstallPath.Replace('\\\\', '/'),\n        entryPointRelativePath = addon.EntryPointPath.Replace('\\\\', '/'),\n        localUrl = addon.LocalUrl,\n        requiredPhpExtensions = addon.RequiredPhpExtensions,\n        version = addon.Version,\n        downloadUrl = addon.DownloadUrl,\n        sha256 = addon.Sha256,\n        archiveRootDirectory = addon.ArchiveRootDirectory\n    };\n''',
    '''    private object ToManifestEntry(AddonDefinition addon) => new\n    {\n        key = addon.Key,\n        displayName = addon.DisplayName,\n        description = addon.Description,\n        installRelativePath = Path.GetRelativePath(_rootPath, Path.GetFullPath(addon.InstallPath)).Replace('\\\\', '/'),\n        entryPointRelativePath = Path.GetRelativePath(_rootPath, Path.GetFullPath(addon.EntryPointPath)).Replace('\\\\', '/'),\n        localUrl = addon.LocalUrl,\n        requiredPhpExtensions = addon.RequiredPhpExtensions,\n        version = addon.Version,\n        downloadUrl = addon.DownloadUrl,\n        sha256 = addon.Sha256,\n        archiveRootDirectory = addon.ArchiveRootDirectory\n    };\n''')

# 3) A corrupt Task Center history file made PlatformTaskCenter construction fail,
# which can break Environment Center startup. Treat history as recoverable log data:
# validate it, quarantine invalid content, and start with an empty history.
old = '''    private IReadOnlyList<PlatformTaskSnapshot> LoadHistory()\n    {\n        if (!File.Exists(_historyPath))\n            return Array.Empty<PlatformTaskSnapshot>();\n        try\n        {\n            var items = JsonSerializer.Deserialize<List<PlatformTaskSnapshot>>(File.ReadAllText(_historyPath), JsonOptions)\n                ?? new List<PlatformTaskSnapshot>();\n            return items.Select(item => item.State is PlatformTaskState.Queued or PlatformTaskState.Running\n                    ? item with\n                    {\n                        State = PlatformTaskState.Failed,\n                        Error = "DevBox exited before this task finished.",\n                        Message = "Interrupted",\n                        FinishedAtUtc = DateTimeOffset.UtcNow\n                    }\n                    : item)\n                .OrderByDescending(item => item.CreatedAtUtc)\n                .Take(500)\n                .ToArray();\n        }\n        catch (JsonException ex)\n        {\n            throw new InvalidDataException("Task Center history contains invalid JSON.", ex);\n        }\n    }\n'''
new = '''    private IReadOnlyList<PlatformTaskSnapshot> LoadHistory()\n    {\n        if (!File.Exists(_historyPath))\n            return Array.Empty<PlatformTaskSnapshot>();\n        try\n        {\n            var items = JsonSerializer.Deserialize<List<PlatformTaskSnapshot?>>(File.ReadAllText(_historyPath), JsonOptions)\n                ?? new List<PlatformTaskSnapshot?>();\n            if (items.Any(item => item is null))\n                throw new InvalidDataException("Task Center history contains a null entry.");\n\n            var materialized = items.Select(item => item!).ToArray();\n            foreach (var item in materialized)\n            {\n                if (item.Id == Guid.Empty || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 160 ||\n                    !Enum.IsDefined(typeof(PlatformTaskState), item.State) || !double.IsFinite(item.Progress) ||\n                    item.Progress is < 0 or > 100)\n                {\n                    throw new InvalidDataException("Task Center history contains an invalid entry.");\n                }\n            }\n\n            return materialized.Select(item => item.State is PlatformTaskState.Queued or PlatformTaskState.Running\n                    ? item with\n                    {\n                        State = PlatformTaskState.Failed,\n                        Error = "DevBox exited before this task finished.",\n                        Message = "Interrupted",\n                        FinishedAtUtc = DateTimeOffset.UtcNow\n                    }\n                    : item)\n                .OrderByDescending(item => item.CreatedAtUtc)\n                .Take(500)\n                .ToArray();\n        }\n        catch (JsonException)\n        {\n            QuarantineInvalidHistory();\n            return Array.Empty<PlatformTaskSnapshot>();\n        }\n        catch (InvalidDataException)\n        {\n            QuarantineInvalidHistory();\n            return Array.Empty<PlatformTaskSnapshot>();\n        }\n    }\n\n    private void QuarantineInvalidHistory()\n    {\n        if (!File.Exists(_historyPath))\n            return;\n        var quarantine = $"{_historyPath}.invalid-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.bak";\n        try\n        {\n            File.Move(_historyPath, quarantine);\n        }\n        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)\n        {\n        }\n    }\n'''
replace_once("src/DevBox.Core/Services/PlatformTaskCenter.cs", old, new)

# 4) Local runtime import moved the new version into its permanent directory before
# activation. If activation failed, the API threw but left a partial successful install.
replace_once(
    "src/DevBox.Core/Services/RuntimePlatformService.cs",
    '''            Directory.Move(staging, installPath);\n\n            if (activate)\n                await _runtimeManager.ActivateUnderLockAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);\n''',
    '''            Directory.Move(staging, installPath);\n            try\n            {\n                if (activate)\n                    await _runtimeManager.ActivateUnderLockAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);\n            }\n            catch\n            {\n                TryDeleteDirectory(installPath);\n                throw;\n            }\n''')

# Regression coverage.
tests = Path("tests/DevBox.Tests/PostMergeAuditRound6Tests.cs")
tests.write_text(r'''using System.IO.Compression;
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
''', encoding="utf-8")
