using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class EnvironmentLockService : IDisposable
{
    public const string LockFileName = "devbox.lock.json";
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly EnvironmentProfileService _profiles;
    private readonly RuntimePlatformService _runtimes;
    private readonly DatabaseRuntimeService _databaseRuntimes;
    private readonly SiteManager _sites;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectActionService _actions;
    private bool _disposed;

    public EnvironmentLockService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.Combine(_rootPath, "www");
        _profiles = new EnvironmentProfileService(_rootPath);
        _runtimes = new RuntimePlatformService(_rootPath, httpClient);
        _databaseRuntimes = new DatabaseRuntimeService(_rootPath);
        _sites = new SiteManager(_rootPath);
        _workspace = new ProjectWorkspaceService(_rootPath, _sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
        _actions = new ProjectActionService(_rootPath, _workspace);
    }

    public EnvironmentLockFile Generate(string projectPath, string? profileKey = null)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var manifest = ReadManifestObject(root);
        var profile = string.IsNullOrWhiteSpace(profileKey) ? null : _profiles.GetProfile(profileKey);

        var projectName = GetString(manifest, "Name") ?? Path.GetFileName(root);
        var domain = GetString(manifest, "Domain") ?? $"{projectName}.test";
        var runtimePins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            foreach (var pair in profile.Runtimes)
                runtimePins[pair.Key] = pair.Value;
        }

        AddIfNotEmpty(runtimePins, "php", GetString(manifest, "PhpVersion") ?? ReadActiveVersion("php"));
        AddIfNotEmpty(runtimePins, "node", GetString(manifest, "NodeVersion") ?? ReadEnvironmentRuntime(manifest, "node"));
        AddIfNotEmpty(runtimePins, "nginx", ReadEnvironmentRuntime(manifest, "nginx") ?? ReadActiveVersion("nginx"));

        var databaseEngine = (GetString(manifest, "DatabaseEngine") ?? profile?.Database.Engine ?? "none").Trim().ToLowerInvariant();
        var databaseVersion = ReadEnvironmentDatabaseVersion(manifest)
            ?? profile?.Database.Version
            ?? (databaseEngine == "none" ? null : ReadActiveVersion(databaseEngine));
        var databaseName = GetString(manifest, "DatabaseName") ?? profile?.Database.DatabaseName;
        var databasePort = ReadEnvironmentDatabasePort(manifest) ?? profile?.Database.Port;

        var lockFile = new EnvironmentLockFile
        {
            ProjectName = projectName,
            Domain = domain,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Runtimes = runtimePins,
            Database = new EnvironmentDatabasePin(databaseEngine, databaseVersion, databaseName, databasePort),
            Https = GetBool(manifest, "Https") ?? profile?.Https ?? false,
            Addons = GetStringArray(manifest, "Addons").Concat(profile?.Addons ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            Services = GetStringArray(manifest, "Services").Concat(profile?.Services ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
            Actions = _actions.GetActions(root),
            SourceProfile = profile?.Key
        };

        ValidateLock(lockFile);
        AtomicWrite(Path.Combine(root, LockFileName), JsonSerializer.Serialize(lockFile, JsonOptions));
        return lockFile;
    }

    public EnvironmentLockFile Load(string projectPath)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var path = Path.Combine(root, LockFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("devbox.lock.json does not exist for this project.", path);
        try
        {
            var value = JsonSerializer.Deserialize<EnvironmentLockFile>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("devbox.lock.json is empty.");
            ValidateLock(value);
            return value;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.lock.json contains invalid JSON.", ex);
        }
    }

    public async Task<EnvironmentApplyResult> ApplyProfileAsync(string projectPath, string profileKey, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var profile = _profiles.GetProfile(profileKey);
        var manifest = ReadManifestObject(root);
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var pin in profile.Runtimes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = _runtimes.GetStatuses(pin.Key).FirstOrDefault(item => item.Package.Version.Equals(pin.Value, StringComparison.OrdinalIgnoreCase));
                if (status is null || !status.Installed || !status.Valid)
                {
                    await _runtimes.InstallAsync(pin.Key, pin.Value, cancellationToken).ConfigureAwait(false);
                    applied.Add($"Installed {pin.Key} {pin.Value}.");
                }
                else
                {
                    applied.Add($"Verified {pin.Key} {pin.Value}.");
                }

                if (pin.Key.Equals("nginx", StringComparison.OrdinalIgnoreCase))
                {
                    await _runtimes.ActivateAsync(pin.Key, pin.Value, cancellationToken).ConfigureAwait(false);
                    applied.Add($"Activated Nginx {pin.Value}.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or KeyNotFoundException or PlatformNotSupportedException)
            {
                warnings.Add($"Runtime {pin.Key} {pin.Value}: {ex.Message}");
            }
        }

        var phpVersion = profile.Runtimes.TryGetValue("php", out var php) ? php : GetString(manifest, "PhpVersion");
        var nodeVersion = profile.Runtimes.TryGetValue("node", out var node) ? node : GetString(manifest, "NodeVersion");
        var databaseName = GetString(manifest, "DatabaseName") ?? profile.Database.DatabaseName ?? SafeDatabaseName(GetString(manifest, "Name") ?? Path.GetFileName(root));
        UpdateLegacyManifest(manifest, profile, phpVersion, nodeVersion, databaseName);
        AtomicWrite(Path.Combine(root, ProjectWorkspaceService.ManifestFileName), manifest.ToJsonString(JsonOptions));
        applied.Add("Updated devbox.json environment metadata without breaking schema v1 compatibility.");

        var projectName = GetString(manifest, "Name") ?? Path.GetFileName(root);
        var site = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (site is not null && !string.IsNullOrWhiteSpace(phpVersion))
        {
            try
            {
                _ = _sites.SetPhpVersion(site.Name, phpVersion);
                applied.Add($"Pinned Site to PHP {phpVersion}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                warnings.Add($"PHP Site pin: {ex.Message}");
            }
        }

        if (!profile.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(profile.Database.Version))
        {
            try
            {
                var database = await _databaseRuntimes.EnsureInitializedAsync(profile.Database.Engine, profile.Database.Version, profile.Database.Port, cancellationToken).ConfigureAwait(false);
                applied.Add($"Prepared {profile.Database.Engine} {profile.Database.Version} on port {database.Port}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                warnings.Add($"Database runtime {profile.Database.Engine} {profile.Database.Version}: {ex.Message}");
            }
        }

        foreach (var addonKey in profile.Addons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var catalog = new AddonCatalog(_rootPath);
                var addon = catalog.GetAddons().FirstOrDefault(item => item.Key.Equals(addonKey, StringComparison.OrdinalIgnoreCase));
                if (addon is null)
                {
                    warnings.Add($"ADDON '{addonKey}' is not present in the local catalog.");
                    continue;
                }
                using var installer = new AddonInstaller(_rootPath);
                await installer.InstallAsync(addon, cancellationToken).ConfigureAwait(false);
                applied.Add($"Ensured ADDON {addon.DisplayName} {addon.Version}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException)
            {
                warnings.Add($"ADDON {addonKey}: {ex.Message}");
            }
        }

        if (profile.Actions.Count > 0)
        {
            _actions.SetActions(root, profile.Actions);
            applied.Add($"Configured {profile.Actions.Count} project action(s).");
        }

        if (profile.Https)
        {
            var domain = GetString(manifest, "Domain");
            if (!string.IsNullOrWhiteSpace(domain))
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        using var certificate = new LocalCertificateAuthorityService(_rootPath).IssueSiteCertificate(domain);
                        applied.Add($"Issued {domain} from the DevBox local CA.");
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
                {
                    warnings.Add($"Local CA certificate: {ex.Message}");
                }
            }
        }

        var lockFile = Generate(root, profile.Key);
        return new EnvironmentApplyResult(lockFile, applied, warnings);
    }

    public async Task<EnvironmentApplyResult> ApplyLockAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var lockFile = Load(root);
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var pin in lockFile.Runtimes)
        {
            try
            {
                var packageStatus = _runtimes.GetStatuses(pin.Key).FirstOrDefault(item => item.Package.Version.Equals(pin.Value, StringComparison.OrdinalIgnoreCase));
                if (packageStatus is null || !packageStatus.Installed || !packageStatus.Valid)
                {
                    await _runtimes.InstallAsync(pin.Key, pin.Value, cancellationToken).ConfigureAwait(false);
                    applied.Add($"Installed {pin.Key} {pin.Value} from lock.");
                }
                if (pin.Key.Equals("nginx", StringComparison.OrdinalIgnoreCase))
                    await _runtimes.ActivateAsync(pin.Key, pin.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException or KeyNotFoundException or PlatformNotSupportedException)
            {
                warnings.Add($"Runtime {pin.Key} {pin.Value}: {ex.Message}");
            }
        }

        if (!lockFile.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(lockFile.Database.Version))
        {
            try
            {
                var database = await _databaseRuntimes.EnsureInitializedAsync(lockFile.Database.Engine, lockFile.Database.Version, lockFile.Database.Port, cancellationToken).ConfigureAwait(false);
                applied.Add($"Prepared locked database runtime {lockFile.Database.Engine} {lockFile.Database.Version} on port {database.Port}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                warnings.Add($"Database runtime: {ex.Message}");
            }
        }

        if (lockFile.Actions.Count > 0)
            _actions.SetActions(root, lockFile.Actions);

        return new EnvironmentApplyResult(lockFile, applied, warnings);
    }

    public IReadOnlyList<string> GetDrift(string projectPath)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var lockFile = Load(root);
        var drift = new List<string>();
        foreach (var pin in lockFile.Runtimes)
        {
            RuntimeVersionStatus? status;
            try
            {
                status = _runtimes.GetStatuses(pin.Key).FirstOrDefault(item => item.Package.Version.Equals(pin.Value, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                drift.Add($"{pin.Key} {pin.Value}: unable to inspect runtime: {ex.Message}");
                continue;
            }
            if (status is null || !status.Installed)
                drift.Add($"Missing runtime: {pin.Key} {pin.Value}.");
            else if (!status.Valid)
                drift.Add($"Invalid runtime: {pin.Key} {pin.Value}.");
        }

        if (!lockFile.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(lockFile.Database.Version))
        {
            var database = _databaseRuntimes.GetInstances(lockFile.Database.Engine)
                .FirstOrDefault(item => item.Version.Equals(lockFile.Database.Version, StringComparison.OrdinalIgnoreCase));
            if (database is null)
                drift.Add($"Database runtime is not registered: {lockFile.Database.Engine} {lockFile.Database.Version}.");
            else if (lockFile.Database.Port is not null && database.Port != lockFile.Database.Port)
                drift.Add($"Database port drift: locked {lockFile.Database.Port}, registered {database.Port}.");
        }

        foreach (var addonKey in lockFile.Addons)
        {
            var addon = new AddonCatalog(_rootPath).GetAddons().FirstOrDefault(item => item.Key.Equals(addonKey, StringComparison.OrdinalIgnoreCase));
            if (addon is null)
            {
                drift.Add($"ADDON catalog entry is missing: {addonKey}.");
                continue;
            }
            var installPath = Path.GetFullPath(Path.Combine(_rootPath, addon.InstallPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(installPath) && !File.Exists(installPath))
                drift.Add($"ADDON is not installed: {addonKey}.");
        }

        if (lockFile.Https)
        {
            var certificate = Path.Combine(_rootPath, "config", "ssl", "sites", $"{lockFile.Domain}.crt.pem");
            var key = Path.Combine(_rootPath, "config", "ssl", "sites", $"{lockFile.Domain}.key.pem");
            if (!File.Exists(certificate) || !File.Exists(key))
                drift.Add($"HTTPS certificate files are missing for {lockFile.Domain}.");
        }

        return drift;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runtimes.Dispose();
        _databaseRuntimes.Dispose();
    }

    private void UpdateLegacyManifest(JsonObject manifest, EnvironmentProfile profile, string? phpVersion, string? nodeVersion, string databaseName)
    {
        SetProperty(manifest, "PhpVersion", phpVersion);
        SetProperty(manifest, "NodeVersion", nodeVersion);
        SetProperty(manifest, "DatabaseEngine", profile.Database.Engine.ToLowerInvariant());
        SetProperty(manifest, "DatabaseName", profile.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : databaseName);
        SetProperty(manifest, "Https", profile.Https);
        SetProperty(manifest, "Addons", JsonSerializer.SerializeToNode(profile.Addons, JsonOptions));
        SetProperty(manifest, "Services", JsonSerializer.SerializeToNode(profile.Services, JsonOptions));
        manifest["Environment"] = new JsonObject
        {
            ["Profile"] = profile.Key,
            ["Runtimes"] = JsonSerializer.SerializeToNode(profile.Runtimes, JsonOptions),
            ["DatabaseVersion"] = profile.Database.Version,
            ["DatabasePort"] = profile.Database.Port
        };
    }

    private JsonObject ReadManifestObject(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("devbox.json does not exist for this project.", path);
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid JSON.", ex);
        }
    }

    private string EnsureProjectRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project directory was not found: {root}");
        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Environment operations are restricted to projects inside the DevBox www directory.");
        return root;
    }

    private string? ReadActiveVersion(string key)
    {
        var marker = Path.Combine(_rootPath, "runtime", key, "current", ".devbox-version");
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static string? ReadEnvironmentRuntime(JsonObject manifest, string runtimeKey)
    {
        var environment = FindObject(manifest, "Environment");
        var runtimes = environment is null ? null : FindObject(environment, "Runtimes");
        return runtimes is null ? null : GetString(runtimes, runtimeKey);
    }

    private static string? ReadEnvironmentDatabaseVersion(JsonObject manifest) => FindObject(manifest, "Environment") is { } environment ? GetString(environment, "DatabaseVersion") : null;

    private static int? ReadEnvironmentDatabasePort(JsonObject manifest)
    {
        var environment = FindObject(manifest, "Environment");
        if (environment is null)
            return null;
        var node = FindProperty(environment, "DatabasePort");
        return node is JsonValue value && value.TryGetValue<int>(out var port) ? port : null;
    }

    private static JsonObject? FindObject(JsonObject value, string name) => FindProperty(value, name) as JsonObject;

    private static JsonNode? FindProperty(JsonObject value, string name)
    {
        foreach (var pair in value)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    private static string? GetString(JsonObject value, string name)
    {
        var node = FindProperty(value, name);
        return node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) ? text : null;
    }

    private static bool? GetBool(JsonObject value, string name)
    {
        var node = FindProperty(value, name);
        return node is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out var flag) ? flag : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonObject value, string name)
    {
        var node = FindProperty(value, name);
        if (node is not JsonArray array)
            return Array.Empty<string>();
        return array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray();
    }

    private static void SetProperty(JsonObject value, string name, object? propertyValue)
    {
        var existing = value.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        var key = string.IsNullOrEmpty(existing) ? name : existing;
        value[key] = propertyValue is JsonNode node ? node : JsonValue.Create(propertyValue);
    }

    private static void AddIfNotEmpty(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values[key] = value.Trim();
    }

    private static string SafeDatabaseName(string value)
    {
        var normalized = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "devbox" : normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static void ValidateLock(EnvironmentLockFile value)
    {
        if (value.SchemaVersion != EnvironmentLockFile.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported devbox.lock.json schema version: {value.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(value.ProjectName) || string.IsNullOrWhiteSpace(value.Domain) || !value.Domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Environment lock project name or .test domain is invalid.");
        foreach (var pair in value.Runtimes)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value) || pair.Key.Contains("..", StringComparison.Ordinal) || pair.Value.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Environment lock contains an invalid runtime pin.");
        }
        var engine = value.Database.Engine.ToLowerInvariant();
        if (engine is not ("mysql" or "mariadb" or "postgresql" or "none"))
            throw new InvalidDataException("Environment lock contains an unsupported database engine.");
        if (value.Database.Port is < 1 or > 65535)
            throw new InvalidDataException("Environment lock database port is invalid.");
    }

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
