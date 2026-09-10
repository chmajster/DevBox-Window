using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class EnvironmentReadinessService
{
    private readonly string _rootPath;
    private readonly RuntimeCatalog _runtimeCatalog;

    public EnvironmentReadinessService(string rootPath, RuntimeCatalog runtimeCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _runtimeCatalog = runtimeCatalog ?? throw new ArgumentNullException(nameof(runtimeCatalog));
    }

    public EnvironmentReadiness Check()
    {
        var automaticallyInstallable = _runtimeCatalog.GetRecommendedWindowsRuntimes()
            .Where(runtime => runtime.HasRemotePackage || BundledRuntimeExists(runtime))
            .Select(runtime => runtime.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new EnvironmentReadiness(
        [
            Runtime("nginx", "Nginx", "nginx.exe", automaticallyInstallable),
            Runtime("php", "PHP FastCGI", "php-cgi.exe", automaticallyInstallable),
            Runtime("mysql", "MySQL", Path.Combine("bin", "mysqld.exe"), automaticallyInstallable),
            FileCheck("config", "Nginx config", Path.Combine("config", "nginx", "nginx.conf")),
            FileCheck("config", "PHP config", Path.Combine("config", "php", "php.ini")),
            FileCheck("config", "MySQL config", Path.Combine("config", "mysql", "my.ini"))
        ]);
    }

    private bool BundledRuntimeExists(RuntimeDefinition runtime)
    {
        var executablePath = Path.Combine(
            _rootPath,
            "runtime",
            runtime.Key,
            runtime.Version,
            runtime.ExecutableRelativePath);
        return File.Exists(executablePath);
    }

    private EnvironmentReadinessItem Runtime(
        string key,
        string displayName,
        string executableRelativePath,
        IReadOnlySet<string> automaticallyInstallable)
    {
        var path = Path.Combine(_rootPath, "runtime", key, "current", executableRelativePath);
        var ready = File.Exists(path);
        return new EnvironmentReadinessItem(
            key,
            displayName,
            ready,
            ready ? path : $"Missing: {path}",
            !ready && automaticallyInstallable.Contains(key));
    }

    private EnvironmentReadinessItem FileCheck(string key, string displayName, string relativePath)
    {
        var path = Path.Combine(_rootPath, relativePath);
        var ready = File.Exists(path);
        return new EnvironmentReadinessItem(
            key,
            displayName,
            ready,
            ready ? path : $"Missing: {path}",
            false);
    }
}
