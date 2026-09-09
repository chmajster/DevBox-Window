using DevBox.Core.Models;

namespace DevBox.App.ViewModels;

public sealed class ServiceRowViewModel : ObservableObject
{
    private string _status = "Stopped";
    private string _portPid;
    private string _uptime = "—";

    public ServiceRowViewModel(ServiceDefinition definition)
    {
        Key = definition.Key;
        Name = definition.DisplayName;
        RuntimePath = definition.ExecutablePath;
        _portPid = $"{definition.Port} / —";
    }

    public string Key { get; }
    public string Name { get; }
    public string RuntimePath { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string PortPid { get => _portPid; private set => SetProperty(ref _portPid, value); }
    public string Uptime { get => _uptime; private set => SetProperty(ref _uptime, value); }

    public void Apply(ServiceSnapshot snapshot, bool runtimeInstalled)
    {
        Status = runtimeInstalled ? snapshot.State.ToString() : "Runtime missing";
        PortPid = $"{snapshot.Port} / {(snapshot.ProcessId?.ToString() ?? "—")}";
        Uptime = snapshot.Uptime is null
            ? "—"
            : $"{(int)snapshot.Uptime.Value.TotalHours:00}:{snapshot.Uptime.Value.Minutes:00}:{snapshot.Uptime.Value.Seconds:00}";
    }
}

public sealed class AddonRowViewModel : ObservableObject
{
    private string _status = "Not installed";
    private string _installAction = "Install";
    private string _hostStatus = "Host: —";
    private string _configStatus = "Config: —";
    private string _phpStatus = "PHP: —";

    public AddonRowViewModel(AddonDefinition definition)
    {
        Key = definition.Key;
        Name = definition.DisplayName;
        Description = definition.Description;
        InstallPath = definition.InstallPath;
        LocalUrl = definition.LocalUrl;
        Requirements = string.Join(", ", definition.RequiredPhpExtensions);
        VersionText = $"Version {definition.Version}";
    }

    public string Key { get; }
    public string Name { get; }
    public string Description { get; }
    public string InstallPath { get; }
    public string LocalUrl { get; }
    public string Requirements { get; }
    public string VersionText { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string InstallAction { get => _installAction; private set => SetProperty(ref _installAction, value); }
    public string HostStatus { get => _hostStatus; private set => SetProperty(ref _hostStatus, value); }
    public string ConfigStatus { get => _configStatus; private set => SetProperty(ref _configStatus, value); }
    public string PhpStatus { get => _phpStatus; private set => SetProperty(ref _phpStatus, value); }

    public void ApplyInstallation(bool installed)
    {
        Status = installed ? "Installed" : "Not installed";
        InstallAction = installed ? "Reinstall" : "Install";
        if (!installed)
        {
            HostStatus = "Host: —";
            ConfigStatus = "Config: —";
            PhpStatus = "PHP: —";
        }
    }

    public void ApplyHealth(bool hostConfigured, bool configPresent, PhpExtensionCheckResult php)
    {
        HostStatus = hostConfigured ? "Host: OK" : "Host: missing";
        ConfigStatus = configPresent ? "Config: OK" : "Config: missing";
        PhpStatus = !php.RuntimeAvailable
            ? "PHP: runtime missing"
            : php.MissingExtensions.Count > 0
                ? $"PHP missing: {string.Join(", ", php.MissingExtensions)}"
                : string.IsNullOrWhiteSpace(php.Error) ? "PHP: OK" : "PHP: check failed";
        Status = hostConfigured && configPresent && php.Success ? "Ready" : "Needs attention";
        InstallAction = "Reinstall";
    }

    public void SetBusy(string text)
    {
        Status = text;
        InstallAction = text;
    }

    public void SetError()
    {
        Status = "Operation failed";
        InstallAction = "Retry";
    }
}

public sealed class SiteRowViewModel(SiteDefinition site)
{
    public string Name { get; } = site.Name;
    public string Domain { get; } = site.Domain;
    public string DocumentRoot { get; } = site.DocumentRoot;
    public string Url { get; } = site.HttpsEnabled ? $"https://{site.Domain}" : $"http://{site.Domain}";
}

public sealed class RuntimeRowViewModel(RuntimeInstallation runtime)
{
    public string Key { get; } = runtime.Key;
    public string Version { get; } = runtime.Version;
    public string Status { get; } = !runtime.IsValid ? "Broken" : runtime.IsActive ? "Active" : "Installed";
    public string InstallPath { get; } = runtime.InstallPath;
}

public sealed class DiagnosticRowViewModel(DiagnosticCheck check)
{
    public string Category { get; } = check.Category;
    public string Name { get; } = check.Name;
    public string Status { get; } = check.Success ? "OK" : check.Severity == DiagnosticSeverity.Warning ? "Warning" : "Error";
    public string Details { get; } = check.Details;
}
