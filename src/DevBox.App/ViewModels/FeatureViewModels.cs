using System.Collections.ObjectModel;
using System.ComponentModel;
using DevBox.App.Services;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class PhpExtensionRowViewModel(PhpExtensionState state)
{
    public string Name { get; } = state.Name;
    public bool Enabled { get; } = state.Enabled;
    public bool BinaryAvailable { get; } = state.BinaryAvailable;
    public string Status => Enabled ? "Enabled" : BinaryAvailable ? "Disabled" : "Binary missing";
    public string Action => Enabled ? "Disable" : "Enable";
}

public sealed class PhpWindowViewModel : ObservableObject
{
    private readonly PhpManager _phpManager;
    private readonly IProcessManager _processManager;
    private readonly ServiceDefinition _phpService;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private string _activeVersion = "Not installed";

    public PhpWindowViewModel(
        PhpManager phpManager,
        IProcessManager processManager,
        ServiceCatalog serviceCatalog,
        IDialogService dialogs,
        IShellService shell)
    {
        _phpManager = phpManager;
        _processManager = processManager;
        _phpService = serviceCatalog.GetDefaultServices().First(service => service.Key == "php");
        _dialogs = dialogs;
        _shell = shell;
        RefreshCommand = new RelayCommand(Refresh);
        ToggleExtensionCommand = new AsyncRelayCommand(ToggleExtensionAsync);
        OpenPhpIniCommand = new RelayCommand(() => TryOpen(_phpManager.PhpIniPath));
        OpenPhpFolderCommand = new RelayCommand(() => TryOpen(Path.GetDirectoryName(_phpService.ExecutablePath)!));
        Refresh();
    }

    public ObservableCollection<PhpExtensionRowViewModel> Extensions { get; } = new();
    public string ActiveVersion { get => _activeVersion; private set => SetProperty(ref _activeVersion, value); }
    public string PhpIniPath => _phpManager.PhpIniPath;
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleExtensionCommand { get; }
    public RelayCommand OpenPhpIniCommand { get; }
    public RelayCommand OpenPhpFolderCommand { get; }

    private void Refresh()
    {
        ActiveVersion = _phpManager.GetActiveVersion() ?? "Not installed";
        Extensions.Clear();
        foreach (var extension in _phpManager.GetExtensions())
        {
            Extensions.Add(new PhpExtensionRowViewModel(extension));
        }
    }

    private async Task ToggleExtensionAsync(object? parameter)
    {
        if (parameter is not PhpExtensionRowViewModel extension)
        {
            return;
        }
        if (!extension.Enabled && !extension.BinaryAvailable)
        {
            _dialogs.Warning("PHP extension unavailable", $"php_{extension.Name}.dll is not present in the active PHP runtime.");
            return;
        }

        try
        {
            var changed = _phpManager.SetExtensionEnabled(extension.Name, !extension.Enabled);
            if (changed && _processManager.GetStatus(_phpService).State == ServiceState.Running)
            {
                await _processManager.RestartAsync(_phpService);
            }
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or Win32Exception)
        {
            _dialogs.Error("PHP configuration failed", ex.Message);
        }
    }

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
}

public sealed class DatabaseWindowViewModel : ObservableObject
{
    private readonly DatabaseManager _databaseManager;
    private readonly IProcessManager _processManager;
    private readonly ServiceDefinition _mysqlService;
    private readonly IFileDialogService _fileDialogs;
    private readonly IDialogService _dialogs;
    private string _host = "127.0.0.1";
    private string _port = "3306";
    private string _user = "root";
    private string? _password;
    private string _newDatabaseName = string.Empty;
    private string? _selectedDatabase;
    private string _status = "Ready";

