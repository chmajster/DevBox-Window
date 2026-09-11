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
    private readonly AddonCatalog _addons;
    private readonly ManagedServiceCatalog _managedServices;
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
        _addons = new AddonCatalog(_rootPath);
        _managedServices = new ManagedServiceCatalog(_rootPath);
    }

    public EnvironmentLockFile Generate(string projectPath, string? profileKey = null)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var manifest = ReadManifestObject(root);
        var profile = string.IsNullOrWhiteSpace(profileKey) ? null : _profiles.GetProfile(profileKey);
        var projectName = GetString(manifest, "Name") ?? Path.GetFileName(root);
        var domain = GetString(manifest, "Domain") ?? LocalDomainName.FromName(projectName);

        var runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            foreach (var pair in profile.Runtimes)
                runtimes[pair.Key] = pair.Value;
        }
        AddIfNotEmpty(runtimes, "php", GetString(manifest, "PhpVersion") ?? ReadActiveVersion("php"));
        AddIfNotEmpty(runtimes, "node", GetString(manifest, "NodeVersion") ?? ReadEnvironmentRuntime(manifest, "node"));
        AddIfNotEmpty(runtimes, "nginx", ReadEnvironmentRuntime(manifest, "nginx") ?? ReadActiveVersion("nginx"));

        var databaseEngine = (GetString(manifest, "DatabaseEngine") ?? profile?.Database.Engine ?? "none").Trim().ToLowerInvariant();
        var databaseVersion = ReadEnvironmentDatabaseVersion(manifest)
            ?? profile?.Database.Version
            ?? (databaseEngine == "none" ? null : ReadActiveVersion(databaseEngine));
        var databaseName = GetString(manifest, "DatabaseName") ?? profile?.Database.DatabaseName;
        var databasePort = ReadEnvironmentDatabasePort(manifest) ?? profile?.Database.Port;

        var result = new EnvironmentLockFile
        {
            ProjectName = projectName,
            Domain = domain,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Runtimes = runtimes,
            Database = new EnvironmentDatabasePin(databaseEngine, databaseVersion, databaseName, databasePort),
            Https = GetBool(manifest, "Https") ?? profile?.Https ?? false,
            Addons = MergeKeys(GetStringArray(manifest, "Addons"), profile?.Addons),
            Services = MergeKeys(GetStringArray(manifest, "Services"), profile?.Services),
            Actions = profile?.Actions.Count > 0 ? profile.Actions : _actions.GetActions(root),
            SourceProfile = profile?.Key
        };
        ValidateLock(result);
        AtomicWrite(Path.Combine(root, LockFileName), JsonSerializer.Serialize(result, JsonOptions));
        return result;
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
        var projectName = GetString(manifest, "Name") ?? Path.GetFileName(root);
        var domain = GetString(manifest, "Domain") ?? LocalDomainName.FromName(projectName);
        var databaseName = profile.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : GetString(manifest, "DatabaseName") ?? profile.Database.DatabaseName ?? SafeDatabaseName(projectName);

        var desired = new EnvironmentLockFile
        {
            ProjectName = projectName,
            Domain = domain,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Runtimes = new Dictionary<string, string>(profile.Runtimes, StringComparer.OrdinalIgnoreCase),
            Database = profile.Database with { DatabaseName = databaseName },
            Https = profile.Https,
            Addons = profile.Addons.ToArray(),
            Services = profile.Services.ToArray(),
            Actions = profile.Actions.ToArray(),
            SourceProfile = profile.Key
        };
        ValidateLock(desired);
        var result = await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);
        if (result.Warnings.Count == 0)
            AtomicWrite(Path.Combine(root, LockFileName), JsonSerializer.Serialize(desired, JsonOptions));
        return result;
    }

    public async Task<EnvironmentApplyResult> ApplyLockAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var desired = Load(root);
        return await ApplyDesiredStateAsync(root, desired, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<string> GetDrift(string projectPath)
    {
        ThrowIfDisposed();
        var root = EnsureProjectRoot(projectPath);
        var desired = Load(root);
        var drift = new List<string>();

        foreach (var pin in desired.Runtimes)
        {
            RuntimeVersionStatus? status;
            try
            {
                status = _runtimes.GetStatuses(pin.Key).FirstOrDefault(item => item.Package.Version.Equals(pin.Value, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or PlatformNotSupportedException)
            {
                drift.Add($"{pin.Key} {pin.Value}: unable to inspect runtime: {ex.Message}");
                continue;
            }
            if (status is null || !status.Installed)
                drift.Add($"Missing runtime: {pin.Key} {pin.Value}.");
            else if (!status.Valid)
                drift.Add($"Invalid runtime: {pin.Key} {pin.Value}.");
        }

        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
        {
            var database = _databaseRuntimes.GetInstances(desired.Database.Engine)
                .FirstOrDefault(item => item.Version.Equals(desired.Database.Version, StringComparison.OrdinalIgnoreCase));
            if (database is null)
                drift.Add($"Database runtime is not registered: {desired.Database.Engine} {desired.Database.Version}.");
            else if (desired.Database.Port is not null && database.Port != desired.Database.Port)
                drift.Add($"Database port drift: locked {desired.Database.Port}, registered {database.Port}.");
        }

        JsonObject manifest;
        try
        {
            manifest = ReadManifestObject(root);
            CompareManifest(desired, manifest, drift);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            drift.Add($"Manifest could not be checked: {ex.Message}");
        }

        var site = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));
        if (site is null)
            drift.Add($"Site is missing: {desired.ProjectName}.");
        else
        {
            var lockedPhp = desired.Runtimes.TryGetValue("php", out var php) ? php : null;
            if (!string.Equals(site.PhpVersion, lockedPhp, StringComparison.OrdinalIgnoreCase))
                drift.Add($"Site PHP drift: locked {lockedPhp ?? "global"}, current {site.PhpVersion ?? "global"}.");
            if (!site.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase))
                drift.Add($"Site domain drift: locked {desired.Domain}, current {site.Domain}.");
            if (site.HttpsEnabled != desired.Https)
                drift.Add($"Site HTTPS drift: locked {desired.Https}, current {site.HttpsEnabled}.");
        }

        foreach (var addonKey in desired.Addons)
        {
            var addon = _addons.GetAddons().FirstOrDefault(item => item.Key.Equals(addonKey, StringComparison.OrdinalIgnoreCase));
            if (addon is null)
                drift.Add($"ADDON catalog entry is missing: {addonKey}.");
            else if (!_addons.IsInstalled(addon))
                drift.Add($"ADDON is not installed: {addonKey}.");
        }

        var currentActions = _actions.GetActions(root);
        if (!ActionsEqual(currentActions, desired.Actions))
            drift.Add("Project actions differ from devbox.lock.json.");

        if (desired.Https && !new LocalCertificateManager(_rootPath).IsMaterialValid(desired.Domain))
            drift.Add($"HTTPS certificate material is missing, invalid, expired, mismatched, or issued for another domain: {desired.Domain}.");

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

    private async Task<EnvironmentApplyResult> ApplyDesiredStateAsync(string root, EnvironmentLockFile desired, CancellationToken cancellationToken)
    {
        var applied = new List<string>();
        var warnings = new List<string>();

        foreach (var pin in desired.Runtimes)
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

        if (!desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(desired.Database.Version))
        {
            try
            {
                var database = await _databaseRuntimes.EnsureInitializedAsync(desired.Database.Engine, desired.Database.Version, desired.Database.Port, cancellationToken).ConfigureAwait(false);
                applied.Add($"Prepared {desired.Database.Engine} {desired.Database.Version} on port {database.Port}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                warnings.Add($"Database runtime {desired.Database.Engine} {desired.Database.Version}: {ex.Message}");
            }
        }

        foreach (var addonKey in desired.Addons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var addon = _addons.GetAddons().FirstOrDefault(item => item.Key.Equals(addonKey, StringComparison.OrdinalIgnoreCase));
                if (addon is null)
                {
                    warnings.Add($"ADDON '{addonKey}' is not present in the local catalog.");
                    continue;
                }
                if (!_addons.IsInstalled(addon))
                {
                    using var installer = new AddonInstaller(_rootPath);
                    await installer.InstallAsync(addon, cancellationToken).ConfigureAwait(false);
                    applied.Add($"Installed ADDON {addon.DisplayName} {addon.Version}.");
                }
                else
                {
                    applied.Add($"Verified ADDON {addon.DisplayName} {addon.Version}.");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or HttpRequestException)
            {
                warnings.Add($"ADDON {addonKey}: {ex.Message}");
            }
        }

        foreach (var serviceKey in desired.Services)
        {
            try
            {
                var template = serviceKey.ToLowerInvariant() switch
                {
                    "mailpit" => ManagedServiceCatalog.MailpitTemplate(),
                    "redis" => ManagedServiceCatalog.RedisTemplate(),
                    _ => null
                };
                if (template is null)
                {
                    if (!_managedServices.GetManifests().Any(item => item.Key.Equals(serviceKey, StringComparison.OrdinalIgnoreCase)))
                        warnings.Add($"Managed service '{serviceKey}' has no known template or existing definition.");
                    continue;
                }
                var executable = Path.GetFullPath(Path.Combine(_rootPath, template.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                _managedServices.Upsert(template with { Enabled = File.Exists(executable) });
                applied.Add($"Synchronized managed service {serviceKey}.");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                warnings.Add($"Managed service {serviceKey}: {ex.Message}");
            }
        }

        if (warnings.Count > 0)
        {
            warnings.Add("Project metadata was left unchanged because one or more environment prerequisites failed.");
            return new EnvironmentApplyResult(desired, applied, warnings);
        }

        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);
        var previousManifestBytes = File.ReadAllBytes(manifestPath);
        var previousActions = _actions.GetActions(root);
        var previousSite = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var desiredTlsState = tlsRollback.Capture(desired.Domain);
        TlsRollbackState? previousTlsState = previousSite is not null && !previousSite.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase)
            ? tlsRollback.Capture(previousSite.Domain)
            : null;

        try
        {
            SynchronizeSite(root, desired);
            applied.Add($"Synchronized Site {desired.Domain}.");

            var manifest = ReadManifestObject(root);
            UpdateManifestFromLock(manifest, desired);
            AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));
            applied.Add("Synchronized devbox.json with devbox.lock.json.");

            _actions.SetActions(root, desired.Actions);
            applied.Add($"Synchronized {desired.Actions.Count} project action(s).");
            return new EnvironmentApplyResult(desired, applied, warnings);
        }
        catch (Exception original)
        {
            var rollbackActions = new List<Action>
            {
                () => RestoreBytes(manifestPath, previousManifestBytes),
                () => _actions.SetActions(root, previousActions),
                () => RestoreSite(previousSite, desired.ProjectName),
                () => tlsRollback.Restore(desiredTlsState)
            };
            if (previousTlsState is not null)
                rollbackActions.Add(() => tlsRollback.Restore(previousTlsState));
            RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());
            throw new InvalidOperationException("Environment rollback executor returned unexpectedly.");
        }
    }

    private void SynchronizeSite(string root, EnvironmentLockFile desired)
    {
        var detection = _workspace.Detect(root);
        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(root, "public"))
            ? Path.Combine(root, "public")
            : root;
        var phpVersion = desired.Runtimes.TryGetValue("php", out var php) ? php : null;
        var previous = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(desired.ProjectName, StringComparison.OrdinalIgnoreCase));

        if (previous is null)
            _ = _sites.Create(desired.ProjectName, desired.Domain, documentRoot);

        if (desired.Https)
        {
            if (OperatingSystem.IsWindows())
            {
                using var authority = new LocalCertificateAuthorityService(_rootPath);
                using var certificate = authority.IssueSiteCertificate(desired.Domain);
            }
            else
            {
                _ = new LocalCertificateManager(_rootPath).Ensure(desired.Domain);
            }
        }
        else
        {
            new LocalCertificateManager(_rootPath).Delete(desired.Domain);
        }

        var updated = new SiteDefinition(desired.ProjectName, desired.Domain, documentRoot, "php", phpVersion, desired.Https);
        _ = _sites.Update(updated);

        if (previous is not null && !previous.Domain.Equals(desired.Domain, StringComparison.OrdinalIgnoreCase))
        {
            var oldDomainStillUsed = _sites.GetSites().Any(item =>
                !item.Name.Equals(previous.Name, StringComparison.OrdinalIgnoreCase) &&
                item.Domain.Equals(previous.Domain, StringComparison.OrdinalIgnoreCase));
            if (!oldDomainStillUsed)
                new LocalCertificateManager(_rootPath).Delete(previous.Domain);
        }
    }

    private void RestoreSite(SiteDefinition? previous, string projectName)
    {
        var current = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (previous is null)
        {
            if (current is not null)
                _sites.Delete(projectName);
            return;
        }

        if (current is null)
            _ = _sites.Create(previous.Name, previous.Domain, previous.DocumentRoot);
        _ = _sites.Update(previous);
    }

    private static void RestoreBytes(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + $".{Guid.NewGuid():N}.rollback";
        try
        {
            File.WriteAllBytes(temp, content);
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

    private static void UpdateManifestFromLock(JsonObject manifest, EnvironmentLockFile desired)
    {
        var phpVersion = desired.Runtimes.TryGetValue("php", out var php) ? php : null;
        var nodeVersion = desired.Runtimes.TryGetValue("node", out var node) ? node : null;
        SetProperty(manifest, "Name", desired.ProjectName);
        SetProperty(manifest, "Domain", desired.Domain);
        SetProperty(manifest, "PhpVersion", phpVersion);
        SetProperty(manifest, "NodeVersion", nodeVersion);
        SetProperty(manifest, "DatabaseEngine", desired.Database.Engine.ToLowerInvariant());
        SetProperty(manifest, "DatabaseName", desired.Database.Engine.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : desired.Database.DatabaseName);
        SetProperty(manifest, "Https", desired.Https);
        SetProperty(manifest, "Addons", JsonSerializer.SerializeToNode(desired.Addons, JsonOptions));
        SetProperty(manifest, "Services", JsonSerializer.SerializeToNode(desired.Services, JsonOptions));
        manifest["Environment"] = new JsonObject
        {
            ["Profile"] = desired.SourceProfile,
            ["Runtimes"] = JsonSerializer.SerializeToNode(desired.Runtimes, JsonOptions),
            ["DatabaseVersion"] = desired.Database.Version,
            ["DatabasePort"] = desired.Database.Port
        };
    }

    private static void CompareManifest(EnvironmentLockFile desired, JsonObject manifest, List<string> drift)
    {
        var php = desired.Runtimes.TryGetValue("php", out var phpVersion) ? phpVersion : null;
        var node = desired.Runtimes.TryGetValue("node", out var nodeVersion) ? nodeVersion : null;
        if (!string.Equals(GetString(manifest, "Name"), desired.ProjectName, StringComparison.OrdinalIgnoreCase))
            drift.Add("Project name differs from devbox.lock.json.");
        if (!string.Equals(GetString(manifest, "Domain"), desired.Domain, StringComparison.OrdinalIgnoreCase))
            drift.Add("Project domain differs from devbox.lock.json.");
        if (!string.Equals(GetString(manifest, "PhpVersion"), php, StringComparison.OrdinalIgnoreCase))
            drift.Add("Manifest PHP pin differs from devbox.lock.json.");
        if (!string.Equals(GetString(manifest, "NodeVersion"), node, StringComparison.OrdinalIgnoreCase))
            drift.Add("Manifest Node.js pin differs from devbox.lock.json.");
        if (!string.Equals(GetString(manifest, "DatabaseEngine") ?? "none", desired.Database.Engine, StringComparison.OrdinalIgnoreCase))
            drift.Add("Manifest database engine differs from devbox.lock.json.");
        if (!string.Equals(GetString(manifest, "DatabaseName"), desired.Database.DatabaseName, StringComparison.OrdinalIgnoreCase))
            drift.Add("Manifest database name differs from devbox.lock.json.");
        if ((GetBool(manifest, "Https") ?? false) != desired.Https)
            drift.Add("Manifest HTTPS state differs from devbox.lock.json.");
        if (!SetEqual(GetStringArray(manifest, "Addons"), desired.Addons))
            drift.Add("Manifest ADDONS differ from devbox.lock.json.");
        if (!SetEqual(GetStringArray(manifest, "Services"), desired.Services))
            drift.Add("Manifest services differ from devbox.lock.json.");
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
        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!root.Equals(www, StringComparison.OrdinalIgnoreCase) && !root.StartsWith(www + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Environment operations are restricted to the DevBox www directory.");
        return root;
    }

    private string? ReadActiveVersion(string key)
    {
        var marker = Path.Combine(_rootPath, "runtime", key, "current", ".devbox-version");
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static string? ReadEnvironmentRuntime(JsonObject manifest, string key)
    {
        var environment = FindProperty(manifest, "Environment") as JsonObject;
        var runtimes = environment is null ? null : FindProperty(environment, "Runtimes") as JsonObject;
        return runtimes is null ? null : GetString(runtimes, key);
    }

    private static string? ReadEnvironmentDatabaseVersion(JsonObject manifest)
    {
        var environment = FindProperty(manifest, "Environment") as JsonObject;
        return environment is null ? null : GetString(environment, "DatabaseVersion");
    }

    private static int? ReadEnvironmentDatabasePort(JsonObject manifest)
    {
        var environment = FindProperty(manifest, "Environment") as JsonObject;
        var value = environment is null ? null : FindProperty(environment, "DatabasePort") as JsonValue;
        return value is not null && value.TryGetValue<int>(out var port) ? port : null;
    }

    private static JsonNode? FindProperty(JsonObject value, string name)
    {
        foreach (var pair in value)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    private static string? GetString(JsonObject value, string name) =>
        FindProperty(value, name) is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;

    private static bool? GetBool(JsonObject value, string name) =>
        FindProperty(value, name) is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : null;

    private static IReadOnlyList<string> GetStringArray(JsonObject value, string name)
    {
        var node = FindProperty(value, name);
        if (node is null)
            return Array.Empty<string>();
        try
        {
            return node.Deserialize<string[]>(JsonOptions) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static void SetProperty(JsonObject value, string name, JsonNode? propertyValue)
    {
        var existing = value.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        value[string.IsNullOrEmpty(existing) ? name : existing] = propertyValue?.DeepClone();
    }

    private static void SetProperty(JsonObject value, string name, string? propertyValue) => SetProperty(value, name, JsonValue.Create(propertyValue));
    private static void SetProperty(JsonObject value, string name, bool propertyValue) => SetProperty(value, name, JsonValue.Create(propertyValue));

    private static IReadOnlyList<string> MergeKeys(IReadOnlyList<string> first, IReadOnlyList<string>? second) =>
        first.Concat(second ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void AddIfNotEmpty(IDictionary<string, string> values, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            values[key] = value.Trim();
    }

    private static bool SetEqual(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        new HashSet<string>(first, StringComparer.OrdinalIgnoreCase).SetEquals(second);

    private static bool ActionsEqual(IReadOnlyList<ProjectActionDefinition> first, IReadOnlyList<ProjectActionDefinition> second)
    {
        if (first.Count != second.Count)
            return false;
        for (var i = 0; i < first.Count; i++)
        {
            if (!string.Equals(JsonSerializer.Serialize(first[i], JsonOptions), JsonSerializer.Serialize(second[i], JsonOptions), StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string SafeDatabaseName(string value)
    {
        var result = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray());
        return string.IsNullOrWhiteSpace(result) ? "devbox" : result;
    }

    private static void ValidateLock(EnvironmentLockFile value)
    {
        if (value.SchemaVersion != EnvironmentLockFile.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported devbox.lock.json schema version: {value.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(value.ProjectName) || string.IsNullOrWhiteSpace(value.Domain) || !value.Domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Environment lock project identity is invalid.");
        if (value.Runtimes.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new InvalidDataException("Environment lock contains an invalid runtime pin.");
        if (value.Database.Engine is not ("mysql" or "mariadb" or "postgresql" or "none"))
            throw new InvalidDataException("Environment lock database engine is invalid.");
        if (value.Database.Port is < 1 or > 65535)
            throw new InvalidDataException("Environment lock database port is invalid.");
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
