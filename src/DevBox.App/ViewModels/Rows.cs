using System.Windows;
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
        InstallAction = "Retry install";
    }
}

public sealed class SiteRowViewModel(SiteDefinition site)
{
    public string Name { get; } = site.Name;
    public string Domain { get; } = site.Domain;
    public string DocumentRoot { get; } = site.DocumentRoot;
    public string? PhpVersion { get; } = site.PhpVersion;
    public string Url { get; } = site.HttpsEnabled ? $"https://{site.Domain}" : $"http://{site.Domain}";
}

public sealed class RuntimeRowViewModel : ObservableObject
{
    private string _status;
    private bool _canDownload;
    private int _progressPercent;
    private Visibility _progressVisibility = Visibility.Collapsed;

    public RuntimeRowViewModel(RuntimeVersionStatus status, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        Key = status.Package.Key;
        Name = status.Package.DisplayName;
        Version = status.Package.Version;
        IsInstalled = status.Installed;
        _canDownload = !status.Installed &&
                       !string.IsNullOrWhiteSpace(status.Package.DownloadUrl) &&
                       !string.IsNullOrWhiteSpace(status.Package.Sha256);
        CanActivate = status.Installed && status.Valid && !status.Active;
        CanRemove = status.Installed && !status.Active;
        _status = !status.Installed
            ? _canDownload ? "Available online" : "Not installed"
            : !status.Valid ? "Broken" : status.Active ? "Active" : "Installed";
        InstallPath = status.Installed
            ? Path.Combine(Path.GetFullPath(rootPath), "runtime", Key, Version)
            : status.Package.DownloadUrl ?? "No verified online package configured";
    }

    public RuntimeRowViewModel(RuntimeInstallation runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        Key = runtime.Key;
        Name = runtime.Key;
        Version = runtime.Version;
        _status = !runtime.IsValid ? "Broken" : runtime.IsActive ? "Active" : "Installed";
        InstallPath = runtime.InstallPath;
        IsInstalled = true;
        _canDownload = false;
        CanActivate = runtime.IsValid && !runtime.IsActive;
        CanRemove = !runtime.IsActive;
    }

    public string Key { get; }
    public string Name { get; }
    public string Version { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string InstallPath { get; }
    public bool IsInstalled { get; }
    public bool CanDownload { get => _canDownload; private set => SetProperty(ref _canDownload, value); }
    public bool CanActivate { get; }
    public bool CanRemove { get; }
    public int ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (SetProperty(ref _progressPercent, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }
    public string ProgressText => $"{ProgressPercent}%";
    public Visibility ProgressVisibility { get => _progressVisibility; private set => SetProperty(ref _progressVisibility, value); }

    public void BeginInstall()
    {
        ProgressPercent = 0;
        ProgressVisibility = Visibility.Visible;
        CanDownload = false;
        Status = "Preparing installation...";
    }

    public void SetInstallProgress(int percentage)
    {
        var normalized = Math.Clamp(percentage, 0, 100);
        if (normalized < ProgressPercent)
            return;

        ProgressPercent = normalized;
        Status = ProgressPercent switch
        {
            < 5 => "Preparing installation...",
            < 68 => $"Downloading... {ProgressPercent}%",
            < 74 => $"Verifying package... {ProgressPercent}%",
            < 82 => $"Extracting package... {ProgressPercent}%",
            < 95 => $"Installing module... {ProgressPercent}%",
            < 100 => $"Activating module... {ProgressPercent}%",
            _ => "Installed · 100%"
        };
    }

    public void SetInstallFailed()
    {
        ProgressVisibility = Visibility.Collapsed;
        CanDownload = true;
        Status = "Installation failed";
    }
}

public sealed class DiagnosticRowViewModel(DiagnosticCheck check)
{
    public string Category { get; } = check.Category;
    public string Name { get; } = check.Name;
    public string Status { get; } = check.Success ? "OK" : check.Severity == DiagnosticSeverity.Warning ? "Warning" : "Error";
    public string Details { get; } = check.Details;
}