    public DatabaseWindowViewModel(
        DatabaseManager databaseManager,
        IProcessManager processManager,
        ServiceCatalog serviceCatalog,
        IFileDialogService fileDialogs,
        IDialogService dialogs)
    {
        _databaseManager = databaseManager;
        _processManager = processManager;
        _mysqlService = serviceCatalog.GetDefaultServices().First(service => service.Key == "mysql");
        _fileDialogs = fileDialogs;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CreateDatabaseCommand = new AsyncRelayCommand(CreateDatabaseAsync);
        BackupCommand = new AsyncRelayCommand(BackupAsync);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync);
    }

    public ObservableCollection<string> Databases { get; } = new();
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public string Port { get => _port; set => SetProperty(ref _port, value); }
    public string User { get => _user; set => SetProperty(ref _user, value); }
    public string? Password { get => _password; set => SetProperty(ref _password, value); }
    public string NewDatabaseName { get => _newDatabaseName; set => SetProperty(ref _newDatabaseName, value); }
    public string? SelectedDatabase { get => _selectedDatabase; set => SetProperty(ref _selectedDatabase, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CreateDatabaseCommand { get; }
    public AsyncRelayCommand BackupCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            Status = "Connecting...";
            await EnsureMysqlRunningAsync();
            var databases = await _databaseManager.ListDatabasesAsync(ConnectionOptions());
            var previous = SelectedDatabase;
            Databases.Clear();
            foreach (var database in databases)
            {
                Databases.Add(database);
            }
            SelectedDatabase = previous is not null && Databases.Contains(previous)
                ? previous
                : Databases.FirstOrDefault();
            Status = $"Connected · {Databases.Count} databases";
        }
        catch (Exception ex) when (IsExpectedDatabaseError(ex))
        {
            Status = "Connection failed";
            _dialogs.Error("Database connection failed", ex.Message);
        }
    }

    private async Task CreateDatabaseAsync()
    {
        if (string.IsNullOrWhiteSpace(NewDatabaseName))
        {
            _dialogs.Warning("Database name required", "Enter a database name first.");
            return;
        }

        try
        {
            await EnsureMysqlRunningAsync();
            await _databaseManager.CreateDatabaseAsync(NewDatabaseName, ConnectionOptions());
            NewDatabaseName = string.Empty;
            await RefreshAsync();
        }
        catch (Exception ex) when (IsExpectedDatabaseError(ex))
        {
            _dialogs.Error("Create database failed", ex.Message);
        }
    }

    private async Task BackupAsync()
    {
        if (SelectedDatabase is null)
        {
            _dialogs.Warning("Database required", "Select a database first.");
            return;
        }

        var suggested = $"{SelectedDatabase}-{DateTime.Now:yyyyMMdd-HHmmss}.sql";
        var destination = _fileDialogs.SaveSqlFile(suggested);
        if (destination is null)
        {
            return;
        }

        try
        {
            await EnsureMysqlRunningAsync();
            var path = await _databaseManager.BackupAsync(SelectedDatabase, destination, ConnectionOptions());
            _dialogs.Info("Backup completed", path);
        }
        catch (Exception ex) when (IsExpectedDatabaseError(ex))
        {
            _dialogs.Error("Backup failed", ex.Message);
        }
    }

    private async Task RestoreAsync()
    {
        if (SelectedDatabase is null)
        {
            _dialogs.Warning("Database required", "Select the target database first.");
            return;
        }
        var source = _fileDialogs.OpenSqlFile();
        if (source is null)
        {
            return;
        }
        if (!_dialogs.Confirm("Restore database", $"Restore {Path.GetFileName(source)} into {SelectedDatabase}? Existing data may be overwritten by statements in the backup."))
        {
            return;
        }

        try
        {
            await EnsureMysqlRunningAsync();
            await _databaseManager.RestoreAsync(SelectedDatabase, source, ConnectionOptions());
            _dialogs.Info("Restore completed", $"Restored into {SelectedDatabase}.");
            await RefreshAsync();
        }
        catch (Exception ex) when (IsExpectedDatabaseError(ex))
        {
            _dialogs.Error("Restore failed", ex.Message);
        }
    }

    private DatabaseConnectionOptions ConnectionOptions()
    {
        if (!int.TryParse(Port, out var port))
        {
            throw new ArgumentException("MySQL port must be a number.", nameof(Port));
        }
        return new DatabaseConnectionOptions(Host.Trim(), port, User.Trim(), Password);
    }

    private async Task EnsureMysqlRunningAsync()
    {
        if (_processManager.GetStatus(_mysqlService).State != ServiceState.Running)
        {
            await _processManager.StartAsync(_mysqlService);
        }
    }

    private static bool IsExpectedDatabaseError(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or Win32Exception;
}

