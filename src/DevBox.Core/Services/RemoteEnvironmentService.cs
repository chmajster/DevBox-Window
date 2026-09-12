using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class RemoteEnvironmentService
{
    private readonly string _rootPath;
    private readonly string _shareRoot;
    private readonly EnvironmentProfileService _profiles;

    public RemoteEnvironmentService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _shareRoot = Path.Combine(_rootPath, "backups", "environment-shares");
        _profiles = new EnvironmentProfileService(_rootPath);
    }

    public string ExportProfile(string profileKey, string? destinationPath = null)
    {
        var profile = _profiles.GetProfile(profileKey);
        return Export(profile, $"profile-{profile.Key}", destinationPath);
    }

    public string ExportProjectLock(string projectPath, string? destinationPath = null)
    {
        using var locks = new EnvironmentLockService(_rootPath);
        var lockFile = locks.Load(projectPath);
        var kind = DetectProjectKind(projectPath);
        var profile = new EnvironmentProfile
        {
            Key = $"imported-{NormalizeKey(lockFile.ProjectName)}",
            DisplayName = $"{lockFile.ProjectName} environment",
            Kind = kind,
            Runtimes = new Dictionary<string, string>(lockFile.Runtimes, StringComparer.OrdinalIgnoreCase),
            Database = lockFile.Database with { DatabaseName = null },
            Https = lockFile.Https,
            Addons = lockFile.Addons,
            Services = lockFile.Services,
            Actions = lockFile.Actions,
            Description = $"Portable environment definition exported from {lockFile.ProjectName}. Database name, project actions and sensitive authentication material are intentionally excluded."
        };
        return Export(profile, $"project-{lockFile.ProjectName}", destinationPath);
    }

    public EnvironmentShareImportResult Import(string sourcePath, bool replaceExisting = false)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Environment share file was not found.", source);
        if (new FileInfo(source).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Environment share file exceeds the 2 MiB limit.");

        var content = File.ReadAllText(source);
        EnvironmentShareBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<EnvironmentShareBundle>(content, JsonOptions)
                ?? throw new InvalidDataException("Environment share file is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Environment share file contains invalid JSON.", ex);
        }
        ValidateBundle(bundle);
        EnsureNoSensitiveMaterial(content);

        var existing = _profiles.GetProfiles().Any(item => item.Key.Equals(bundle.Profile.Key, StringComparison.OrdinalIgnoreCase));
        if (existing && !replaceExisting)
            throw new InvalidOperationException($"Environment profile '{bundle.Profile.Key}' already exists. Explicit replacement is required.");
        _profiles.SaveCustomProfile(bundle.Profile);
        return new EnvironmentShareImportResult(bundle.Profile.Key, bundle.Profile.DisplayName, existing, DateTimeOffset.UtcNow);
    }

    private string Export(EnvironmentProfile profile, string name, string? destinationPath)
    {
        var sanitized = profile with
        {
            Database = profile.Database with { DatabaseName = null },
            Actions = Array.Empty<ProjectActionDefinition>(),
            Description = profile.Description
        };
        var bundle = new EnvironmentShareBundle
        {
            Name = name,
            Profile = sanitized,
            Metadata = new Dictionary<string, string>
            {
                ["format"] = "DevBox Environment Share",
                ["sanitized"] = "true",
                ["actionsOmitted"] = profile.Actions.Count > 0 ? "true" : "false"
            }
        };
        var content = JsonSerializer.Serialize(bundle, JsonOptions);
        EnsureNoSensitiveMaterial(content);
        Directory.CreateDirectory(_shareRoot);
        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(_shareRoot, $"{SafeFileName(name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.devbox-env.json")
            : Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        AtomicWrite(destination, content);
        return destination;
    }

    private ProjectKind DetectProjectKind(string projectPath)
    {
        var sites = new SiteManager(_rootPath);
        var workspace = new ProjectWorkspaceService(_rootPath, sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
        return workspace.Detect(projectPath).Kind;
    }

    private static void ValidateBundle(EnvironmentShareBundle bundle)
    {
        if (bundle.SchemaVersion != EnvironmentShareBundle.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported environment share schema version: {bundle.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(bundle.Name) || bundle.Name.Length > 160)
            throw new InvalidDataException("Environment share name is invalid.");
        ArgumentNullException.ThrowIfNull(bundle.Profile);
        if (bundle.Profile.Actions.Count > 0)
            throw new InvalidDataException("Portable environment shares must not contain project actions because action arguments may contain sensitive values.");
    }

    private static void EnsureNoSensitiveMaterial(string json)
    {
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement);
        return;

        static void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    var key = property.Name.ToLowerInvariant();
                    if (key.Contains("password", StringComparison.Ordinal) ||
                        key.Contains("secret", StringComparison.Ordinal) ||
                        key.Contains("token", StringComparison.Ordinal) ||
                        key.Contains("privatekey", StringComparison.Ordinal) ||
                        key.Contains("credential", StringComparison.Ordinal))
                        throw new InvalidDataException($"Environment share contains forbidden sensitive field '{property.Name}'.");
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    Walk(item);
            }
        }
    }

    private static string NormalizeKey(string value)
    {
        var normalized = new string(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = "environment";
        return normalized.Length <= 50 ? normalized : normalized[..50];
    }

    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
