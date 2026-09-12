using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class DiagnosticsService
{
    private readonly string _rootPath;
    private readonly ServiceCatalog _serviceCatalog;
    private readonly IProcessManager _processManager;

    public DiagnosticsService(string rootPath, ServiceCatalog serviceCatalog, IProcessManager processManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _serviceCatalog = serviceCatalog ?? throw new ArgumentNullException(nameof(serviceCatalog));
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
    }

    public IReadOnlyList<DiagnosticCheck> Run()
    {
        var results = new List<DiagnosticCheck>
        {
            CheckRootWritable(),
            CheckFile("Configuration", "Nginx configuration", Path.Combine(_rootPath, "config", "nginx", "nginx.conf")),
            CheckFile("Configuration", "PHP configuration", Path.Combine(_rootPath, "config", "php", "php.ini")),
            CheckFile("Configuration", "MySQL configuration", Path.Combine(_rootPath, "config", "mysql", "my.ini"))
        };

        IReadOnlyList<ServiceDefinition> services;
        try
        {
            services = _serviceCatalog.GetDefaultServices();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            results.Add(new DiagnosticCheck(
                "Configuration",
                "Managed services configuration",
                false,
                $"Optional managed services were ignored: {ex.Message}",
                DiagnosticSeverity.Warning));
            services = _serviceCatalog.GetCoreServices();
        }

        foreach (var service in services)
        {
            var runtimeExists = File.Exists(service.ExecutablePath);
            results.Add(new DiagnosticCheck(
                "Runtime",
                $"{service.DisplayName} executable",
                runtimeExists,
                runtimeExists ? service.ExecutablePath : $"Missing: {service.ExecutablePath}"));

            if (!runtimeExists)
            {
                continue;
            }

            var status = _processManager.GetStatus(service);
            var running = status.State == ServiceState.Running;
            results.Add(new DiagnosticCheck(
                "Service",
                service.DisplayName,
                running,
                running
                    ? $"Running on port {status.Port}, PID {status.ProcessId}."
                    : $"State: {status.State}.",
                running ? DiagnosticSeverity.Info : DiagnosticSeverity.Warning));
        }

        return results;
    }

    private DiagnosticCheck CheckRootWritable()
    {
        var probeDirectory = Path.Combine(_rootPath, "tmp");
        var probePath = Path.Combine(probeDirectory, $"write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(probeDirectory);
            File.WriteAllText(probePath, "DevBox");
            File.Delete(probePath);
            return new DiagnosticCheck("Filesystem", "DevBox root writable", true, _rootPath, DiagnosticSeverity.Info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticCheck("Filesystem", "DevBox root writable", false, ex.Message);
        }
        finally
        {
            if (File.Exists(probePath))
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static DiagnosticCheck CheckFile(string category, string name, string path)
    {
        var exists = File.Exists(path);
        return new DiagnosticCheck(category, name, exists, exists ? path : $"Missing: {path}");
    }
}
