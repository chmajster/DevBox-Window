using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectProvisioningService
{
    private readonly string _rootPath;
    private readonly ProjectWorkspaceService _workspace;
    private readonly DatabaseManager _databases;
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
        _databases = databases ?? throw new ArgumentNullException(nameof(databases));
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

        var site = _workspace.Create(request);
        actions.Add($"Created Site {site.Domain}.");

        var projectRoot = _workspace.ResolveProjectRoot(site.DocumentRoot);
        var manifest = _workspace.LoadManifest(projectRoot)
            ?? throw new InvalidDataException("Project manifest was not created.");
        manifest = manifest with
        {
            Addons = NormalizeKeys(request.Addons),
            Services = NormalizeKeys(request.Services)
        };
        _workspace.SaveManifest(projectRoot, manifest);
        actions.Add("Saved devbox.json project manifest.");

        if (manifest.DatabaseEngine.Equals("mysql", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(manifest.DatabaseName))
        {
            var mysql = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysql.exe");
            if (File.Exists(mysql))
            {
                await _databases.CreateDatabaseAsync(
                    manifest.DatabaseName,
                    databaseOptions ?? new DatabaseConnectionOptions(),
                    cancellationToken).ConfigureAwait(false);
                actions.Add($"Ensured MySQL database {manifest.DatabaseName}.");
            }
            else
            {
                warnings.Add("MySQL database was declared but the active MySQL client runtime is not available yet.");
            }
        }
        else if (!manifest.DatabaseEngine.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                 !manifest.DatabaseEngine.Equals("mysql", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Database engine '{manifest.DatabaseEngine}' is declared in devbox.json but its native provisioning provider is not installed yet.");
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
            {
                actions.Add($"Registered managed service {template.DisplayName}.");
            }
            else
            {
                warnings.Add($"{template.DisplayName} is required by the profile but its runtime is not installed. The service definition was registered disabled.");
            }
        }

        return new ProjectProvisioningResult(site, manifest, actions, warnings);
    }

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
