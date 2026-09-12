using System.Collections.ObjectModel;
using DevBox.App.Services;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class SitePhpRowViewModel(SiteDefinition site)
{
    public string Name { get; } = site.Name;
    public string Domain { get; } = site.Domain;
    public string? PhpVersion { get; } = site.PhpVersion;
    public string DisplayVersion { get; } = site.PhpVersion ?? SitePhpWindowViewModel.GlobalPhpOption;
}

public sealed class SitePhpWindowViewModel : ObservableObject
{
    public const string GlobalPhpOption = "Global active PHP";

    private readonly SiteManager _siteManager;
    private readonly IRuntimeManager _runtimeManager;
    private readonly PhpRuntimePoolManager _pool;
    private readonly IProcessManager _processManager;
    private readonly ServiceDefinition _nginx;
    private readonly IDialogService _dialogs;
    private SitePhpRowViewModel? _selectedSite;
    private string? _selectedVersion;
    private string _status = "Ready";

    public SitePhpWindowViewModel(
        SiteManager siteManager,
        IRuntimeManager runtimeManager,
        PhpRuntimePoolManager pool,
        IProcessManager processManager,
        ServiceCatalog serviceCatalog,
        IDialogService dialogs)
    {
        _siteManager = siteManager;
        _runtimeManager = runtimeManager;
        _pool = pool;
        _processManager = processManager;
        _nginx = serviceCatalog.GetCoreServices().First(service => service.Key == "nginx");
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(Refresh);
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => SelectedSite is not null && SelectedVersion is not null);
        StartSelectedCommand = new AsyncRelayCommand(StartSelectedAsync, () => SelectedVersion is not null && SelectedVersion != GlobalPhpOption);
        Refresh();
    }

    public ObservableCollection<SitePhpRowViewModel> Sites { get; } = new();
    public ObservableCollection<string> Versions { get; } = new();
    public SitePhpRowViewModel? SelectedSite
    {
        get => _selectedSite;
        set
        {
            if (SetProperty(ref _selectedSite, value))
            {
                SelectedVersion = value?.PhpVersion ?? GlobalPhpOption;
                ApplyCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                ApplyCommand.RaiseCanExecuteChanged();
                StartSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ApplyCommand { get; }
    public AsyncRelayCommand StartSelectedCommand { get; }

    private void Refresh()
    {
        var previousSite = SelectedSite?.Name;
        var previousVersion = SelectedVersion;

        Sites.Clear();
        foreach (var site in _siteManager.GetSites())
        {
            Sites.Add(new SitePhpRowViewModel(site));
        }

        Versions.Clear();
        Versions.Add(GlobalPhpOption);
        foreach (var runtime in _runtimeManager.GetInstalled("php", "php-cgi.exe")
                     .Where(runtime => runtime.IsValid)
                     .OrderByDescending(runtime => ParseVersion(runtime.Version)))
        {
            Versions.Add(runtime.Version);
        }

        foreach (var configured in Sites.Select(site => site.PhpVersion).Where(version => version is not null).Cast<string>())
        {
            if (!Versions.Contains(configured))
            {
                Versions.Add(configured);
            }
        }

        SelectedSite = previousSite is null
            ? Sites.FirstOrDefault()
            : Sites.FirstOrDefault(site => site.Name.Equals(previousSite, StringComparison.OrdinalIgnoreCase)) ?? Sites.FirstOrDefault();
        if (previousVersion is not null && Versions.Contains(previousVersion))
        {
            SelectedVersion = previousVersion;
        }
        Status = Sites.Count == 0 ? "No sites configured" : "Ready";
    }

    private async Task ApplyAsync()
    {
        if (SelectedSite is null || SelectedVersion is null)
        {
            return;
        }

        try
        {
            Status = "Applying PHP runtime...";
            var version = SelectedVersion == GlobalPhpOption ? null : SelectedVersion;
            if (version is not null)
            {
                var runtime = _runtimeManager.GetInstalled("php", "php-cgi.exe")
                    .FirstOrDefault(item => item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) && item.IsValid);
                if (runtime is null)
                {
                    throw new InvalidOperationException($"PHP {version} is not installed or is broken.");
                }
                var port = await _pool.EnsureRunningAsync(version);
                Status = $"PHP {version} running on FastCGI port {port}";
            }

            _siteManager.SetPhpVersion(SelectedSite.Name, version);
            if (_processManager.GetStatus(_nginx).State == ServiceState.Running)
            {
                await _processManager.RestartAsync(_nginx);
            }
            Refresh();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            Status = "Apply failed";
            _dialogs.Error("PHP per-site configuration failed", ex.Message);
        }
    }

    private async Task StartSelectedAsync()
    {
        if (SelectedVersion is null || SelectedVersion == GlobalPhpOption)
        {
            return;
        }
        try
        {
            var port = await _pool.EnsureRunningAsync(SelectedVersion);
            Status = $"PHP {SelectedVersion} running on FastCGI port {port}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            Status = "Start failed";
            _dialogs.Error("PHP FastCGI start failed", ex.Message);
        }
    }

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);
}
