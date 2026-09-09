using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ObservableCollection<ServiceRow> _rows = new();
    private readonly ObservableCollection<AddonRow> _addonRows = new();
    private readonly HashSet<string> _installingAddons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;

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
            RefreshAddonStatuses();
        };
        _refreshTimer.Start();
        RefreshStatuses();
        RefreshAddonStatuses();
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

    private void AddonsNav_Click(object sender, RoutedEventArgs e)
    {
        DashboardPanel.Visibility = Visibility.Collapsed;
        AddonsPanel.Visibility = Visibility.Visible;
        AddonsNavButton.Foreground = Brushes.White;
        AddonsNavButton.FontWeight = FontWeights.SemiBold;
        DashboardNavButton.Foreground = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        DashboardNavButton.FontWeight = FontWeights.Normal;
        RefreshAddonStatuses();
    }

    private async void InstallAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddon(sender, out var addon) || !_installingAddons.Add(addon.Key))
        {
            return;
        }

        var row = _addonRows.First(x => x.Key.Equals(addon.Key, StringComparison.OrdinalIgnoreCase));
        row.SetInstalling();

        try
        {
            await _addonInstaller.InstallAsync(addon);
            RefreshAddonStatuses();

            MessageBox.Show(
                this,
                $"{addon.DisplayName} {addon.Version} installed successfully.\n\nPath: {addon.InstallPath}\nURL: {addon.LocalUrl}",
                $"{addon.DisplayName} installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            row.SetError();
            MessageBox.Show(this, ex.Message, $"{addon.DisplayName} installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _installingAddons.Remove(addon.Key);
            RefreshAddonStatuses();
        }
    }

    private void OpenAddon_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetAddon(sender, out var addon))
        {
            return;
        }

        if (!_addonCatalog.IsInstalled(addon))
        {
            MessageBox.Show(
                this,
                $"{addon.DisplayName} is not installed. Use Install first.",
                $"{addon.DisplayName} not installed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(addon.LocalUrl) { UseShellExecute = true });
        }
        catch (Win32Exception ex)
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

    private async Task RunAsync(
        object sender,
        Func<ServiceDefinition, CancellationToken, Task<ServiceSnapshot>> operation)
    {
        if (sender is not Button { Tag: string key } || !_definitions.TryGetValue(key, out var definition))
        {
            return;
        }

        await ExecuteAsync(definition, operation);
    }

    private async Task RunAllAsync(ServiceAction action)
    {
        var ordered = action == ServiceAction.Stop
            ? _definitions.Values.Reverse()
            : _definitions.Values;

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

    private async Task ExecuteAsync(
        ServiceDefinition definition,
        Func<ServiceDefinition, CancellationToken, Task<ServiceSnapshot>> operation,
        bool showDialog = true)
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

    private void RefreshAddonStatuses()
    {
        foreach (var row in _addonRows)
        {
            if (_installingAddons.Contains(row.Key))
            {
                continue;
            }

            row.Apply(_addonCatalog.IsInstalled(_addonDefinitions[row.Key]));
        }
    }

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

        public string Status
        {
            get => _status;
            private set => SetField(ref _status, value);
        }

        public string PortPid
        {
            get => _portPid;
            private set => SetField(ref _portPid, value);
        }

        public string Uptime
        {
            get => _uptime;
            private set => SetField(ref _uptime, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(ServiceSnapshot snapshot, bool runtimeInstalled)
        {
            Status = runtimeInstalled ? snapshot.State.ToString() : "Runtime missing";
            PortPid = $"{snapshot.Port} / {(snapshot.ProcessId?.ToString() ?? "—")}";
            Uptime = snapshot.Uptime is null
                ? "—"
                : $"{(int)snapshot.Uptime.Value.TotalHours:00}:{snapshot.Uptime.Value.Minutes:00}:{snapshot.Uptime.Value.Seconds:00}";
        }

        private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class AddonRow : INotifyPropertyChanged
    {
        private string _status = "Not installed";
        private string _installAction = "Install";

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

        public string Status
        {
            get => _status;
            private set => SetField(ref _status, value);
        }

        public string InstallAction
        {
            get => _installAction;
            private set => SetField(ref _installAction, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(bool installed)
        {
            Status = installed ? "Installed" : "Not installed";
            InstallAction = installed ? "Reinstall" : "Install";
        }

        public void SetInstalling()
        {
            Status = "Installing...";
            InstallAction = "Installing...";
        }

        public void SetError()
        {
            Status = "Install failed";
            InstallAction = "Retry";
        }

        private void SetField(ref string field, string value, [CallerMemberName] string? propertyName = null)
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
