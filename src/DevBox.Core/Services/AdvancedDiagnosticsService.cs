using System.Net.NetworkInformation;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AdvancedDiagnosticsService
{
    private readonly string _rootPath;

    public AdvancedDiagnosticsService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public AdvancedDiagnosticReport Run()
    {
        var findings = new List<AdvancedDiagnosticFinding>();
        CheckDirectories(findings);
        CheckDiskSpace(findings);
        CheckRuntimeCatalog(findings);
        CheckServices(findings);
        CheckSites(findings);
        CheckProjectLocks(findings);
        CheckConfigurationFiles(findings);
        CheckTemporaryArtifacts(findings);
        return new AdvancedDiagnosticReport(
            DateTimeOffset.UtcNow,
            findings
                .OrderByDescending(item => item.Severity)
                .ThenBy(item => item.Area, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private void CheckDirectories(List<AdvancedDiagnosticFinding> findings)
    {
        foreach (var relative in new[] { "runtime", "config", "www", "logs", "tmp", "backups" })
        {
            var path = Path.Combine(_rootPath, relative);
            if (!Directory.Exists(path))
            {
                findings.Add(Error($"directory-{relative}", "Filesystem", $"{relative} directory is missing.", path, "Run DevBox setup/repair."));
                continue;
            }

            try
            {
                _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
                    _rootPath,
                    path,
                    $"{relative} directory cannot be a reparse point or escape the DevBox root.");
                findings.Add(Info($"directory-{relative}", "Filesystem", $"{relative} directory is available.", path));
            }
            catch (InvalidOperationException ex)
            {
                findings.Add(Error(
                    $"directory-{relative}-unsafe",
                    "Filesystem",
                    $"{relative} directory is unsafe.",
                    ex.Message,
                    "Replace the junction/symbolic link with a real DevBox-managed directory."));
            }
        }
    }

    private void CheckDiskSpace(List<AdvancedDiagnosticFinding> findings)
    {
        try
        {
            var root = Path.GetPathRoot(_rootPath);
            if (string.IsNullOrWhiteSpace(root))
                return;
            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;
            var freeGiB = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
            findings.Add(freeGiB switch
            {
                < 1 => Error("disk-space", "Filesystem", "Critical free disk space.", $"{freeGiB:F2} GiB available on {drive.Name}.", "Free at least several GiB before installing runtimes or creating backups."),
                < 5 => Warning("disk-space", "Filesystem", "Low free disk space.", $"{freeGiB:F2} GiB available on {drive.Name}.", "Free disk space before large runtime installs/backups."),
                _ => Info("disk-space", "Filesystem", "Disk space is sufficient.", $"{freeGiB:F2} GiB available on {drive.Name}.")
            });
        }
        catch (IOException ex)
        {
            findings.Add(Warning("disk-space-read", "Filesystem", "Unable to inspect free disk space.", ex.Message));
        }
        catch (UnauthorizedAccessException ex)
        {
            findings.Add(Warning("disk-space-access", "Filesystem", "Unable to inspect free disk space.", ex.Message));
        }
    }

    private void CheckRuntimeCatalog(List<AdvancedDiagnosticFinding> findings)
    {
        try
        {
            using var platform = new RuntimePlatformService(_rootPath);
            foreach (var status in platform.GetStatuses())
            {
                if (!status.Installed)
                    continue;
                if (!status.Valid)
                {
                    findings.Add(Error($"runtime-{status.Package.Key}-{status.Package.Version}", "Runtimes", "Installed runtime is invalid.", $"{status.Package.DisplayName} {status.Package.Version} is missing its expected executable.", "Repair or reinstall this runtime version."));
                    continue;
                }
                if (status.SupportState == RuntimeSupportState.EndOfLife)
                    findings.Add(Warning($"runtime-eol-{status.Package.Key}-{status.Package.Version}", "Runtimes", "Installed runtime is end-of-life.", $"{status.Package.DisplayName} {status.Package.Version}", "Move projects to a supported runtime version."));
                else
                    findings.Add(Info($"runtime-{status.Package.Key}-{status.Package.Version}", "Runtimes", "Runtime installation is valid.", $"{status.Package.DisplayName} {status.Package.Version}{(status.Active ? " (active)" : string.Empty)}."));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        {
            findings.Add(Error("runtime-catalog", "Runtimes", "Runtime catalog could not be inspected.", ex.Message, "Repair the runtime/catalog path or remove the unsafe entry."));
        }
    }

    private void CheckServices(List<AdvancedDiagnosticFinding> findings)
    {
        IReadOnlyList<ServiceDefinition> services;
        try
        {
            services = new ServiceCatalog(_rootPath).GetServices();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            findings.Add(Error("service-catalog", "Services", "Service catalog is invalid.", ex.Message, "Repair config/services.json."));
            return;
        }

        var duplicatePorts = services.Where(item => item.Port > 0).GroupBy(item => item.Port).Where(group => group.Count() > 1).ToArray();
        foreach (var duplicate in duplicatePorts)
            findings.Add(Error($"service-port-{duplicate.Key}", "Services", "Duplicate configured service port.", $"Port {duplicate.Key}: {string.Join(", ", duplicate.Select(item => item.Key))}.", "Assign unique ports before starting services."));

        using var processes = new ProcessManager();
        foreach (var service in services)
        {
            var status = processes.GetStatus(service);
            if (!File.Exists(service.ExecutablePath))
                findings.Add(Warning($"service-runtime-{service.Key}", "Services", $"{service.DisplayName} runtime is missing.", service.ExecutablePath, "Install or repair the required runtime."));
            else
                findings.Add(Info($"service-{service.Key}", "Services", $"{service.DisplayName}: {status.State}.", $"Port {service.Port}; PID {(status.ProcessId?.ToString() ?? "-")}; version {service.Version}."));
        }

        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(item => item.Port).ToHashSet();
        foreach (var service in services.Where(item => item.Port > 0 && listeners.Contains(item.Port)))
        {
            var state = processes.GetStatus(service).State;
            if (state != ServiceState.Running)
                findings.Add(Warning($"port-external-{service.Port}", "Networking", "Configured DevBox port is already occupied by another process.", $"{service.DisplayName} expects TCP {service.Port} but DevBox does not own a running service on that port.", "Stop the external process or change the DevBox service port."));
        }
    }

    private void CheckSites(List<AdvancedDiagnosticFinding> findings)
    {
        IReadOnlyList<SiteDefinition> sites;
        try
        {
            sites = new SiteManager(_rootPath).GetSites();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            findings.Add(Error("sites-metadata", "Sites", "Sites metadata could not be loaded.", ex.Message, "Use the Sites repair workflow and inspect quarantined metadata."));
            return;
        }

        var duplicateDomains = sites.GroupBy(item => item.Domain, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);
        foreach (var duplicate in duplicateDomains)
            findings.Add(Error($"site-domain-{duplicate.Key}", "Sites", "Duplicate Site domain.", duplicate.Key, "Assign a unique .test domain to every Site."));

        var duplicatePhpPorts = sites
            .Where(item => !string.IsNullOrWhiteSpace(item.PhpVersion))
            .GroupBy(item => item.PhpPort)
            .Where(group => group.Count() > 1);
        foreach (var duplicate in duplicatePhpPorts)
            findings.Add(Error($"site-php-port-{duplicate.Key}", "Sites", "Duplicate per-Site PHP FastCGI port.", $"Port {duplicate.Key}: {string.Join(", ", duplicate.Select(item => item.Name))}.", "Repair Sites metadata to allocate unique PHP ports."));

        foreach (var site in sites)
        {
            if (!Directory.Exists(site.DocumentRoot))
                findings.Add(Error($"site-root-{site.Name}", "Sites", "Site document root is missing.", site.DocumentRoot, "Restore the project or update the Site document root."));
            var vhost = new SiteManager(_rootPath).GetNginxConfigPath(site.Domain);
            if (!File.Exists(vhost))
                findings.Add(Warning($"site-vhost-{site.Name}", "Sites", "Generated Nginx vhost is missing.", vhost, "Run Project/Site repair."));
            if (site.HttpsEnabled)
            {
                var certificates = new LocalCertificateManager(_rootPath);
                if (!certificates.IsMaterialValid(site.Domain))
                    findings.Add(Error($"site-tls-{site.Name}", "TLS", "HTTPS is enabled but certificate material is missing, invalid, expired, mismatched, or issued for another domain.", site.Domain, "Reissue the Site certificate."));
            }
        }
    }

    private void CheckProjectLocks(List<AdvancedDiagnosticFinding> findings)
    {
        var www = Path.Combine(_rootPath, "www");
        if (!Directory.Exists(www))
            return;
        try
        {
            _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
                _rootPath,
                www,
                "Project diagnostics cannot traverse a reparse-point www directory.");
        }
        catch (InvalidOperationException ex)
        {
            findings.Add(Error("projects-root-unsafe", "Projects", "Project root is unsafe.", ex.Message, "Replace the www junction/symbolic link with a real DevBox-managed directory."));
            return;
        }

        foreach (var directory in Directory.GetDirectories(www))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                findings.Add(Error($"project-root-{Path.GetFileName(directory)}-unsafe", "Projects", "Project directory is a reparse point.", directory, "Move the project into a real directory inside DevBox www."));
                continue;
            }
            var manifest = Path.Combine(directory, ProjectWorkspaceService.ManifestFileName);
            var lockPath = Path.Combine(directory, EnvironmentLockService.LockFileName);
            if (!File.Exists(manifest))
                continue;
            if (!File.Exists(lockPath))
            {
                findings.Add(Warning($"project-lock-{Path.GetFileName(directory)}", "Projects", "Project has no environment lock.", directory, "Generate devbox.lock.json to make the environment reproducible."));
                continue;
            }
            try
            {
                using var locks = new EnvironmentLockService(_rootPath);
                var drift = locks.GetDrift(directory);
                if (drift.Count == 0)
                    findings.Add(Info($"project-drift-{Path.GetFileName(directory)}", "Projects", "Project environment matches its lock.", directory));
                else
                    findings.Add(Warning($"project-drift-{Path.GetFileName(directory)}", "Projects", "Project environment drift detected.", string.Join(" | ", drift.Take(10)), "Apply devbox.lock.json to restore the pinned environment."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
            {
                findings.Add(Error($"project-lock-invalid-{Path.GetFileName(directory)}", "Projects", "Project environment lock could not be checked.", ex.Message, "Repair or regenerate devbox.lock.json."));
            }
        }
    }

    private void CheckConfigurationFiles(List<AdvancedDiagnosticFinding> findings)
    {
        var service = new ConfigurationFileService(_rootPath);
        foreach (var key in service.GetKnownConfigurations())
        {
            try
            {
                var path = service.GetPath(key);
                if (!File.Exists(path))
                    findings.Add(Error($"config-{key}", "Configuration", $"{key} configuration is missing.", path, "Run DevBox setup/repair."));
                else if (new FileInfo(path).Length == 0)
                    findings.Add(Error($"config-{key}-empty", "Configuration", $"{key} configuration is empty.", path, "Restore a configuration backup or regenerate defaults."));
                else
                    findings.Add(Info($"config-{key}", "Configuration", $"{key} configuration is present.", path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                findings.Add(Error($"config-{key}-unsafe", "Configuration", $"{key} configuration path is unsafe or unavailable.", ex.Message, "Replace reparse points and repair the DevBox configuration directory."));
            }
        }
    }

    private void CheckTemporaryArtifacts(List<AdvancedDiagnosticFinding> findings)
    {
        var stale = new List<string>();
        foreach (var root in new[] { Path.Combine(_rootPath, "runtime"), Path.Combine(_rootPath, "tmp") })
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                _ = PathSafety.EnsureUnderRootWithoutReparsePoints(
                    _rootPath,
                    root,
                    "Temporary-artifact scan cannot traverse a reparse-point root.");
                foreach (var path in EnumerateDirectoriesWithoutReparsePoints(root))
                {
                    var name = Path.GetFileName(path);
                    if (!(name.Contains(".backup-", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".current-", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    if (Directory.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddHours(-24))
                        continue;
                    stale.Add(path);
                    if (stale.Count >= 50)
                        break;
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
            {
                findings.Add(Warning("temp-scan-access", "Filesystem", "Some temporary directories could not be scanned safely.", ex.Message));
            }
            if (stale.Count >= 50)
                break;
        }
        if (stale.Count > 0)
            findings.Add(Warning("stale-transaction-artifacts", "Filesystem", "Stale transaction directories were found.", string.Join(" | ", stale.Take(10)), "Remove them after confirming no DevBox operation is running."));
    }

    private static IEnumerable<string> EnumerateDirectoriesWithoutReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;
                yield return child;
                pending.Push(child);
            }
        }
    }

    private static AdvancedDiagnosticFinding Info(string key, string area, string summary, string details) => new(key, area, DiagnosticSeverity.Info, summary, details);
    private static AdvancedDiagnosticFinding Warning(string key, string area, string summary, string details, string? action = null) => new(key, area, DiagnosticSeverity.Warning, summary, details, action);
    private static AdvancedDiagnosticFinding Error(string key, string area, string summary, string details, string? action = null) => new(key, area, DiagnosticSeverity.Error, summary, details, action);
}
