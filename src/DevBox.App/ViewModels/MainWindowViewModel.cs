using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using DevBox.App.Services;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan AddonHealthRefreshInterval = TimeSpan.FromSeconds(15);
    private readonly string _rootPath;
    private readonly ServiceCatalog _serviceCatalog;
    private readonly IProcessManager _processManager;
    private readonly AddonCatalog _addonCatalog;
    private readonly AddonInstaller _addonInstaller;
    private readonly PhpExtensionInspector _phpExtensionInspector;
    private readonly PhpRuntimePoolManager _phpRuntimePoolManager;
    private readonly IHostMappingService _hostMappingService;
    private readonly SiteManager _siteManager;
    private readonly IRuntimeManager _runtimeManager;
    private readonly RuntimePlatformService _runtimePlatformService;
    private readonly DiagnosticsService _diagnosticsService;
    private readonly LogReader _logReader;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private readonly IReadOnlyDictionary<string, ServiceDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, AddonDefinition> _addonDefinitions;
    private readonly HashSet<string> _busyAddons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private bool _addonHealthRefreshRunning;
    private bool _refreshTickRunning;
    private DateTimeOffset _lastAddonHealthRefresh = DateTimeOffset.MinValue;
    private string _currentSection = "Dashboard";
    private string _newSiteName = string.Empty;
    private string _newSiteDomain = string.Empty;
    private string? _selectedLog;
    private string _logText = "Select a log file.";
    private bool _disposed;

    public MainWindowViewModel(
        string rootPath,
        ServiceCatalog serviceCatalog,
        IProcessManager processManager,
        AddonCatalog addonCatalog,
        AddonInstaller addonInstaller,
        PhpExtensionInspector phpExtensionInspector,
        PhpRuntimePoolManager phpRuntimePoolManager,
        IHostMappingService hostMappingService,
        SiteManager siteManager,
        IRuntimeManager runtimeManager,
        RuntimePlatformService runtimePlatformService,
        DiagnosticsService diagnosticsService,
        LogReader logReader,
        IDialogService dialogs,
        IShellService shell)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _serviceCatalog = serviceCatalog;
        _processManager = processManager;
        _addonCatalog = addonCatalog;
        _addonInstaller = addonInstaller;
        _phpExtensionInspector = phpExtensionInspector;
        _phpRuntimePoolManager = phpRuntimePoolManager;
        _hostMappingService = hostMappingService;
        _siteManager = siteManager;
        _runtimeManager = runtimeManager;
        _runtimePlatformService = runtimePlatformService;
        _diagnosticsService = diagnosticsService;
        _logReader = logReader;
        _dialogs = dialogs;
        _shell = shell;

        _definitions = _serviceCatalog.GetDefaultServices().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        _addonDefinitions = _addonCatalog.GetDefaultAddons().ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in _definitions.Values)
        {
            Services.Add(new ServiceRowViewModel(definition));
        }
        foreach (var addon in _addonDefinitions.Values)
        {
            Addons.Add(new AddonRowViewModel(addon));
        }

        NavigateCommand = new RelayCommand(parameter => Navigate(parameter as string ?? "Dashboard"));
        StartServiceCommand = new AsyncRelayCommand(parameter => RunServiceAsync(parameter, ServiceAction.Start));
        StopServiceCommand = new AsyncRelayCommand(parameter => RunServiceAsync(parameter, ServiceAction.Stop));
        RestartServiceCommand = new AsyncRelayCommand(parameter => RunServiceAsync(parameter, ServiceAction.Restart));
        StartAllCommand = new AsyncRelayCommand(() => RunAllAsync(ServiceAction.Start));
        StopAllCommand = new AsyncRelayCommand(() => RunAllAsync(ServiceAction.Stop));
        RestartAllCommand = new AsyncRelayCommand(() => RunAllAsync(ServiceAction.Restart));
        InstallAddonCommand = new AsyncRelayCommand(InstallAddonAsync);
        RepairAddonCommand = new AsyncRelayCommand(RepairAddonAsync);
        OpenAddonCommand = new AsyncRelayCommand(OpenAddonAsync);
        OpenAddonFolderCommand = new RelayCommand(OpenAddonFolder);
        UninstallAddonCommand = new AsyncRelayCommand(UninstallAddonAsync);
        CreateSiteCommand = new AsyncRelayCommand(CreateSiteAsync, () => !string.IsNullOrWhiteSpace(NewSiteName));
        OpenSiteCommand = new AsyncRelayCommand(OpenSiteAsync);
        OpenSiteFolderCommand = new RelayCommand(OpenSiteFolder);
        DeleteSiteCommand = new AsyncRelayCommand(DeleteSiteAsync);
        RefreshDiagnosticsCommand = new RelayCommand(RefreshDiagnostics);
        RefreshLogsCommand = new RelayCommand(RefreshLogs);
        ClearLogCommand = new RelayCommand(ClearSelectedLog, () => SelectedLog is not null);
        DownloadRuntimeCommand = new AsyncRelayCommand(DownloadRuntimeAsync);
        ActivateRuntimeCommand = new AsyncRelayCommand(ActivateRuntimeAsync);
        RemoveRuntimeCommand = new AsyncRelayCommand(RemoveRuntimeAsync);

        RefreshStatuses();
        RefreshAddonInstallState();
        RefreshSites();
        RefreshRuntimes();
        RefreshDiagnostics();
        RefreshLogs();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += RefreshTimerOnTick;
        _refreshTimer.Start();
    }

    public ObservableCollection<ServiceRowViewModel> Services { get; } = new();
    public ObservableCollection<AddonRowViewModel> Addons { get; } = new();
    public ObservableCollection<SiteRowViewModel> Sites { get; } = new();
    public ObservableCollection<RuntimeRowViewModel> Runtimes { get; } = new();
    public ObservableCollection<DiagnosticRowViewModel> Diagnostics { get; } = new();
    public ObservableCollection<string> LogFiles { get; } = new();

    public string RootPath => _rootPath;
    public string CurrentSection { get => _currentSection; private set { if (SetProperty(ref _currentSection, value)) RaiseSectionVisibility(); } }
    public Visibility DashboardVisibility => SectionVisibility("Dashboard");
    public Visibility SitesVisibility => SectionVisibility("Sites");
    public Visibility RuntimesVisibility => SectionVisibility("Runtimes");
    public Visibility AddonsVisibility => SectionVisibility("ADDONS");
    public Visibility LogsVisibility => SectionVisibility("Logs");
    public Visibility DiagnosticsVisibility => SectionVisibility("Diagnostics");

    public string NewSiteName
    {
        get => _newSiteName;
        set
        {
            if (SetProperty(ref _newSiteName, value))
            {
                ((AsyncRelayCommand)CreateSiteCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public string NewSiteDomain { get => _newSiteDomain; set => SetProperty(ref _newSiteDomain, value); }

    public string? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (SetProperty(ref _selectedLog, value))
            {
                ((RelayCommand)ClearLogCommand).RaiseCanExecuteChanged();
                LoadSelectedLog();
            }
        }
    }

    public string LogText { get => _logText; private set => SetProperty(ref _logText, value); }

    public RelayCommand NavigateCommand { get; }
    public AsyncRelayCommand StartServiceCommand { get; }
    public AsyncRelayCommand StopServiceCommand { get; }
    public AsyncRelayCommand RestartServiceCommand { get; }
    public AsyncRelayCommand StartAllCommand { get; }
    public AsyncRelayCommand StopAllCommand { get; }
    public AsyncRelayCommand RestartAllCommand { get; }
    public AsyncRelayCommand InstallAddonCommand { get; }
    public AsyncRelayCommand RepairAddonCommand { get; }
    public AsyncRelayCommand OpenAddonCommand { get; }
    public RelayCommand OpenAddonFolderCommand { get; }
    public AsyncRelayCommand UninstallAddonCommand { get; }
    public AsyncRelayCommand CreateSiteCommand { get; }
    public AsyncRelayCommand OpenSiteCommand { get; }
    public RelayCommand OpenSiteFolderCommand { get; }
    public AsyncRelayCommand DeleteSiteCommand { get; }
    public RelayCommand RefreshDiagnosticsCommand { get; }
    public RelayCommand RefreshLogsCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public AsyncRelayCommand DownloadRuntimeCommand { get; }
    public AsyncRelayCommand ActivateRuntimeCommand { get; }
    public AsyncRelayCommand RemoveRuntimeCommand { get; }

    public async Task RefreshAddonHealthAsync()
    {
        if (_addonHealthRefreshRunning)
        {
            return;
        }

        _addonHealthRefreshRunning = true;
        try
        {
            foreach (var addon in _addonDefinitions.Values)
            {
                var row = GetAddonRow(addon.Key);
                if (!_addonCatalog.IsInstalled(addon) || _busyAddons.Contains(addon.Key))
                {
                    continue;
                }

                var domain = new Uri(addon.LocalUrl).Host;
                var hostConfigured = _hostMappingService.Has(domain);
                var addonConfig = File.Exists(Path.Combine(addon.InstallPath, "config.inc.php"));
                var nginxConfig = File.Exists(Path.Combine(_rootPath, "config", "nginx", "sites-enabled", $"{domain}.conf"));
                var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);
                row.ApplyHealth(hostConfigured, addonConfig && nginxConfig, phpCheck);
            }
        }
        finally
        {
            _addonHealthRefreshRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimerOnTick;
    }

    private async void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        if (_refreshTickRunning || _disposed)
        {
            return;
        }

        _refreshTickRunning = true;
        try
        {
            RefreshStatuses();
            RefreshAddonInstallState();
            var now = DateTimeOffset.UtcNow;
            if (CurrentSection == "ADDONS" && now - _lastAddonHealthRefresh >= AddonHealthRefreshInterval)
            {
                _lastAddonHealthRefresh = now;
                await RefreshAddonHealthAsync();
            }
            if (CurrentSection == "Logs" && SelectedLog is not null)
            {
                LoadSelectedLog();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DevBox refresh cycle failed: {ex}");
        }
        finally
        {
            _refreshTickRunning = false;
        }
    }

    private void Navigate(string section)
    {
        CurrentSection = section;
        if (section == "Sites") RefreshSites();
        if (section == "Runtimes") RefreshRuntimes();
        if (section == "Diagnostics") RefreshDiagnostics();
        if (section == "Logs") RefreshLogs();
        if (section == "ADDONS")
        {
            _lastAddonHealthRefresh = DateTimeOffset.UtcNow;
            _ = RefreshAddonHealthSafelyAsync();
        }
    }

    private async Task RefreshAddonHealthSafelyAsync()
    {
        try
        {
            await RefreshAddonHealthAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DevBox addon health refresh failed: {ex}");
        }
    }

    private async Task RunServiceAsync(object? parameter, ServiceAction action)
    {
        if (parameter is not ServiceRowViewModel row || !_definitions.TryGetValue(row.Key, out var definition))
        {
            return;
        }
        _ = await ExecuteServiceAsync(definition, action, showDialog: true);
    }

    private async Task RunAllAsync(ServiceAction action)
    {
        var succeeded = 0;
        var missing = new List<string>();
        var failures = new List<string>();

        foreach (var definition in _definitions.Values)
        {
            if (action != ServiceAction.Stop && !File.Exists(definition.ExecutablePath))
            {
                missing.Add(definition.DisplayName);
                continue;
            }

            var error = await ExecuteServiceAsync(definition, action, showDialog: false);
            if (error is null)
            {
                succeeded++;
            }
            else
            {
                failures.Add($"{definition.DisplayName}: {error}");
            }
        }

        var operation = action switch
        {
            ServiceAction.Start => "Start All",
            ServiceAction.Stop => "Stop All",
            ServiceAction.Restart => "Restart All",
            _ => "Service operation"
        };

        var summary = $"Succeeded: {succeeded}. Failed: {failures.Count}. Missing runtime: {missing.Count}.";
        if (failures.Count > 0 || missing.Count > 0)
        {
            var details = new List<string> { summary };
            if (missing.Count > 0) details.Add($"Missing: {string.Join(", ", missing)}");
            if (failures.Count > 0) details.Add(string.Join(Environment.NewLine, failures));
            _dialogs.Warning(operation, string.Join(Environment.NewLine + Environment.NewLine, details));
        }
        else
        {
            _dialogs.Info(operation, summary);
        }
    }

    private async Task<string?> ExecuteServiceAsync(ServiceDefinition definition, ServiceAction action, bool showDialog)
    {
        string? error = null;
        try
        {
            _ = action switch
            {
                ServiceAction.Start => await _processManager.StartAsync(definition),
                ServiceAction.Stop => await _processManager.StopAsync(definition),
                ServiceAction.Restart => await _processManager.RestartAsync(definition),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            if (showDialog)
            {
                _dialogs.Error($"{definition.DisplayName} error", ex.Message);
            }
        }
        finally
        {
            RefreshStatuses();
        }
        return error;
    }

    private async Task InstallAddonAsync(object? parameter)
    {
        if (!TryBeginAddonOperation(parameter, "Downloading...", out var addon)) return;
        try
        {
            await _addonInstaller.InstallAsync(addon);
            RuntimeLayout.EnsureInitialized(_rootPath);
            var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);
            var phpConfigChanged = phpCheck.RuntimeAvailable &&
                                   _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            var domain = new Uri(addon.LocalUrl).Host;
            var hostConfigured = await _hostMappingService.EnsureAsync(domain);
            if (phpConfigChanged) await RestartIfRunningAsync("php");
            await RestartIfRunningAsync("nginx");
            await RefreshAddonHealthAsync();

            if (!phpCheck.RuntimeAvailable)
            {
                _dialogs.Warning(
                    $"{addon.DisplayName} downloaded",
                    $"{addon.DisplayName} {addon.Version} was downloaded and configured, but PHP is not installed. Open Modules and click Install for PHP.");
            }
            else
            {
                _dialogs.Info($"{addon.DisplayName} installed", hostConfigured
                    ? $"{addon.DisplayName} {addon.Version} installed and configured."
                    : $"{addon.DisplayName} installed, but the hosts mapping is missing.");
            }
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            GetAddonRow(addon.Key).SetError();
            _dialogs.Error($"{addon.DisplayName} installation failed", ex.Message);
        }
        finally
        {
            EndAddonOperation(addon.Key);
        }
    }

    private async Task RepairAddonAsync(object? parameter)
    {
        if (!TryBeginAddonOperation(parameter, "Repairing...", out var addon)) return;
        try
        {
            RuntimeLayout.EnsureInitialized(_rootPath);
            await _addonInstaller.RepairAsync(addon);
            var phpChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            var hostConfigured = await _hostMappingService.EnsureAsync(new Uri(addon.LocalUrl).Host);
            if (phpChanged) await RestartIfRunningAsync("php");
            await RestartIfRunningAsync("nginx");
            await RefreshAddonHealthAsync();
            if (hostConfigured) _dialogs.Info($"{addon.DisplayName} repair", "Configuration repaired.");
            else _dialogs.Warning($"{addon.DisplayName} repair", "Files were repaired, but the hosts mapping is still missing.");
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            GetAddonRow(addon.Key).SetError();
            _dialogs.Error($"{addon.DisplayName} repair failed", ex.Message);
        }
        finally
        {
            EndAddonOperation(addon.Key);
        }
    }

    private async Task UninstallAddonAsync(object? parameter)
    {
        if (!TryGetAddon(parameter, out var addon) || !_addonCatalog.IsInstalled(addon)) return;
        if (!_dialogs.Confirm($"Uninstall {addon.DisplayName}", $"Remove {addon.DisplayName} from DevBox?\n\n{addon.InstallPath}")) return;
        if (!_busyAddons.Add(addon.Key)) return;
        GetAddonRow(addon.Key).SetBusy("Uninstalling...");
        try
        {
            await _addonInstaller.UninstallAsync(addon);
            await _hostMappingService.RemoveAsync(new Uri(addon.LocalUrl).Host);
            RefreshAddonInstallState();
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            GetAddonRow(addon.Key).SetError();
            _dialogs.Error($"{addon.DisplayName} uninstall failed", ex.Message);
        }
        finally
        {
            EndAddonOperation(addon.Key);
        }
    }

    private async Task OpenAddonAsync(object? parameter)
    {
        if (!TryGetAddon(parameter, out var addon)) return;
        if (!_addonCatalog.IsInstalled(addon))
        {
            _dialogs.Info($"{addon.DisplayName} not installed", "Install the addon first.");
            return;
        }

        try
        {
            await _addonInstaller.RepairAsync(addon);
            var phpChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            if (phpChanged) await RestartIfRunningAsync("php");
            if (!await _hostMappingService.EnsureAsync(new Uri(addon.LocalUrl).Host))
            {
                _dialogs.Warning("Hosts entry missing", $"Cannot open {addon.DisplayName} until its .test mapping is configured.");
                return;
            }

            var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);
            if (!phpCheck.Success)
            {
                _dialogs.Warning($"{addon.DisplayName} prerequisites", phpCheck.Error ?? $"Missing PHP extensions: {string.Join(", ", phpCheck.MissingExtensions)}");
                return;
            }

            await EnsureRunningAsync("mysql");
            await EnsureRunningAsync("php");
            await EnsureRunningAsync("nginx");
            _shell.Open(addon.LocalUrl);
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            _dialogs.Error($"Unable to open {addon.DisplayName}", ex.Message);
        }
    }

    private void OpenAddonFolder(object? parameter)
    {
        if (!TryGetAddon(parameter, out var addon)) return;
        Directory.CreateDirectory(addon.InstallPath);
        TryOpen(addon.InstallPath);
    }

    private async Task CreateSiteAsync()
    {
        try
        {
            var domain = string.IsNullOrWhiteSpace(NewSiteDomain) ? null : NewSiteDomain.Trim();
            var site = _siteManager.Create(NewSiteName, domain);
            var hostConfigured = await _hostMappingService.EnsureAsync(site.Domain);
            await RestartIfRunningAsync("nginx");
            NewSiteName = string.Empty;
            NewSiteDomain = string.Empty;
            RefreshSites();
            if (!hostConfigured)
            {
                _dialogs.Warning("Site created", $"{site.Domain} was created, but the hosts mapping could not be configured.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Create site failed", ex.Message);
        }
    }

    private async Task OpenSiteAsync(object? parameter)
    {
        if (parameter is not SiteRowViewModel site) return;
        try
        {
            if (!await _hostMappingService.EnsureAsync(site.Domain))
            {
                _dialogs.Warning("Hosts entry missing", $"Cannot open {site.Domain} until its hosts mapping is configured.");
                return;
            }

            if (string.IsNullOrWhiteSpace(site.PhpVersion))
            {
                await EnsureRunningAsync("php");
            }
            else
            {
                _ = await _phpRuntimePoolManager.EnsureRunningAsync(site.PhpVersion);
            }

            await EnsureRunningAsync("nginx");
            _shell.Open(site.Url);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Open site failed", ex.Message);
        }
    }

    private void OpenSiteFolder(object? parameter)
    {
        if (parameter is SiteRowViewModel site)
        {
            TryOpen(site.DocumentRoot);
        }
    }

    private async Task DeleteSiteAsync(object? parameter)
    {
        if (parameter is not SiteRowViewModel site) return;
        if (!_dialogs.Confirm("Delete site", $"Remove {site.Domain} from DevBox? Project files will be kept.")) return;
        try
        {
            _siteManager.Delete(site.Name);
            await _hostMappingService.RemoveAsync(site.Domain);
            await RestartIfRunningAsync("nginx");
            RefreshSites();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _dialogs.Error("Delete site failed", ex.Message);
        }
    }

    private async Task DownloadRuntimeAsync(object? parameter)
    {
        if (parameter is not RuntimeRowViewModel runtime || !runtime.CanDownload) return;

        runtime.BeginInstall();
        var progress = new Progress<int>(runtime.SetInstallProgress);
        _definitions.TryGetValue(runtime.Key, out var service);
        var wasRunning = service is not null && _processManager.GetStatus(service).State == ServiceState.Running;

        try
        {
            if (wasRunning)
            {
                await _processManager.StopAsync(service!);
            }

            // RuntimeManager verifies SHA-256, installs atomically and reports each installation stage.
            await _runtimePlatformService.InstallAsync(runtime.Key, runtime.Version, progress);

            if (wasRunning)
            {
                await _processManager.StartAsync(service!);
            }

            runtime.SetInstallProgress(100);
            RefreshRuntimes();
            RefreshStatuses();
            RefreshDiagnostics();
            _dialogs.Info(
                $"{runtime.Name} installed",
                $"{runtime.Name} {runtime.Version} was downloaded, verified, installed and activated.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception)
        {
            if (wasRunning && service is not null &&
                _processManager.GetStatus(service).State != ServiceState.Running &&
                File.Exists(service.ExecutablePath))
            {
                try
                {
                    await _processManager.StartAsync(service);
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception)
                {
                    _dialogs.Warning(
                        "Service recovery failed",
                        $"{runtime.Name} installation failed and {service.DisplayName} could not be restarted: {recoveryError.Message}");
                }
            }

            runtime.SetInstallFailed();
            RefreshStatuses();
            RefreshDiagnostics();
            _dialogs.Error($"{runtime.Name} installation failed", ex.Message);
        }
    }

    private async Task ActivateRuntimeAsync(object? parameter)
    {
        if (parameter is not RuntimeRowViewModel runtime || runtime.Status == "Active") return;
        var executableRelativePath = RuntimeExecutable(runtime.Key);
        _definitions.TryGetValue(runtime.Key, out var service);
        var wasRunning = service is not null && _processManager.GetStatus(service).State == ServiceState.Running;

        try
        {
            if (wasRunning)
            {
                await _processManager.StopAsync(service!);
            }

            await _runtimeManager.ActivateAsync(runtime.Key, runtime.Version, executableRelativePath);

            if (wasRunning)
            {
                await _processManager.StartAsync(service!);
            }

            RefreshRuntimes();
            RefreshStatuses();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or DirectoryNotFoundException)
        {
            if (wasRunning && service is not null && _processManager.GetStatus(service).State != ServiceState.Running)
            {
                try
                {
                    await _processManager.StartAsync(service);
                }
                catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception)
                {
                    _dialogs.Warning("Service recovery failed", $"Runtime activation failed and {service.DisplayName} could not be restarted: {recoveryError.Message}");
                }
            }
            _dialogs.Error("Runtime activation failed", ex.Message);
        }
    }

    private async Task RemoveRuntimeAsync(object? parameter)
    {
        if (parameter is not RuntimeRowViewModel runtime) return;

        if (runtime.Key.Equals("php", StringComparison.OrdinalIgnoreCase))
        {
            var dependentSites = _siteManager.GetSites()
                .Where(site => site.PhpVersion?.Equals(runtime.Version, StringComparison.OrdinalIgnoreCase) == true)
                .Select(site => site.Domain)
                .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dependentSites.Length > 0)
            {
                _dialogs.Warning(
                    "Runtime is in use",
                    $"PHP {runtime.Version} cannot be removed because it is assigned to: {string.Join(", ", dependentSites)}.");
                return;
            }
        }

        if (!_dialogs.Confirm("Remove runtime", $"Remove {runtime.Key} {runtime.Version}?")) return;
        try
        {
            await _runtimeManager.RemoveAsync(runtime.Key, runtime.Version);
            RefreshRuntimes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _dialogs.Error("Runtime removal failed", ex.Message);
        }
    }

    private void RefreshStatuses()
    {
        foreach (var row in Services)
        {
            var definition = _definitions[row.Key];
            row.Apply(_processManager.GetStatus(definition), File.Exists(definition.ExecutablePath));
        }
    }

    private void RefreshAddonInstallState()
    {
        foreach (var row in Addons)
        {
            if (!_busyAddons.Contains(row.Key))
            {
                row.ApplyInstallation(_addonCatalog.IsInstalled(_addonDefinitions[row.Key]));
            }
        }
    }

    private void RefreshSites()
    {
        Sites.Clear();
        foreach (var site in _siteManager.GetSites()) Sites.Add(new SiteRowViewModel(site));
    }

    private void RefreshRuntimes()
    {
        Runtimes.Clear();
        var supportedKeys = new HashSet<string>(["php", "nginx", "mysql"], StringComparer.OrdinalIgnoreCase);
        var represented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var status in _runtimePlatformService.GetStatuses()
                     .Where(item => supportedKeys.Contains(item.Package.Key)))
        {
            Runtimes.Add(new RuntimeRowViewModel(status, _rootPath));
            represented.Add($"{status.Package.Key}|{status.Package.Version}");
        }

        foreach (var descriptor in new[]
        {
            (Key: "php", Executable: "php-cgi.exe"),
            (Key: "nginx", Executable: "nginx.exe"),
            (Key: "mysql", Executable: Path.Combine("bin", "mysqld.exe"))
        })
        {
            foreach (var runtime in _runtimeManager.GetInstalled(descriptor.Key, descriptor.Executable))
            {
                if (represented.Add($"{runtime.Key}|{runtime.Version}"))
                {
                    Runtimes.Add(new RuntimeRowViewModel(runtime));
                }
            }
        }
    }

    private void RefreshDiagnostics()
    {
        Diagnostics.Clear();
        foreach (var check in _diagnosticsService.Run()) Diagnostics.Add(new DiagnosticRowViewModel(check));
    }

    private void RefreshLogs()
    {
        var previous = SelectedLog;
        LogFiles.Clear();
        foreach (var file in _logReader.GetAvailableLogs()) LogFiles.Add(file);
        if (previous is not null && LogFiles.Contains(previous)) SelectedLog = previous;
        else if (LogFiles.Count > 0) SelectedLog = LogFiles[0];
        else
        {
            SelectedLog = null;
            LogText = "No log files available yet.";
        }
    }

    private void LoadSelectedLog()
    {
        if (SelectedLog is null)
        {
            LogText = "Select a log file.";
            return;
        }
        try
        {
            LogText = string.Join(Environment.NewLine, _logReader.ReadTail(SelectedLog, 1000));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LogText = ex.Message;
        }
    }

    private void ClearSelectedLog()
    {
        if (SelectedLog is null) return;
        try
        {
            _logReader.Clear(SelectedLog);
            LoadSelectedLog();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.Error("Clear log failed", ex.Message);
        }
    }

    private async Task EnsureRunningAsync(string key)
    {
        if (!_definitions.TryGetValue(key, out var definition)) throw new InvalidOperationException($"Service '{key}' is not registered.");
        if (_processManager.GetStatus(definition).State != ServiceState.Running)
        {
            await _processManager.StartAsync(definition);
            RefreshStatuses();
        }
    }

    private async Task RestartIfRunningAsync(string key)
    {
        if (_definitions.TryGetValue(key, out var definition) && _processManager.GetStatus(definition).State == ServiceState.Running)
        {
            await _processManager.RestartAsync(definition);
            RefreshStatuses();
        }
    }

    private bool TryBeginAddonOperation(object? parameter, string busyText, out AddonDefinition addon)
    {
        if (!TryGetAddon(parameter, out addon) || !_busyAddons.Add(addon.Key)) return false;
        GetAddonRow(addon.Key).SetBusy(busyText);
        return true;
    }

    private void EndAddonOperation(string key)
    {
        _busyAddons.Remove(key);
        RefreshAddonInstallState();
    }

    private bool TryGetAddon(object? parameter, out AddonDefinition addon)
    {
        if (parameter is AddonRowViewModel row && _addonDefinitions.TryGetValue(row.Key, out var found))
        {
            addon = found;
            return true;
        }
        addon = null!;
        return false;
    }

    private AddonRowViewModel GetAddonRow(string key) => Addons.First(row => row.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private void TryOpen(string target)
    {
        try
        {
            _shell.Open(target);
        }
        catch (Win32Exception ex)
        {
            _dialogs.Error("Unable to open", ex.Message);
        }
    }

    private Visibility SectionVisibility(string section) => CurrentSection == section ? Visibility.Visible : Visibility.Collapsed;

    private void RaiseSectionVisibility()
    {
        OnPropertyChanged(nameof(DashboardVisibility));
        OnPropertyChanged(nameof(SitesVisibility));
        OnPropertyChanged(nameof(RuntimesVisibility));
        OnPropertyChanged(nameof(AddonsVisibility));
        OnPropertyChanged(nameof(LogsVisibility));
        OnPropertyChanged(nameof(DiagnosticsVisibility));
    }

    private static string RuntimeExecutable(string key) => key.ToLowerInvariant() switch
    {
        "php" => "php-cgi.exe",
        "nginx" => "nginx.exe",
        "mysql" => Path.Combine("bin", "mysqld.exe"),
        _ => throw new InvalidOperationException($"Unknown runtime '{key}'.")
    };

    private static bool IsExpectedAddonError(Exception ex) =>
        ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception;

    private enum ServiceAction
    {
        Start,
        Stop,
        Restart
    }
}
