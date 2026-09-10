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
        {
            return Array.Empty<ManagedServiceManifest>();
        }

        try
        {
            var manifests = JsonSerializer.Deserialize<List<ManagedServiceManifest>>(File.ReadAllText(_manifestPath), JsonOptions)
                ?? new List<ManagedServiceManifest>();
            ValidateAll(manifests);
            return manifests.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/services.json contains invalid JSON.", ex);
        }
    }

    public IReadOnlyList<ServiceDefinition> GetEnabledDefinitions()
    {
        return GetManifests()
            .Where(manifest => manifest.Enabled)
            .Select(GetDefinition)
            .ToArray();
    }

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
        Directory.CreateDirectory(Path.GetDirectoryName(_manifestPath)!);
        AtomicWrite(_manifestPath, JsonSerializer.Serialize(manifests.OrderBy(item => item.Key), JsonOptions));
    }

    public void Upsert(ManagedServiceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Validate(manifest);
        var manifests = GetManifests().ToList();
        var index = manifests.FindIndex(item => item.Key.Equals(manifest.Key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            manifests[index] = manifest;
        }
        else
        {
            manifests.Add(manifest);
        }
        Save(manifests);
    }

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var manifests = GetManifests().ToList();
        var removed = manifests.RemoveAll(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            Save(manifests);
        }
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
        {
            Validate(manifest);
        }

        var duplicateKey = materialized.GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1);
        if (duplicateKey is not null)
        {
            throw new InvalidDataException($"Duplicate managed service key: {duplicateKey.Key}.");
        }
        var duplicatePort = materialized.Where(item => item.Enabled).GroupBy(item => item.Port).FirstOrDefault(group => group.Count() > 1);
        if (duplicatePort is not null)
        {
            throw new InvalidDataException($"Multiple enabled managed services use TCP port {duplicatePort.Key}.");
        }
    }

    private void Validate(ManagedServiceManifest manifest)
    {
        if (manifest.SchemaVersion != ManagedServiceManifest.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported managed service schema version: {manifest.SchemaVersion}.");
        }
        if (!SafeKeyRegex().IsMatch(manifest.Key) || ReservedKeys.Contains(manifest.Key))
        {
            throw new InvalidDataException($"Managed service key '{manifest.Key}' is invalid or reserved.");
        }
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || manifest.DisplayName.Length > 100)
        {
            throw new InvalidDataException("Managed service display name is invalid.");
        }
        if (manifest.Port is < 1 or > 65535 || manifest.Port is 80 or 3306 or 9084)
        {
            throw new InvalidDataException($"Managed service port {manifest.Port} is invalid or reserved by a core DevBox service.");
        }
        if (manifest.GracefulStopTimeoutSeconds is < 1 or > 60)
        {
            throw new InvalidDataException("Managed service graceful-stop timeout must be between 1 and 60 seconds.");
        }
        if (manifest.Arguments.Count > 64 || manifest.Arguments.Any(argument => argument.Contains('\0')))
        {
            throw new InvalidDataException("Managed service arguments are invalid.");
        }
        if (manifest.StopArguments is { Count: > 64 } || manifest.StopArguments?.Any(argument => argument.Contains('\0')) == true)
        {
            throw new InvalidDataException("Managed service stop arguments are invalid.");
        }

        _ = ResolveRelativeFile(manifest.ExecutableRelativePath, nameof(manifest.ExecutableRelativePath));
        _ = ResolveRelativeDirectory(manifest.WorkingDirectoryRelativePath, nameof(manifest.WorkingDirectoryRelativePath));
        if (!string.IsNullOrWhiteSpace(manifest.StopExecutableRelativePath))
        {
            _ = ResolveRelativeFile(manifest.StopExecutableRelativePath, nameof(manifest.StopExecutableRelativePath));
        }
        if (!string.IsNullOrWhiteSpace(manifest.LogRelativePath))
        {
            _ = ResolveRelativeFile(manifest.LogRelativePath, nameof(manifest.LogRelativePath));
        }
    }

    private string ResolveRelativeFile(string relativePath, string name) => ResolveInsideRoot(relativePath, name);
    private string ResolveRelativeDirectory(string relativePath, string name) => ResolveInsideRoot(relativePath, name);

    private string ResolveInsideRoot(string relativePath, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath, name);
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException($"{name} must be relative to the DevBox root.");
        }
        var root = _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(_rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{name} escapes the DevBox root.");
        }
        return full;
    }

    private static void AtomicWrite(string path, string content)
    {
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
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
