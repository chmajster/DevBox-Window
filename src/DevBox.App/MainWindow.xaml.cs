using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class MainWindow : Window
{
    private readonly IReadOnlyDictionary<string, ServiceDefinition> _definitions;
    private readonly IReadOnlyDictionary<string, AddonDefinition> _addonDefinitions;
    private readonly AddonCatalog _addonCatalog;
    private readonly AddonInstaller _addonInstaller;
    private readonly HostsFileManager _hostsFileManager;
    private readonly PhpExtensionInspector _phpExtensionInspector;
    private readonly ObservableCollection<ServiceRow> _rows = new();
    private readonly ObservableCollection<AddonRow> _addonRows = new();
    private readonly HashSet<string> _busyAddons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private bool _addonHealthRefreshRunning;

    public MainWindow()
    {
        InitializeComponent();

        _definitions = App.ServiceCatalog.GetDefaultServices()
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var definition in _definitions.Values)
        {
            _rows.Add(new ServiceRow(definition));
        }

        _addonCatalog = new AddonCatalog(App.DevBoxRoot);
        _addonInstaller = new AddonInstaller(App.DevBoxRoot);
        _hostsFileManager = new HostsFileManager();
        _phpExtensionInspector = new PhpExtensionInspector(App.DevBoxRoot);
        _addonDefinitions = _addonCatalog.GetDefaultAddons()
            .ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var addon in _addonDefinitions.Values)
        {
            _addonRows.Add(new AddonRow(addon));
        }

        ServicesList.ItemsSource = _rows;
        AddonsList.ItemsSource = _addonRows;
        RootPathText.Text = App.DevBoxRoot;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshStatuses();
            RefreshAddonInstallState();
        };
        _refreshTimer.Start();
        RefreshStatuses();
        RefreshAddonInstallState();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshTimer.Stop();
        _addonInstaller.Dispose();
        base.OnClosed(e);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, (definition, token) => App.ProcessManager.StartAsync(definition, token));

    private async void Stop_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, (definition, token) => App.ProcessManager.StopAsync(definition, token));

    private async void Restart_Click(object sender, RoutedEventArgs e) =>
        await RunAsync(sender, (definition, token) => App.ProcessManager.RestartAsync(definition, token));

    private async void StartAll_Click(object sender, RoutedEventArgs e) => await RunAllAsync(ServiceAction.Start);
    private async void StopAll_Click(object sender, RoutedEventArgs e) => await RunAllAsync(ServiceAction.Stop);
    private async void RestartAll_Click(object sender, RoutedEventArgs e) => await RunAllAsync(ServiceAction.Restart);

    private void DashboardNav_Click(object sender, RoutedEventArgs e)
    {
        DashboardPanel.Visibility = Visibility.Visible;
        AddonsPanel.Visibility = Visibility.Collapsed;
        DashboardNavButton.Foreground = Brushes.White;
        DashboardNavButton.FontWeight = FontWeights.SemiBold;
        AddonsNavButton.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        AddonsNavButton.FontWeight = FontWeights.Normal;
    }

    private async void AddonsNav_Click(object sender, RoutedEventArgs e)
    {
        DashboardPanel.Visibility = Visibility.Collapsed;
        AddonsPanel.Visibility = Visibility.Visible;
        AddonsNavButton.Foreground = Brushes.White;
        AddonsNavButton.FontWeight = FontWeights.SemiBold;
        DashboardNavButton.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        DashboardNavButton.FontWeight = FontWeights.Normal;
        RefreshAddonInstallState();
        await RefreshAddonHealthAsync();
    }

    private async void InstallAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginAddonOperation(sender, out var addon, "Installing..."))
        {
            return;
        }

        try
        {
            await _addonInstaller.InstallAsync(addon);
            RuntimeLayout.EnsureInitialized(App.DevBoxRoot);
            var phpConfigChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            var hostConfigured = await EnsureAddonHostAsync(addon);

            if (phpConfigChanged)
            {
                await RestartManagedServiceIfRunningAsync("php");
            }
            await RestartManagedServiceIfRunningAsync("nginx");
            await RefreshAddonHealthAsync();

            var hostMessage = hostConfigured ? "Host mapping: OK" : "Host mapping: not configured (UAC was cancelled or failed).";
            MessageBox.Show(
                this,
                $"{addon.DisplayName} {addon.Version} installed successfully.\n\n{hostMessage}\nPath: {addon.InstallPath}\nURL: {addon.LocalUrl}",
                $"{addon.DisplayName} installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            SetAddonError(addon);
            MessageBox.Show(this, ex.Message, $"{addon.DisplayName} installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndAddonOperation(addon);
        }
    }

    private async void RepairAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryBeginAddonOperation(sender, out var addon, "Repairing..."))
        {
            return;
        }

        try
        {
            RuntimeLayout.EnsureInitialized(App.DevBoxRoot);
            await _addonInstaller.RepairAsync(addon);
            var phpConfigChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            var hostConfigured = await EnsureAddonHostAsync(addon);

            if (phpConfigChanged)
            {
                await RestartManagedServiceIfRunningAsync("php");
            }
            await RestartManagedServiceIfRunningAsync("nginx");
            await RefreshAddonHealthAsync();

            MessageBox.Show(
                this,
                hostConfigured ? $"{addon.DisplayName} configuration repaired." : $"{addon.DisplayName} files were repaired, but the hosts entry is still missing.",
                $"{addon.DisplayName} repair",
                MessageBoxButton.OK,
                hostConfigured ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            SetAddonError(addon);
            MessageBox.Show(this, ex.Message, $"{addon.DisplayName} repair failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndAddonOperation(addon);
        }
    }

    private async void UninstallAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddon(sender, out var addon) || !_addonCatalog.IsInstalled(addon))
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Remove {addon.DisplayName} from DevBox?\n\n{addon.InstallPath}",
                $"Uninstall {addon.DisplayName}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_busyAddons.Add(addon.Key))
        {
            return;
        }

        GetAddonRow(addon).SetBusy("Uninstalling...");
        try
        {
            await _addonInstaller.UninstallAsync(addon);
            await RemoveAddonHostAsync(addon);
            RefreshAddonInstallState();
            MessageBox.Show(this, $"{addon.DisplayName} removed.", $"{addon.DisplayName} uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            SetAddonError(addon);
            MessageBox.Show(this, ex.Message, $"{addon.DisplayName} uninstall failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            EndAddonOperation(addon);
        }
    }

    private async void OpenAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddon(sender, out var addon))
        {
            return;
        }

        if (!_addonCatalog.IsInstalled(addon))
        {
            MessageBox.Show(this, $"{addon.DisplayName} is not installed. Use Install first.", $"{addon.DisplayName} not installed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            RuntimeLayout.EnsureInitialized(App.DevBoxRoot);
            await _addonInstaller.RepairAsync(addon);
            var phpConfigChanged = _phpExtensionInspector.EnsureConfigured(addon.RequiredPhpExtensions);
            if (phpConfigChanged)
            {
                await RestartManagedServiceIfRunningAsync("php");
            }

            if (!await EnsureAddonHostAsync(addon))
            {
                MessageBox.Show(this, $"Cannot open {addon.DisplayName} because its .test hosts entry is missing.", "Hosts entry missing", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);
            if (!phpCheck.Success)
            {
                var details = !phpCheck.RuntimeAvailable
                    ? phpCheck.Error
                    : phpCheck.MissingExtensions.Count > 0
                        ? $"Missing PHP extensions: {string.Join(", ", phpCheck.MissingExtensions)}"
                        : phpCheck.Error;
                MessageBox.Show(this, details ?? "PHP validation failed.", $"{addon.DisplayName} prerequisites", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshAddonHealthAsync();
                return;
            }

            await EnsureServiceRunningAsync("mysql");
            await EnsureServiceRunningAsync("php");
            await EnsureServiceRunningAsync("nginx");
            Process.Start(new ProcessStartInfo(addon.LocalUrl) { UseShellExecute = true });
            await RefreshAddonHealthAsync();
        }
        catch (Exception ex) when (IsExpectedAddonError(ex))
        {
            MessageBox.Show(this, ex.Message, $"Unable to open {addon.DisplayName}", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenAddonFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddon(sender, out var addon))
        {
            return;
        }

        Directory.CreateDirectory(addon.InstallPath);
        try
        {
            Process.Start(new ProcessStartInfo(addon.InstallPath) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(this, ex.Message, $"Unable to open {addon.DisplayName} folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool TryBeginAddonOperation(object sender, out AddonDefinition addon, string busyText)
    {
        if (!TryGetAddon(sender, out addon) || !_busyAddons.Add(addon.Key))
        {
            return false;
        }

        GetAddonRow(addon).SetBusy(busyText);
        return true;
    }

    private void EndAddonOperation(AddonDefinition addon)
    {
        _busyAddons.Remove(addon.Key);
        RefreshAddonInstallState();
    }

    private void SetAddonError(AddonDefinition addon) => GetAddonRow(addon).SetError();

    private AddonRow GetAddonRow(AddonDefinition addon) =>
        _addonRows.First(row => row.Key.Equals(addon.Key, StringComparison.OrdinalIgnoreCase));

    private bool TryGetAddon(object sender, out AddonDefinition addon)
    {
        if (sender is Button { Tag: string key } && _addonDefinitions.TryGetValue(key, out var found))
        {
            addon = found;
            return true;
        }

        addon = null!;
        return false;
    }

    private async Task<bool> EnsureAddonHostAsync(AddonDefinition addon)
    {
        var domain = new Uri(addon.LocalUrl).Host;
        const string ipAddress = "127.0.0.1";
        try
        {
            if (_hostsFileManager.HasMapping(ipAddress, domain))
            {
                return true;
            }

            _hostsFileManager.EnsureMapping(ipAddress, domain);
            return _hostsFileManager.HasMapping(ipAddress, domain);
        }
        catch (UnauthorizedAccessException)
        {
            if (!await RunElevatedHostsCommandAsync("--hosts-ensure", domain, ipAddress))
            {
                return false;
            }
            return _hostsFileManager.HasMapping(ipAddress, domain);
        }
    }

    private async Task RemoveAddonHostAsync(AddonDefinition addon)
    {
        var domain = new Uri(addon.LocalUrl).Host;
        try
        {
            _hostsFileManager.RemoveMapping(domain);
        }
        catch (UnauthorizedAccessException)
        {
            await RunElevatedHostsCommandAsync("--hosts-remove", domain);
        }
    }

    private static async Task<bool> RunElevatedHostsCommandAsync(string command, params string[] arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        info.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    private async Task RefreshAddonHealthAsync()
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
                var row = GetAddonRow(addon);
                if (!_addonCatalog.IsInstalled(addon) || _busyAddons.Contains(addon.Key))
                {
                    continue;
                }

                var domain = new Uri(addon.LocalUrl).Host;
                var hostConfigured = false;
                try
                {
                    hostConfigured = _hostsFileManager.HasMapping("127.0.0.1", domain);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                var addonConfig = File.Exists(Path.Combine(addon.InstallPath, "config.inc.php"));
                var nginxConfig = File.Exists(Path.Combine(App.DevBoxRoot, "config", "nginx", "sites-enabled", $"{domain}.conf"));
                var phpCheck = await _phpExtensionInspector.CheckAsync(addon.RequiredPhpExtensions);
                row.ApplyHealth(hostConfigured, addonConfig && nginxConfig, phpCheck);
            }
        }
        finally
        {
            _addonHealthRefreshRunning = false;
        }
    }

    private async Task EnsureServiceRunningAsync(string key)
    {
        if (!_definitions.TryGetValue(key, out var definition))
        {
            throw new InvalidOperationException($"Service '{key}' is not registered.");
        }

        if (App.ProcessManager.GetStatus(definition).State == ServiceState.Running)
        {
            return;
        }

        await App.ProcessManager.StartAsync(definition);
        RefreshStatuses();
    }

    private async Task RestartManagedServiceIfRunningAsync(string key)
    {
        if (_definitions.TryGetValue(key, out var definition) && App.ProcessManager.GetStatus(definition).State == ServiceState.Running)
        {
            await App.ProcessManager.RestartAsync(definition);
            RefreshStatuses();
        }
    }

    private async Task RunAsync(object sender, Func<ServiceDefinition, CancellationToken, Task<ServiceSnapshot>> operation)
    {
        if (sender is not Button { Tag: string key } || !_definitions.TryGetValue(key, out var definition))
        {
            return;
        }

        await ExecuteAsync(definition, operation);
    }

    private async Task RunAllAsync(ServiceAction action)
    {
        var ordered = action == ServiceAction.Stop ? _definitions.Values.Reverse() : _definitions.Values;
        foreach (var definition in ordered)
        {
            Func<ServiceDefinition, CancellationToken, Task<ServiceSnapshot>> operation = action switch
            {
                ServiceAction.Start => (d, token) => App.ProcessManager.StartAsync(d, token),
                ServiceAction.Stop => (d, token) => App.ProcessManager.StopAsync(d, token),
                ServiceAction.Restart => (d, token) => App.ProcessManager.RestartAsync(d, token),
                _ => throw new ArgumentOutOfRangeException(nameof(action))
            };

            if (action != ServiceAction.Stop && !File.Exists(definition.ExecutablePath))
            {
                continue;
            }

            await ExecuteAsync(definition, operation, showDialog: false);
        }
    }

    private async Task ExecuteAsync(ServiceDefinition definition, Func<ServiceDefinition, CancellationToken, Task<ServiceSnapshot>> operation, bool showDialog = true)
    {
        try
        {
            await operation(definition, CancellationToken.None);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or Win32Exception)
        {
            if (showDialog)
            {
                MessageBox.Show(this, ex.Message, $"{definition.DisplayName} error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            RefreshStatuses();
        }
    }

    private void RefreshStatuses()
    {
        foreach (var row in _rows)
        {
            var definition = _definitions[row.Key];
            row.Apply(App.ProcessManager.GetStatus(definition), File.Exists(definition.ExecutablePath));
        }
    }

    private void RefreshAddonInstallState()
    {
        foreach (var row in _addonRows)
        {
            if (_busyAddons.Contains(row.Key))
            {
                continue;
            }
            row.ApplyInstallation(_addonCatalog.IsInstalled(_addonDefinitions[row.Key]));
        }
    }

    private static bool IsExpectedAddonError(Exception ex) =>
        ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception;

    private enum ServiceAction
    {
        Start,
        Stop,
        Restart
    }

    private sealed class ServiceRow : INotifyPropertyChanged
    {
        private string _status = "Stopped";
        private string _portPid = string.Empty;
        private string _uptime = "—";

        public ServiceRow(ServiceDefinition definition)
        {
            Key = definition.Key;
            Name = definition.DisplayName;
            RuntimePath = definition.ExecutablePath;
            _portPid = $"{definition.Port} / —";
        }

        public string Key { get; }
        public string Name { get; }
        public string RuntimePath { get; }
        public string Status { get => _status; private set => SetField(ref _status, value); }
        public string PortPid { get => _portPid; private set => SetField(ref _portPid, value); }
        public string Uptime { get => _uptime; private set => SetField(ref _uptime, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(ServiceSnapshot snapshot, bool runtimeInstalled)
        {
            Status = runtimeInstalled ? snapshot.State.ToString() : "Runtime missing";
            PortPid = $"{snapshot.Port} / {(snapshot.ProcessId?.ToString() ?? "—")}";
            Uptime = snapshot.Uptime is null ? "—" : $"{(int)snapshot.Uptime.Value.TotalHours:00}:{snapshot.Uptime.Value.Minutes:00}:{snapshot.Uptime.Value.Seconds:00}";
        }

        private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class AddonRow : INotifyPropertyChanged
    {
        private string _status = "Not installed";
        private string _installAction = "Install";
        private string _hostStatus = "Host: —";
        private string _configStatus = "Config: —";
        private string _phpStatus = "PHP: —";

        public AddonRow(AddonDefinition definition)
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
        public string Status { get => _status; private set => SetField(ref _status, value); }
        public string InstallAction { get => _installAction; private set => SetField(ref _installAction, value); }
        public string HostStatus { get => _hostStatus; private set => SetField(ref _hostStatus, value); }
        public string ConfigStatus { get => _configStatus; private set => SetField(ref _configStatus, value); }
        public string PhpStatus { get => _phpStatus; private set => SetField(ref _phpStatus, value); }
        public event PropertyChangedEventHandler? PropertyChanged;

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

        private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