public sealed class SslSiteRowViewModel(SiteDefinition site, bool trusted)
{
    public string Name { get; } = site.Name;
    public string Domain { get; } = site.Domain;
    public bool HttpsEnabled { get; } = site.HttpsEnabled;
    public bool Trusted { get; } = trusted;
    public string HttpsStatus => HttpsEnabled ? "HTTPS enabled" : "HTTP only";
    public string TrustStatus => Trusted ? "Trusted" : "Not trusted";
}

public sealed class SslWindowViewModel : ObservableObject
{
    private readonly SiteManager _siteManager;
    private readonly LocalCertificateManager _certificateManager;
    private readonly IHostMappingService _hostMappingService;
    private readonly IProcessManager _processManager;
    private readonly IReadOnlyDictionary<string, ServiceDefinition> _services;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    public SslWindowViewModel(
        SiteManager siteManager,
        LocalCertificateManager certificateManager,
        IHostMappingService hostMappingService,
        IProcessManager processManager,
        ServiceCatalog serviceCatalog,
        IDialogService dialogs,
        IShellService shell)
    {
        _siteManager = siteManager;
        _certificateManager = certificateManager;
        _hostMappingService = hostMappingService;
        _processManager = processManager;
        _services = serviceCatalog.GetDefaultServices().ToDictionary(service => service.Key, StringComparer.OrdinalIgnoreCase);
        _dialogs = dialogs;
        _shell = shell;
        RefreshCommand = new RelayCommand(Refresh);
        EnableHttpsCommand = new AsyncRelayCommand(EnableHttpsAsync);
        DisableHttpsCommand = new AsyncRelayCommand(DisableHttpsAsync);
        TrustCommand = new RelayCommand(Trust);
        UntrustCommand = new RelayCommand(Untrust);
        OpenHttpsCommand = new AsyncRelayCommand(OpenHttpsAsync);
        Refresh();
    }

