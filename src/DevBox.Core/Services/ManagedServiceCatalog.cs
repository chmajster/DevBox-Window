using System.Text.Json;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class ManagedServiceCatalog
{
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "nginx", "php", "mysql"
    };
    private static readonly string[] ReservedKeyPrefixes = ["db-", "php-pool-"];

    private readonly string _rootPath;
    private readonly string _manifestPath;

    public ManagedServiceCatalog(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _manifestPath = Path.Combine(_rootPath, "config", "services.json");
    }

    public IReadOnlyList<ManagedServiceManifest> GetManifests()
    {
        if (!File.Exists(_manifestPath))
            return Array.Empty<ManagedServiceManifest>();

        try
        {
            var manifests = JsonSerializer.Deserialize<List<ManagedServiceManifest?>>(File.ReadAllText(_manifestPath), JsonOptions)
                ?? new List<ManagedServiceManifest?>();
            if (manifests.Any(item => item is null))
                throw new InvalidDataException("config/services.json contains a null managed-service entry.");
            var materialized = manifests.Select(item => item!).ToArray();
            ValidateAll(materialized);
            return materialized.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/services.json contains invalid JSON.", ex);
        }
    }

    public IReadOnlyList<ServiceDefinition> GetEnabledDefinitions() => GetManifests()
        .Where(manifest => manifest.Enabled)
        .Select(GetDefinition)
        .ToArray();

    public ServiceDefinition GetDefinition(ManagedServiceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var executable = ResolveRelativeFile(manifest.ExecutableRelativePath, nameof(manifest.ExecutableRelativePath));
        var workingDirectory = ResolveRelativeDirectory(manifest.WorkingDirectoryRelativePath, nameof(manifest.WorkingDirectoryRelativePath));
        var stopExecutable = string.IsNullOrWhiteSpace(manifest.StopExecutableRelativePath)
            ? null
            : ResolveRelativeFile(manifest.StopExecutableRelativePath, nameof(manifest.StopExecutableRelativePath));
        var logPath = string.IsNullOrWhiteSpace(manifest.LogRelativePath)
            ? Path.Combine(_rootPath, "logs", $"{manifest.Key}-process.log")
            : ResolveRelativeFile(manifest.LogRelativePath, nameof(manifest.LogRelativePath));

        return new ServiceDefinition(
            manifest.Key,
            manifest.DisplayName,
            executable,
            manifest.Arguments.ToArray(),
            workingDirectory,
            manifest.Port,
            manifest.Version,
            stopExecutable,
            manifest.StopArguments?.ToArray(),
            TimeSpan.FromSeconds(manifest.GracefulStopTimeoutSeconds),
            logPath);
    }

    public void Save(IReadOnlyCollection<ManagedServiceManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ValidateAll(manifests);
        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));
        SaveUnderLock(manifests);
    }

    private void SaveUnderLock(IReadOnlyCollection<ManagedServiceManifest> manifests)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        AtomicWrite(_manifestPath, JsonSerializer.Serialize(manifests.OrderBy(item => item.Key), JsonOptions));
    }

    public void Upsert(ManagedServiceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));
        var manifests = GetManifests().ToList();
        var index = manifests.FindIndex(item => item.Key.Equals(manifest.Key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            manifests[index] = manifest;
        else
            manifests.Add(manifest);
        SaveUnderLock(manifests);
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var mutationLock = CrossProcessFileLock.Acquire(_manifestPath + ".lock", TimeSpan.FromSeconds(15));
        var manifests = GetManifests().ToList();
        var removed = manifests.RemoveAll(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
            SaveUnderLock(manifests);
        return removed;
    }

    public static ManagedServiceManifest MailpitTemplate(string version = "current") => new(
        ManagedServiceManifest.CurrentSchemaVersion,
        "mailpit",
        "Mailpit",
        "runtime/mailpit/current/mailpit.exe",
        ["--listen", "127.0.0.1:8025", "--smtp", "127.0.0.1:1025"],
        ".",
        8025,
        version,
        Enabled: true,
        LogRelativePath: "logs/mailpit-process.log");

    public static ManagedServiceManifest RedisTemplate(string version = "current") => new(
        ManagedServiceManifest.CurrentSchemaVersion,
        "redis",
        "Garnet (Redis-compatible)",
        "runtime/redis/current/GarnetServer.exe",
        ["--bind", "127.0.0.1", "--port", "6379"],
        ".",
        6379,
        version,
        Enabled: true,
        LogRelativePath: "logs/redis-process.log");

    private void ValidateAll(IEnumerable<ManagedServiceManifest> manifests)
    {
        var materialized = manifests.ToArray();
        foreach (var manifest in materialized)
            Validate(manifest);

        var duplicateKey = materialized.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
            throw new InvalidDataException($"Duplicate managed service key: {duplicateKey.Key}.");
        var duplicatePort = materialized.Where(item => item.Enabled).GroupBy(item => item.Port).FirstOrDefault(group => group.Count() > 1);
        if (duplicatePort is not null)
            throw new InvalidDataException($"Multiple enabled managed services use TCP port {duplicatePort.Key}.");
    }

    private void Validate(ManagedServiceManifest manifest)
    {
        if (manifest.SchemaVersion != ManagedServiceManifest.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported managed service schema version: {manifest.SchemaVersion}.");
        var manifestKey = manifest.Key ?? string.Empty;
        if (!SafeKeyRegex().IsMatch(manifestKey) || ReservedKeys.Contains(manifestKey) ||
            ReservedKeyPrefixes.Any(prefix => manifestKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 100)
            throw new InvalidDataException("Managed service display name is invalid.");
        if (manifest.Port is < 1 or > 65535 || IsReservedCorePort(manifest.Port))
            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");
        if (manifest.GracefulStopTimeoutSeconds is < 1 or > 60)
            throw new InvalidDataException("Managed service graceful-stop timeout must be between 1 and 60 seconds.");
        if (string.IsNullOrWhiteSpace(manifest.ExecutableRelativePath) || string.IsNullOrWhiteSpace(manifest.WorkingDirectoryRelativePath) ||
            string.IsNullOrWhiteSpace(manifest.Version) || manifest.Arguments is null || manifest.Arguments.Count > 64 ||
            manifest.Arguments.Any(argument => argument is null || argument.Contains('\0')))
            throw new InvalidDataException("Managed service executable, working directory, version or arguments are invalid.");
        if (manifest.StopArguments is { Count: > 64 } || manifest.StopArguments?.Any(argument => argument is null || argument.Contains('\0')) == true)
            throw new InvalidDataException("Managed service stop arguments are invalid.");

        _ = ResolveRelativeFile(manifest.ExecutableRelativePath, nameof(manifest.ExecutableRelativePath));
        _ = ResolveRelativeDirectory(manifest.WorkingDirectoryRelativePath, nameof(manifest.WorkingDirectoryRelativePath));
        if (!string.IsNullOrWhiteSpace(manifest.StopExecutableRelativePath))
            _ = ResolveRelativeFile(manifest.StopExecutableRelativePath, nameof(manifest.StopExecutableRelativePath));
        if (!string.IsNullOrWhiteSpace(manifest.LogRelativePath))
            _ = ResolveRelativeFile(manifest.LogRelativePath, nameof(manifest.LogRelativePath));
    }

    private bool IsReservedCorePort(int port)
    {
        if (port is 80 or 443 or 3306 or 3316 or 5432 or 9084 || port is >= 20000 and <= 49999)
            return true;

        var databaseRegistrations = Path.Combine(_rootPath, "config", "database-runtimes.json");
        if (!File.Exists(databaseRegistrations))
            return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(databaseRegistrations));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("config/database-runtimes.json root must be a JSON array.");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("config/database-runtimes.json contains a non-object entry.");
                JsonElement? portValue = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (property.Name.Equals("port", StringComparison.OrdinalIgnoreCase))
                    {
                        portValue = property.Value;
                        break;
                    }
                }
                if (portValue is null || !portValue.Value.TryGetInt32(out var registeredPort) || registeredPort is < 1 or > 65535)
                    throw new InvalidDataException("config/database-runtimes.json contains an invalid or missing port.");
                if (registeredPort == port)
                    return true;
            }
            return false;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/database-runtimes.json contains invalid JSON.", ex);
        }
    }

    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name, allowRoot: false);
    private string ResolveRelativeDirectory(string relativePath, string name) => ResolveInsideRoot(relativePath, name, allowRoot: true);

    private string ResolveInsideRoot(string relativePath, string name, bool allowRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, name);
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"{name} must be relative to the DevBox root.");

        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        try
        {
            return PathSafety.EnsureUnderRootWithoutReparsePoints(
                _rootPath,
                full,
                $"{name} escapes the DevBox root or traverses a reparse point.",
                allowRoot);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }
    }

    private static void AtomicWrite(string path, string content)
    {
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKeyRegex();
}
