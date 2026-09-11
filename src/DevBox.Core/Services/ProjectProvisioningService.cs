using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectProvisioningService
{
    private readonly string _rootPath;
    private readonly ProjectWorkspaceService _workspace;
    private readonly SiteManager _sites;
    private readonly ProjectDatabaseProvisioner _projectDatabases;
    private readonly ManagedServiceCatalog _managedServices;

    public ProjectProvisioningService(
        string rootPath,
        ProjectWorkspaceService workspace,
        DatabaseManager databases,
        ManagedServiceCatalog managedServices)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _sites = new SiteManager(_rootPath);
        ArgumentNullException.ThrowIfNull(databases);
        _projectDatabases = new ProjectDatabaseProvisioner(_rootPath, databases);
        _managedServices = managedServices ?? throw new ArgumentNullException(nameof(managedServices));
    }

    public async Task<ProjectProvisioningResult> ProvisionAsync(
        ProjectCreateRequest request,
        DatabaseConnectionOptions? databaseOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actions = new List<string>();
        var warnings = new List<string>();

        var expectedProjectRoot = Path.Combine(_rootPath, "www", request.Name.Trim().ToLowerInvariant());
        var projectRootExisted = Directory.Exists(expectedProjectRoot);
        var site = _workspace.Create(request);
        actions.Add($"Created Site {site.Domain}.");

        var projectRoot = _workspace.ResolveProjectRoot(site.DocumentRoot);
        try
        {
            var manifest = _workspace.LoadManifest(projectRoot)
                ?? throw new InvalidDataException("Project manifest was not created.");
            manifest = manifest with
            {
                Addons = NormalizeKeys(request.Addons),
                Services = NormalizeKeys(request.Services)
            };
            _workspace.SaveManifest(projectRoot, manifest);
            actions.Add("Saved devbox.json project manifest.");

            if (!string.IsNullOrWhiteSpace(manifest.NodeVersion))
            {
                var nodeExe = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "node.exe");
                var npmCmd = Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "npm.cmd");
                if (File.Exists(nodeExe) && File.Exists(npmCmd))
                    actions.Add($"Pinned Node.js {manifest.NodeVersion} is available for project commands.");
                else
                    warnings.Add($"Node.js {manifest.NodeVersion} is pinned by the profile but is not installed. Install the portable Node LTS runtime from Developer Tools before running npm presets.");
            }

            if (!manifest.DatabaseEngine.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(manifest.DatabaseName))
            {
                if (_projectDatabases.IsAvailable(manifest.DatabaseEngine))
                {
                    await _projectDatabases.EnsureDatabaseAsync(
                        manifest.DatabaseEngine,
                        manifest.DatabaseName,
                        databaseOptions,
                        cancellationToken).ConfigureAwait(false);
                    actions.Add($"Ensured {DisplayEngine(manifest.DatabaseEngine)} database {manifest.DatabaseName}.");
                }
                else
                {
                    warnings.Add($"{DisplayEngine(manifest.DatabaseEngine)} database was declared but its native client runtime is not available yet.");
                }
            }

            foreach (var serviceKey in manifest.Services)
            {
                var template = ResolveManagedServiceTemplate(serviceKey);
                if (template is null)
                {
                    warnings.Add($"Managed service '{serviceKey}' has no built-in template and was left declarative only.");
                    continue;
                }

                var executablePath = Path.GetFullPath(Path.Combine(_rootPath, template.ExecutableRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                var enabled = File.Exists(executablePath);
                _managedServices.Upsert(template with { Enabled = enabled });
                if (enabled)
                    actions.Add($"Registered managed service {template.DisplayName}.");
                else
                    warnings.Add($"{template.DisplayName} is required by the profile but its runtime is not installed. The service definition was registered disabled.");
            }

            return new ProjectProvisioningResult(site, manifest, actions, warnings);
        }
        catch (Exception original)
        {
            RollbackExecutor.RethrowAfterRollback(
                original,
                () =>
                {
                    var current = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(site.Name, StringComparison.OrdinalIgnoreCase));
                    if (current is not null)
                        _sites.Delete(current.Name);
                },
                () => RollbackProjectDirectory(projectRoot, projectRootExisted));
            throw new InvalidOperationException("Project provisioning rollback executor returned unexpectedly.");
        }
    }


    private static void RollbackProjectDirectory(string projectRoot, bool existedBefore)
    {
        if (!Directory.Exists(projectRoot))
            return;
        if (!existedBefore)
        {
            Directory.Delete(projectRoot, recursive: true);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(projectRoot))
            File.Delete(file);
        foreach (var directory in Directory.EnumerateDirectories(projectRoot))
            Directory.Delete(directory, recursive: true);
    }

    private static string DisplayEngine(string engine) => engine.ToLowerInvariant() switch
    {
        "mysql" => "MySQL",
        "mariadb" => "MariaDB",
        "postgresql" => "PostgreSQL",
        _ => engine
    };

    private static ManagedServiceManifest? ResolveManagedServiceTemplate(string key) =>
        key.ToLowerInvariant() switch
        {
            "mailpit" => ManagedServiceCatalog.MailpitTemplate(),
            "redis" => ManagedServiceCatalog.RedisTemplate(),
            _ => null
        };

    private static IReadOnlyList<string> NormalizeKeys(IReadOnlyList<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
