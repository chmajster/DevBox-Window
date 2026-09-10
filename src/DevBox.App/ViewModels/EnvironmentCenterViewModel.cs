using System.Collections.ObjectModel;
using System.Windows.Input;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class EnvironmentCenterViewModel : ObservableObject, IDisposable
{
    private readonly string _rootPath;
    private readonly PlatformTaskCenter _taskCenter;
    private string _status = "Ready";
    private string _projectName = string.Empty;
    private string _selectedProfileKey = "php-minimal";
    private string _runtimeKey = "php";
    private string _runtimeVersion = "8.5.10";
    private string _databaseEngine = "mysql";
    private string _databaseVersion = "8.4.11";
    private string _databasePort = "3306";
    private string _databaseName = string.Empty;
    private string _backupPath = string.Empty;
    private string _snapshotPath = string.Empty;
    private string _transferPath = string.Empty;
    private string _gitUrl = string.Empty;
    private string _gitProjectName = string.Empty;
    private string _configurationKey = "php";
    private string _configurationText = string.Empty;
    private string _secretKey = string.Empty;
    private string _secretValue = string.Empty;
    private string _marketplaceCatalogUrl = string.Empty;
    private string _marketplaceSignatureUrl = string.Empty;
    private string _marketplacePublicKey = string.Empty;
    private string _wordpressName = string.Empty;
    private string _wordpressTitle = "Local WordPress";
    private string _wordpressAdminUser = "admin";
    private string _wordpressAdminEmail = string.Empty;
    private string _wordpressAdminPassword = string.Empty;
    private bool _disposed;

    public EnvironmentCenterViewModel()
    {
        _rootPath = App.DevBoxRoot;
        _taskCenter = new PlatformTaskCenter(_rootPath);
        _taskCenter.TaskChanged += TaskCenterOnTaskChanged;

        RefreshCommand = new RelayCommand(Refresh);
        GenerateLockCommand = new RelayCommand(GenerateLock);
        ApplyLockCommand = new AsyncRelayCommand(ApplyLockAsync);
        ApplyProfileCommand = new AsyncRelayCommand(ApplyProfileAsync);
        RuntimeInstallCommand = new AsyncRelayCommand(RuntimeInstallAsync);
        RuntimeActivateCommand = new AsyncRelayCommand(RuntimeActivateAsync);
        RuntimeRemoveCommand = new AsyncRelayCommand(RuntimeRemoveAsync);
        DatabaseRegisterCommand = new RelayCommand(DatabaseRegister);
        DatabaseStartCommand = new AsyncRelayCommand(DatabaseStartAsync);
        DatabaseStopCommand = new AsyncRelayCommand(DatabaseStopAsync);
        DatabaseBackupCommand = new AsyncRelayCommand(DatabaseBackupAsync);
        DatabaseRestoreCommand = new AsyncRelayCommand(DatabaseRestoreAsync);
        SnapshotCreateCommand = new AsyncRelayCommand(SnapshotCreateAsync);
        SnapshotRestoreCommand = new AsyncRelayCommand(SnapshotRestoreAsync);
        TransferExportCommand = new AsyncRelayCommand(TransferExportAsync);
        TransferImportCommand = new AsyncRelayCommand(TransferImportAsync);
        GitBootstrapCommand = new AsyncRelayCommand(GitBootstrapAsync);
        DiagnosticsCommand = new RelayCommand(RefreshDiagnostics);
        ConfigurationLoadCommand = new RelayCommand(ConfigurationLoad);
        ConfigurationSaveCommand = new AsyncRelayCommand(ConfigurationSaveAsync);
        SecretSetCommand = new RelayCommand(SecretSet);
        SecretDeleteCommand = new RelayCommand(SecretDelete);
        LocalCaTrustCommand = new RelayCommand(LocalCaTrust);
        MarketplaceConfigureCommand = new RelayCommand(MarketplaceConfigure);
        MarketplaceSyncCommand = new AsyncRelayCommand(MarketplaceSyncAsync);
        WordPressCreateCommand = new AsyncRelayCommand(WordPressCreateAsync);
        WordPressStatusCommand = new AsyncRelayCommand(WordPressStatusAsync);
        Refresh();
    }

    public ObservableCollection<EnvironmentProfile> Profiles { get; } = [];
    public ObservableCollection<RuntimeVersionStatus> Runtimes { get; } = [];
    public ObservableCollection<DatabaseRuntimeInstance> DatabaseRuntimes { get; } = [];
    public ObservableCollection<PlatformTaskSnapshot> Tasks { get; } = [];
    public ObservableCollection<AdvancedDiagnosticFinding> DiagnosticFindings { get; } = [];
    public ObservableCollection<string> SecretKeys { get; } = [];

    public string RootPath => _rootPath;
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string ProjectName { get => _projectName; set => SetProperty(ref _projectName, value); }
    public string SelectedProfileKey { get => _selectedProfileKey; set => SetProperty(ref _selectedProfileKey, value); }
    public string RuntimeKey { get => _runtimeKey; set => SetProperty(ref _runtimeKey, value); }
    public string RuntimeVersion { get => _runtimeVersion; set => SetProperty(ref _runtimeVersion, value); }
    public string DatabaseEngine { get => _databaseEngine; set => SetProperty(ref _databaseEngine, value); }
    public string DatabaseVersion { get => _databaseVersion; set => SetProperty(ref _databaseVersion, value); }
    public string DatabasePort { get => _databasePort; set => SetProperty(ref _databasePort, value); }
    public string DatabaseName { get => _databaseName; set => SetProperty(ref _databaseName, value); }
    public string BackupPath { get => _backupPath; set => SetProperty(ref _backupPath, value); }
    public string SnapshotPath { get => _snapshotPath; set => SetProperty(ref _snapshotPath, value); }
    public string TransferPath { get => _transferPath; set => SetProperty(ref _transferPath, value); }
    public string GitUrl { get => _gitUrl; set => SetProperty(ref _gitUrl, value); }
    public string GitProjectName { get => _gitProjectName; set => SetProperty(ref _gitProjectName, value); }
    public string ConfigurationKey { get => _configurationKey; set => SetProperty(ref _configurationKey, value); }
    public string ConfigurationText { get => _configurationText; set => SetProperty(ref _configurationText, value); }
    public string SecretKey { get => _secretKey; set => SetProperty(ref _secretKey, value); }
    public string SecretValue { get => _secretValue; set => SetProperty(ref _secretValue, value); }
    public string MarketplaceCatalogUrl { get => _marketplaceCatalogUrl; set => SetProperty(ref _marketplaceCatalogUrl, value); }
    public string MarketplaceSignatureUrl { get => _marketplaceSignatureUrl; set => SetProperty(ref _marketplaceSignatureUrl, value); }
    public string MarketplacePublicKey { get => _marketplacePublicKey; set => SetProperty(ref _marketplacePublicKey, value); }
    public string WordPressName { get => _wordpressName; set => SetProperty(ref _wordpressName, value); }
    public string WordPressTitle { get => _wordpressTitle; set => SetProperty(ref _wordpressTitle, value); }
    public string WordPressAdminUser { get => _wordpressAdminUser; set => SetProperty(ref _wordpressAdminUser, value); }
    public string WordPressAdminEmail { get => _wordpressAdminEmail; set => SetProperty(ref _wordpressAdminEmail, value); }
    public string WordPressAdminPassword { get => _wordpressAdminPassword; set => SetProperty(ref _wordpressAdminPassword, value); }

    public ICommand RefreshCommand { get; }
    public ICommand GenerateLockCommand { get; }
    public ICommand ApplyLockCommand { get; }
    public ICommand ApplyProfileCommand { get; }
    public ICommand RuntimeInstallCommand { get; }
    public ICommand RuntimeActivateCommand { get; }
    public ICommand RuntimeRemoveCommand { get; }
    public ICommand DatabaseRegisterCommand { get; }
    public ICommand DatabaseStartCommand { get; }
    public ICommand DatabaseStopCommand { get; }
    public ICommand DatabaseBackupCommand { get; }
    public ICommand DatabaseRestoreCommand { get; }
    public ICommand SnapshotCreateCommand { get; }
    public ICommand SnapshotRestoreCommand { get; }
    public ICommand TransferExportCommand { get; }
    public ICommand TransferImportCommand { get; }
    public ICommand GitBootstrapCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand ConfigurationLoadCommand { get; }
    public ICommand ConfigurationSaveCommand { get; }
    public ICommand SecretSetCommand { get; }
    public ICommand SecretDeleteCommand { get; }
    public ICommand LocalCaTrustCommand { get; }
    public ICommand MarketplaceConfigureCommand { get; }
    public ICommand MarketplaceSyncCommand { get; }
    public ICommand WordPressCreateCommand { get; }
    public ICommand WordPressStatusCommand { get; }

    public void Refresh()
    {
        RunUiAction(() =>
        {
            Replace(Profiles, new EnvironmentProfileService(_rootPath).GetProfiles());
            using var runtimes = new RuntimePlatformService(_rootPath);
            Replace(Runtimes, runtimes.GetStatuses());
            using var databases = new DatabaseRuntimeService(_rootPath);
            Replace(DatabaseRuntimes, databases.GetInstances());
            Replace(Tasks, _taskCenter.GetTasks());
            RefreshDiagnostics();
            RefreshSecrets();
            Status = "Environment Center refreshed.";
        });
    }

    private void GenerateLock() => RunUiAction(() =>
    {
        using var service = new EnvironmentLockService(_rootPath);
        var value = service.Generate(RequireProjectPath(), string.IsNullOrWhiteSpace(SelectedProfileKey) ? null : SelectedProfileKey);
        Status = $"Generated {EnvironmentLockService.LockFileName} for {value.ProjectName}.";
    });

    private Task ApplyLockAsync() => QueueAsync("Apply environment lock", async token =>
    {
        using var service = new EnvironmentLockService(_rootPath);
        var result = await service.ApplyLockAsync(RequireProjectPath(), token);
        Status = DescribeApply(result);
    });

    private Task ApplyProfileAsync() => QueueAsync("Apply environment profile", async token =>
    {
        using var service = new EnvironmentLockService(_rootPath);
        var result = await service.ApplyProfileAsync(RequireProjectPath(), Require(SelectedProfileKey, "Profile"), token);
        Status = DescribeApply(result);
    });

    private Task RuntimeInstallAsync() => QueueAsync("Install runtime", async token =>
    {
        using var service = new RuntimePlatformService(_rootPath);
        await service.InstallAsync(Require(RuntimeKey, "Runtime key"), Require(RuntimeVersion, "Runtime version"), token);
        Status = $"Installed {RuntimeKey} {RuntimeVersion}.";
    });

    private Task RuntimeActivateAsync() => QueueAsync("Activate runtime", async token =>
    {
        using var service = new RuntimePlatformService(_rootPath);
        await service.ActivateAsync(Require(RuntimeKey, "Runtime key"), Require(RuntimeVersion, "Runtime version"), token);
        Status = $"Activated {RuntimeKey} {RuntimeVersion}.";
    });

    private Task RuntimeRemoveAsync() => QueueAsync("Remove runtime", async token =>
    {
        using var service = new RuntimePlatformService(_rootPath);
        await service.RemoveAsync(Require(RuntimeKey, "Runtime key"), Require(RuntimeVersion, "Runtime version"), token);
        Status = $"Removed {RuntimeKey} {RuntimeVersion}.";
    });

    private void DatabaseRegister() => RunUiAction(() =>
    {
        using var service = new DatabaseRuntimeService(_rootPath);
        var port = ParseOptionalPort(DatabasePort);
        var value = service.Register(Require(DatabaseEngine, "Database engine"), Require(DatabaseVersion, "Database version"), port);
        Status = $"Registered {value.Engine} {value.Version} on port {value.Port}.";
        Replace(DatabaseRuntimes, service.GetInstances());
    });

    private Task DatabaseStartAsync() => QueueAsync("Start database runtime", async token =>
    {
        using var service = new DatabaseRuntimeService(_rootPath);
        var value = await service.StartAsync(Require(DatabaseEngine, "Database engine"), Require(DatabaseVersion, "Database version"), ParseOptionalPort(DatabasePort), token);
        Status = $"{value.DisplayName}: {value.State}.";
    });

    private Task DatabaseStopAsync() => QueueAsync("Stop database runtime", async token =>
    {
        using var service = new DatabaseRuntimeService(_rootPath);
        var value = await service.StopAsync(Require(DatabaseEngine, "Database engine"), Require(DatabaseVersion, "Database version"), cancellationToken: token);
        Status = $"{value.DisplayName}: {value.State}.";
    });

    private Task DatabaseBackupAsync() => QueueAsync("Backup database", async token =>
    {
        using var service = new DatabaseRuntimeService(_rootPath);
        var result = await service.BackupAsync(Require(DatabaseEngine, "Database engine"), Require(DatabaseVersion, "Database version"), Require(DatabaseName, "Database name"), cancellationToken: token);
        BackupPath = result.BackupPath;
        Status = $"Backup created: {result.BackupPath}";
    });

    private Task DatabaseRestoreAsync() => QueueAsync("Restore database", async token =>
    {
        using var service = new DatabaseRuntimeService(_rootPath);
        await service.RestoreAsync(Require(DatabaseEngine, "Database engine"), Require(DatabaseVersion, "Database version"), Require(DatabaseName, "Database name"), Require(BackupPath, "Backup path"), cancellationToken: token);
        Status = $"Restored database {DatabaseName}.";
    });

    private Task SnapshotCreateAsync() => QueueAsync("Create project snapshot", async token =>
    {
        var result = await new ProjectSnapshotService(_rootPath).CreateAsync(RequireProjectPath(), cancellationToken: token);
        SnapshotPath = result.SnapshotPath;
        Status = $"Snapshot created: {result.SnapshotPath}";
    });

    private Task SnapshotRestoreAsync() => QueueAsync("Restore project snapshot", async token =>
    {
        var target = Require(ProjectName, "Target project name");
        var path = await new ProjectSnapshotService(_rootPath).RestoreAsync(Require(SnapshotPath, "Snapshot path"), target, overwrite: true, cancellationToken: token);
        Status = $"Snapshot restored to {path}.";
    });

    private Task TransferExportAsync() => QueueAsync("Export project", async token =>
    {
        var result = await new ProjectTransferService(_rootPath).ExportAsync(RequireProjectPath(), cancellationToken: token);
        TransferPath = result.ArchivePath;
        Status = $"Project export created: {result.ArchivePath}";
    });

    private Task TransferImportAsync() => QueueAsync("Import project", async token =>
    {
        var path = await new ProjectTransferService(_rootPath).ImportAsync(Require(TransferPath, "Project archive"), string.IsNullOrWhiteSpace(ProjectName) ? null : ProjectName, overwrite: true, cancellationToken: token);
        Status = $"Project imported to {path}.";
    });

    private Task GitBootstrapAsync() => QueueAsync("Bootstrap Git project", async token =>
    {
        var service = new GitProjectBootstrapService(_rootPath);
        var result = await service.BootstrapAsync(new GitBootstrapRequest(
            Require(GitUrl, "Git repository URL"),
            Require(GitProjectName, "Git project name"),
            ProfileKey: string.IsNullOrWhiteSpace(SelectedProfileKey) ? null : SelectedProfileKey), token);
        ProjectName = Path.GetFileName(result.ProjectRoot);
        Status = $"Bootstrapped {result.ProjectRoot}.";
    });

    private void RefreshDiagnostics()
    {
        var report = new AdvancedDiagnosticsService(_rootPath).Run();
        Replace(DiagnosticFindings, report.Findings);
        Status = report.HasErrors ? "Diagnostics found errors." : report.HasWarnings ? "Diagnostics found warnings." : "Diagnostics passed.";
    }

    private void ConfigurationLoad() => RunUiAction(() =>
    {
        ConfigurationText = new ConfigurationFileService(_rootPath).Read(Require(ConfigurationKey, "Configuration key"));
        Status = $"Loaded {ConfigurationKey} configuration.";
    });

    private Task ConfigurationSaveAsync() => QueueAsync("Validate and save configuration", async token =>
    {
        var result = await new ConfigurationFileService(_rootPath).SaveValidatedAsync(Require(ConfigurationKey, "Configuration key"), ConfigurationText, token);
        if (!result.IsValid)
            throw new InvalidDataException(result.Message);
        Status = result.Message;
    });

    private void SecretSet() => RunUiAction(() =>
    {
        if (string.IsNullOrEmpty(SecretValue))
            throw new InvalidOperationException("Secret value is empty.");
        new SecureSecretStore(_rootPath).Set(Require(SecretKey, "Secret key"), SecretValue);
        SecretValue = string.Empty;
        RefreshSecrets();
        Status = $"Stored secret '{SecretKey}'.";
    });

    private void SecretDelete() => RunUiAction(() =>
    {
        var removed = new SecureSecretStore(_rootPath).Delete(Require(SecretKey, "Secret key"));
        RefreshSecrets();
        Status = removed ? $"Deleted secret '{SecretKey}'." : $"Secret '{SecretKey}' was not found.";
    });

    private void LocalCaTrust() => RunUiAction(() =>
    {
        using var service = new LocalCertificateAuthorityService(_rootPath);
        var ca = service.EnsureRootTrusted();
        Status = $"DevBox Local CA trusted. Thumbprint: {ca.Thumbprint}";
    });

    private void MarketplaceConfigure() => RunUiAction(() =>
    {
        using var service = new AddonMarketplaceService(_rootPath);
        service.Configure(Require(MarketplaceCatalogUrl, "Marketplace catalog URL"), Require(MarketplaceSignatureUrl, "Marketplace signature URL"), Require(MarketplacePublicKey, "Marketplace public key"));
        Status = "ADDONS Marketplace source configured.";
    });

    private Task MarketplaceSyncAsync() => QueueAsync("Sync ADDONS Marketplace", async token =>
    {
        using var service = new AddonMarketplaceService(_rootPath);
        var addons = await service.SyncAsync(token);
        Status = $"Marketplace synchronized: {addons.Count} ADDON(s).";
    });

    private Task WordPressCreateAsync() => QueueAsync("Create WordPress site", async token =>
    {
        var result = await new WordPressToolkitService(_rootPath).CreateSiteAsync(new WordPressSiteRequest(
            Require(WordPressName, "WordPress project name"),
            Require(WordPressTitle, "WordPress title"),
            Require(WordPressAdminUser, "WordPress admin user"),
            Require(WordPressAdminPassword, "WordPress admin password"),
            Require(WordPressAdminEmail, "WordPress admin email")), token);
        WordPressAdminPassword = string.Empty;
        ProjectName = WordPressName;
        Status = $"WordPress created: {result.AdminUrl}";
    });

    private Task WordPressStatusAsync() => QueueAsync("Check WordPress status", async token =>
    {
        var values = await new WordPressToolkitService(_rootPath).GetStatusAsync(RequireProjectPath(), token);
        Status = string.Join(" | ", values);
    });

    private async Task QueueAsync(string name, Func<CancellationToken, Task> operation)
    {
        try
        {
            var id = _taskCenter.Enqueue(name, async (progress, token) =>
            {
                progress.Report((5, "Started"));
                await operation(token);
                progress.Report((100, "Completed"));
            });
            await _taskCenter.WaitAsync(id);
            Replace(Tasks, _taskCenter.GetTasks());
            RefreshCollectionsAfterTask();
            var snapshot = _taskCenter.GetTask(id);
            if (snapshot?.State == PlatformTaskState.Failed)
                Status = snapshot.Error ?? "Task failed.";
            else if (snapshot?.State == PlatformTaskState.Cancelled)
                Status = "Task cancelled.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or ArgumentException or KeyNotFoundException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            Status = ex.Message;
        }
    }

    private void RefreshCollectionsAfterTask()
    {
        try
        {
            using var runtimes = new RuntimePlatformService(_rootPath);
            Replace(Runtimes, runtimes.GetStatuses());
            using var databases = new DatabaseRuntimeService(_rootPath);
            Replace(DatabaseRuntimes, databases.GetInstances());
            RefreshSecrets();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or PlatformNotSupportedException)
        {
            Status = ex.Message;
        }
    }

    private void RefreshSecrets()
    {
        try
        {
            Replace(SecretKeys, new SecureSecretStore(_rootPath).ListKeys());
        }
        catch (PlatformNotSupportedException)
        {
            SecretKeys.Clear();
        }
    }

    private void TaskCenterOnTaskChanged(object? sender, PlatformTaskSnapshot e)
    {
        App.Current.Dispatcher.InvokeAsync(() =>
        {
            var existing = Tasks.FirstOrDefault(item => item.Id == e.Id);
            if (existing is not null)
                Tasks.Remove(existing);
            Tasks.Insert(0, e);
        });
    }

    private string RequireProjectPath()
    {
        var name = Require(ProjectName, "Project name/path");
        return Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(_rootPath, "www", name));
    }

    private static int? ParseOptionalPort(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("Database port must be between 1 and 65535.");
        return port;
    }

    private static string Require(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{name} is required.")
        : value.Trim();

    private static string DescribeApply(EnvironmentApplyResult result) =>
        $"Applied environment: {result.Applied.Count} action(s), {result.Warnings.Count} warning(s).";

    private void RunUiAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            Status = ex.Message;
        }
    }

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> values)
    {
        destination.Clear();
        foreach (var value in values)
            destination.Add(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _taskCenter.TaskChanged -= TaskCenterOnTaskChanged;
        _taskCenter.Dispose();
    }
}
