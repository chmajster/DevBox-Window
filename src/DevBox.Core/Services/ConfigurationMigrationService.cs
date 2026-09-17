using System.Text.Json;

namespace DevBox.Core.Services;

public static class ConfigurationMigrationService
{
    private const int ManifestSchemaVersion = 1;
    private const long MaxManifestBytes = 256 * 1024;

    private static readonly IReadOnlyDictionary<string, int> CurrentFileVersions =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["addons.json"] = 1,
            ["addons.local.json"] = 1,
            ["addon-marketplace-source.json"] = 1,
            ["appsettings.json"] = 1,
            ["database-runtimes.json"] = 1,
            ["environment-profiles.json"] = 1,
            ["project-profiles.json"] = 1,
            ["runtime-catalog.json"] = 1,
            ["services.json"] = 1,
            ["sites.json"] = 1,
            ["secrets.dpapi.json"] = 1
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void EnsureMigrated(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.GetFullPath(rootPath);
        var configRoot = PathSafety.EnsureUnderRootWithoutReparsePoints(
            root,
            Path.Combine(root, "config"),
            "Configuration migration root cannot traverse a reparse point.");
        Directory.CreateDirectory(configRoot);

        var manifestPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
            root,
            Path.Combine(configRoot, "schema-versions.json"),
            "Configuration migration manifest cannot traverse a reparse point.");
        var lockPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
            root,
            manifestPath + ".lock",
            "Configuration migration lock cannot traverse a reparse point.");

        using var migrationLock = CrossProcessFileLock.Acquire(lockPath, TimeSpan.FromSeconds(15));
        var state = LoadState(manifestPath);
        ValidateManifestSchema(state);

        var originalManifest = File.Exists(manifestPath) ? ReadBounded(manifestPath, MaxManifestBytes) : null;
        var backups = new List<(string Source, string Backup)>();
        string? backupRoot = null;
        var changed = !File.Exists(manifestPath);

        try
        {
            foreach (var (relativeFile, targetVersion) in CurrentFileVersions)
            {
                var currentVersion = state.Files.TryGetValue(relativeFile, out var recordedVersion)
                    ? recordedVersion
                    : 0;

                if (currentVersion < 0)
                    throw new InvalidDataException($"Configuration schema version for '{relativeFile}' cannot be negative.");
                if (currentVersion > targetVersion)
                    throw new InvalidDataException(
                        $"Configuration file '{relativeFile}' uses schema version {currentVersion}, but this DevBox build supports up to version {targetVersion}. Upgrade DevBox before modifying this configuration.");

                var configPath = PathSafety.EnsureUnderRootWithoutReparsePoints(
                    configRoot,
                    Path.Combine(configRoot, relativeFile),
                    $"Configuration file '{relativeFile}' cannot traverse a reparse point.");

                if (currentVersion < targetVersion && File.Exists(configPath))
                {
                    backupRoot ??= CreateBackupRoot(root);
                    var backupPath = Path.Combine(backupRoot, "config", relativeFile);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                    File.Copy(configPath, backupPath, overwrite: false);
                    backups.Add((configPath, backupPath));
                }

                while (currentVersion < targetVersion)
                {
                    ApplyMigration(configPath, relativeFile, currentVersion, currentVersion + 1);
                    currentVersion++;
                    state.Files[relativeFile] = currentVersion;
                    changed = true;
                }
            }

            if (backupRoot is not null && originalManifest is not null)
            {
                var manifestBackup = Path.Combine(backupRoot, "config", "schema-versions.json");
                Directory.CreateDirectory(Path.GetDirectoryName(manifestBackup)!);
                File.WriteAllText(manifestBackup, originalManifest);
            }

            if (changed)
                AtomicWrite(manifestPath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            foreach (var (source, backup) in backups.AsEnumerable().Reverse())
            {
                try
                {
                    File.Copy(backup, source, overwrite: true);
                }
                catch
                {
                    // Preserve the original migration failure. The backup remains on disk for manual recovery.
                }
            }

            try
            {
                if (originalManifest is null)
                {
                    if (File.Exists(manifestPath))
                        File.Delete(manifestPath);
                }
                else
                {
                    AtomicWrite(manifestPath, originalManifest);
                }
            }
            catch
            {
                // Preserve the original migration failure. The previous manifest is also retained in the backup when possible.
            }

            throw;
        }
    }

    private static ConfigurationSchemaState LoadState(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return new ConfigurationSchemaState(ManifestSchemaVersion, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        try
        {
            var state = JsonSerializer.Deserialize<ConfigurationSchemaState>(ReadBounded(manifestPath, MaxManifestBytes), JsonOptions)
                ?? throw new InvalidDataException("config/schema-versions.json cannot contain JSON null.");
            if (state.Files is null)
                throw new InvalidDataException("config/schema-versions.json must contain the files map.");

            return new ConfigurationSchemaState(
                state.SchemaVersion,
                new Dictionary<string, int>(state.Files, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/schema-versions.json contains invalid JSON.", ex);
        }
    }

    private static void ValidateManifestSchema(ConfigurationSchemaState state)
    {
        if (state.SchemaVersion != ManifestSchemaVersion)
        {
            if (state.SchemaVersion > ManifestSchemaVersion)
                throw new InvalidDataException(
                    $"Configuration migration manifest uses schema version {state.SchemaVersion}, but this DevBox build supports version {ManifestSchemaVersion}.");

            throw new InvalidDataException($"Unsupported configuration migration manifest schema version: {state.SchemaVersion}.");
        }
    }

    private static void ApplyMigration(string path, string relativeFile, int fromVersion, int toVersion)
    {
        if (fromVersion == 0 && toVersion == 1)
        {
            // Version 1 adopts the existing DevBox JSON formats without rewriting user data.
            // Future format changes belong here as explicit vN -> vN+1 transformations.
            return;
        }

        throw new InvalidDataException(
            $"No configuration migration is registered for '{relativeFile}' from version {fromVersion} to {toVersion}.");
    }

    private static string CreateBackupRoot(string root)
    {
        var backupBase = PathSafety.EnsureUnderRootWithoutReparsePoints(
            root,
            Path.Combine(root, "backups", "configuration-migrations"),
            "Configuration migration backup root cannot traverse a reparse point.");
        Directory.CreateDirectory(backupBase);

        var backupRoot = Path.Combine(
            backupBase,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(backupRoot);
        return backupRoot;
    }

    private static string ReadBounded(string path, long maxBytes)
    {
        var info = new FileInfo(path);
        if (info.Length > maxBytes)
            throw new InvalidDataException($"Configuration migration manifest exceeds the {maxBytes} byte limit.");
        return File.ReadAllText(path);
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private sealed record ConfigurationSchemaState(int SchemaVersion, Dictionary<string, int> Files);
}