    public ObservableCollection<SslSiteRowViewModel> Sites { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand EnableHttpsCommand { get; }
    public AsyncRelayCommand DisableHttpsCommand { get; }
    public RelayCommand TrustCommand { get; }
    public RelayCommand UntrustCommand { get; }
    public AsyncRelayCommand OpenHttpsCommand { get; }

    private void Refresh()
    {
        Sites.Clear();
        foreach (var site in _siteManager.GetSites())
        {
            var trusted = false;
            try
            {
                trusted = _certificateManager.IsTrustedForCurrentUser(site.Domain);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                _dialogs.Warning("Certificate check failed", $"{site.Domain}: {ex.Message}");
            }
            Sites.Add(new SslSiteRowViewModel(site, trusted));
        }
    }

    private async Task EnableHttpsAsync(object? parameter)
    {
        if (parameter is not SslSiteRowViewModel site)
        {
            return;
        }
        try
        {
            _certificateManager.Ensure(site.Domain);
            await _hostMappingService.EnsureAsync(site.Domain);
            _siteManager.SetHttps(site.Name, true);
            await RestartNginxIfRunningAsync();
            Refresh();
        }
        catch (Exception ex) when (IsExpectedSslError(ex))
        {
            _dialogs.Error("Enable HTTPS failed", ex.Message);
        }
    }

    private async Task DisableHttpsAsync(object? parameter)
    {
        if (parameter is not SslSiteRowViewModel site)
        {
            return;
        }
        try
        {
            _siteManager.SetHttps(site.Name, false);
            await RestartNginxIfRunningAsync();
            Refresh();
        }
        catch (Exception ex) when (IsExpectedSslError(ex))
        {
            _dialogs.Error("Disable HTTPS failed", ex.Message);
        }
    }

    private void Trust(object? parameter)
    {
        if (parameter is not SslSiteRowViewModel site)
        {
            return;
        }
        try
        {
            _certificateManager.TrustForCurrentUser(site.Domain);
            Refresh();
        }
        catch (Exception ex) when (IsExpectedSslError(ex))
        {
            _dialogs.Error("Trust certificate failed", ex.Message);
        }
    }

    private void Untrust(object? parameter)
    {
        if (parameter is not SslSiteRowViewModel site)
        {
            return;
        }
        try
        {
            _certificateManager.UntrustForCurrentUser(site.Domain);
            Refresh();
        }
        catch (Exception ex) when (IsExpectedSslError(ex))
        {
            _dialogs.Error("Untrust certificate failed", ex.Message);
        }
    }

    private async Task OpenHttpsAsync(object? parameter)
    {
        if (parameter is not SslSiteRowViewModel site)
        {
            return;
        }
        if (!site.HttpsEnabled)
        {
            _dialogs.Warning("HTTPS disabled", $"Enable HTTPS for {site.Domain} first.");
            return;
        }
        try
        {
            await _hostMappingService.EnsureAsync(site.Domain);
            await EnsureRunningAsync("php");
            await EnsureRunningAsync("nginx");
            _shell.Open($"https://{site.Domain}");
        }
        catch (Exception ex) when (IsExpectedSslError(ex))
        {
            _dialogs.Error("Open HTTPS site failed", ex.Message);
        }
    }

    private async Task RestartNginxIfRunningAsync()
    {
        var nginx = _services["nginx"];
        if (_processManager.GetStatus(nginx).State == ServiceState.Running)
        {
            await _processManager.RestartAsync(nginx);
        }
    }

    private async Task EnsureRunningAsync(string key)
    {
        var service = _services[key];
        if (_processManager.GetStatus(service).State != ServiceState.Running)
        {
            await _processManager.StartAsync(service);
        }
    }

    private static bool IsExpectedSslError(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or FileNotFoundException or Win32Exception or System.Security.Cryptography.CryptographicException;
}

public sealed class SetupItemViewModel(EnvironmentReadinessItem item)
{
    public string Key { get; } = item.Key;
    public string Name { get; } = item.DisplayName;
    public bool Ready { get; } = item.Ready;
    public bool CanInstallAutomatically { get; } = item.CanInstallAutomatically;
    public string Status => Ready ? "Ready" : CanInstallAutomatically ? "Missing · can install" : "Missing · manual action required";
    public string Details { get; } = item.Details;
}

public sealed class FirstRunViewModel : ObservableObject
{
    private readonly EnvironmentReadinessService _readinessService;
    private readonly RuntimeCatalog _runtimeCatalog;
    private readonly IRuntimeManager _runtimeManager;
    private readonly IDialogService _dialogs;
    private string _status = "Check the environment and install verified runtimes.";

    public FirstRunViewModel(
        EnvironmentReadinessService readinessService,
        RuntimeCatalog runtimeCatalog,
        IRuntimeManager runtimeManager,
        IDialogService dialogs)
    {
        _readinessService = readinessService;
        _runtimeCatalog = runtimeCatalog;
        _runtimeManager = runtimeManager;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(Refresh);
        InstallCommand = new AsyncRelayCommand(InstallAsync);
        InstallAllCommand = new AsyncRelayCommand(InstallAllAsync);
        Refresh();
    }

    public ObservableCollection<SetupItemViewModel> Items { get; } = new();
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsReady => Items.All(item => item.Ready);
    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallAllCommand { get; }

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in _readinessService.Check().Items)
        {
            Items.Add(new SetupItemViewModel(item));
        }
        OnPropertyChanged(nameof(IsReady));
        Status = IsReady ? "Environment is ready." : "Some components are missing.";
    }

    private async Task InstallAsync(object? parameter)
    {
        if (parameter is not SetupItemViewModel item || item.Ready || !item.CanInstallAutomatically)
        {
            return;
        }
        try
        {
            Status = $"Installing {item.Name}...";
            var definition = _runtimeCatalog.GetRecommended(item.Key);
            await _runtimeManager.InstallAsync(definition);
            Refresh();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            Status = $"Installation failed: {item.Name}";
            _dialogs.Error("Runtime installation failed", ex.Message);
        }
    }

    private async Task InstallAllAsync()
    {
        foreach (var item in Items.Where(item => !item.Ready && item.CanInstallAutomatically).ToArray())
        {
            await InstallAsync(item);
        }
        Refresh();
    }
}
