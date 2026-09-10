using System.Collections.ObjectModel;
using System.ComponentModel;
using DevBox.App.Services;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class ProjectSiteRow(SiteDefinition site, string projectRoot, ProjectKind kind)
{
    public SiteDefinition Site { get; } = site;
    public string Name => Site.Name;
    public string Domain => Site.Domain;
    public string ProjectRoot { get; } = projectRoot;
    public string PhpVersion => Site.PhpVersion ?? "global";
    public string Https => Site.HttpsEnabled ? "HTTPS" : "HTTP";
    public ProjectKind Kind { get; } = kind;
}

public sealed class ManagedServiceRow(ManagedServiceManifest manifest, ServiceDefinition definition, ServiceSnapshot snapshot)
{
    public ManagedServiceManifest Manifest { get; } = manifest;
    public ServiceDefinition Definition { get; } = definition;
    public ServiceSnapshot Snapshot { get; } = snapshot;
    public string Key => Manifest.Key;
    public string Name => Manifest.DisplayName;
    public string State => Snapshot.State.ToString();
    public string Port => Definition.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Runtime => File.Exists(Definition.ExecutablePath) ? "Ready" : "Missing";
}

public sealed class ProjectManagerWindowViewModel : ObservableObject
{
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectProvisioningService _provisioning;
    private readonly ProjectStackProfileService _profiles;
    private readonly ProjectCommandService _commands;
    private readonly XdebugConfigurationService _xdebug;
    private readonly ManagedServiceCatalog _managedServices;
    private readonly SiteManager _sites;
    private readonly IProcessManager _processManager;
    private readonly IHostMappingService _hosts;
    private readonly IFileDialogService _files;
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    private ProjectStackProfile? _selectedProfile;
    private ProjectSiteRow? _selectedProject;
    private ProjectCommandPreset? _selectedCommand;
    private ManagedServiceRow? _selectedManagedService;
    private string _projectName = string.Empty;
    private string _projectDomain = string.Empty;
    private string _importPath = string.Empty;
    private string _importName = string.Empty;
    private bool _copyImport = true;
    private string _status = "Ready";
    private string _detectionSummary = "Select a project directory to detect its stack.";
    private string _healthSummary = "Select a registered project.";
    private string _commandOutput = string.Empty;
    private bool _xdebugEnabled;
    private string _xdebugMode = "debug,develop";
    private string _xdebugPort = "9003";
    private string _xdebugStartWithRequest = "trigger";
    private string _xdebugStatus = "Not checked";

    public ProjectManagerWindowViewModel(
        ProjectWorkspaceService workspace,
        ProjectProvisioningService provisioning,
        ProjectStackProfileService profiles,
        ProjectCommandService commands,
        XdebugConfigurationService xdebug,
        ManagedServiceCatalog managedServices,
        SiteManager sites,
        IProcessManager processManager,
        IHostMappingService hosts,
        IFileDialogService files,
        IDialogService dialogs,
        IShellService shell)
    {
        _workspace = workspace;
        _provisioning = provisioning;
        _profiles = profiles;
        _commands = commands;
        _xdebug = xdebug;
        _managedServices = managedServices;
        _sites = sites;
        _processManager = processManager;
        _hosts = hosts;
        _files = files;
        _dialogs = dialogs;
        _shell = shell;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CreateProjectCommand = new AsyncRelayCommand(CreateProjectAsync, () => SelectedProfile is not null && !string.IsNullOrWhiteSpace(ProjectName));
        BrowseImportCommand = new RelayCommand(BrowseImport);
        DetectImportCommand = new RelayCommand(DetectImport, () => Directory.Exists(ImportPath));
        ImportProjectCommand = new AsyncRelayCommand(ImportProjectAsync, () => Directory.Exists(ImportPath) && !string.IsNullOrWhiteSpace(ImportName));
        CheckHealthCommand = new AsyncRelayCommand(CheckHealthAsync, () => SelectedProject is not null);
        RepairCommand = new AsyncRelayCommand(RepairAsync, () => SelectedProject is not null);
        RunCommand = new AsyncRelayCommand(RunProjectCommandAsync, () => SelectedProject is not null && SelectedCommand is not null);
        OpenProjectCommand = new RelayCommand(OpenProject, () => SelectedProject is not null);
        OpenSiteCommand = new RelayCommand(OpenSite, () => SelectedProject is not null);
        RefreshXdebugCommand = new RelayCommand(RefreshXdebug);
        ApplyXdebugCommand = new RelayCommand(ApplyXdebug);
        AddMailpitCommand = new RelayCommand(() => AddManagedService(ManagedServiceCatalog.MailpitTemplate()));
        AddRedisCommand = new RelayCommand(() => AddManagedService(ManagedServiceCatalog.RedisTemplate()));
        StartManagedServiceCommand = new AsyncRelayCommand(StartManagedServiceAsync, () => SelectedManagedService is not null);
        StopManagedServiceCommand = new AsyncRelayCommand(StopManagedServiceAsync, () => SelectedManagedService is not null);
        RestartManagedServiceCommand = new AsyncRelayCommand(RestartManagedServiceAsync, () => SelectedManagedService is not null);
        ToggleManagedServiceCommand = new RelayCommand(ToggleManagedService, () => SelectedManagedService is not null);
    }

    public ObservableCollection<ProjectStackProfile> Profiles { get; } = new();
    public ObservableCollection<ProjectSiteRow> Projects { get; } = new();
    public ObservableCollection<ProjectHealthCheck> HealthChecks { get; } = new();
    public ObservableCollection<ProjectCommandPreset> ProjectCommands { get; } = new();
    public ObservableCollection<ManagedServiceRow> ManagedServices { get; } = new();

    public ProjectStackProfile? SelectedProfile
    {
        get => _selectedProfile;
        set { if (SetProperty(ref _selectedProfile, value)) CreateProjectCommand.RaiseCanExecuteChanged(); }
    }

    public ProjectSiteRow? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (!SetProperty(ref _selectedProject, value)) return;
            LoadProjectCommands();
            CheckHealthCommand.RaiseCanExecuteChanged();
            RepairCommand.RaiseCanExecuteChanged();
            RunCommand.RaiseCanExecuteChanged();
            OpenProjectCommand.RaiseCanExecuteChanged();
            OpenSiteCommand.RaiseCanExecuteChanged();
        }
    }

    public ProjectCommandPreset? SelectedCommand
    {
        get => _selectedCommand;
        set { if (SetProperty(ref _selectedCommand, value)) RunCommand.RaiseCanExecuteChanged(); }
    }

    public ManagedServiceRow? SelectedManagedService
    {
        get => _selectedManagedService;
        set
        {
            if (!SetProperty(ref _selectedManagedService, value)) return;
            StartManagedServiceCommand.RaiseCanExecuteChanged();
            StopManagedServiceCommand.RaiseCanExecuteChanged();
            RestartManagedServiceCommand.RaiseCanExecuteChanged();
            ToggleManagedServiceCommand.RaiseCanExecuteChanged();
        }
    }

    public string ProjectName
    {
        get => _projectName;
        set { if (SetProperty(ref _projectName, value)) CreateProjectCommand.RaiseCanExecuteChanged(); }
    }
    public string ProjectDomain { get => _projectDomain; set => SetProperty(ref _projectDomain, value); }
    public string ImportPath
    {
        get => _importPath;
        set
        {
            if (!SetProperty(ref _importPath, value)) return;
            DetectImportCommand.RaiseCanExecuteChanged();
            ImportProjectCommand.RaiseCanExecuteChanged();
        }
    }
    public string ImportName
    {
        get => _importName;
        set { if (SetProperty(ref _importName, value)) ImportProjectCommand.RaiseCanExecuteChanged(); }
    }
    public bool CopyImport { get => _copyImport; set => SetProperty(ref _copyImport, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string DetectionSummary { get => _detectionSummary; private set => SetProperty(ref _detectionSummary, value); }
    public string HealthSummary { get => _healthSummary; private set => SetProperty(ref _healthSummary, value); }
    public string CommandOutput { get => _commandOutput; private set => SetProperty(ref _commandOutput, value); }
    public bool XdebugEnabled { get => _xdebugEnabled; set => SetProperty(ref _xdebugEnabled, value); }
    public string XdebugMode { get => _xdebugMode; set => SetProperty(ref _xdebugMode, value); }
    public string XdebugPort { get => _xdebugPort; set => SetProperty(ref _xdebugPort, value); }
    public string XdebugStartWithRequest { get => _xdebugStartWithRequest; set => SetProperty(ref _xdebugStartWithRequest, value); }
    public string XdebugStatus { get => _xdebugStatus; private set => SetProperty(ref _xdebugStatus, value); }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand CreateProjectCommand { get; }
    public RelayCommand BrowseImportCommand { get; }
    public RelayCommand DetectImportCommand { get; }
    public AsyncRelayCommand ImportProjectCommand { get; }
    public AsyncRelayCommand CheckHealthCommand { get; }
    public AsyncRelayCommand RepairCommand { get; }
    public AsyncRelayCommand RunCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand OpenSiteCommand { get; }
    public RelayCommand RefreshXdebugCommand { get; }
    public RelayCommand ApplyXdebugCommand { get; }
    public RelayCommand AddMailpitCommand { get; }
    public RelayCommand AddRedisCommand { get; }
    public AsyncRelayCommand StartManagedServiceCommand { get; }
    public AsyncRelayCommand StopManagedServiceCommand { get; }
    public AsyncRelayCommand RestartManagedServiceCommand { get; }
    public RelayCommand ToggleManagedServiceCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            Status = "Refreshing projects...";
            Profiles.Clear();
            foreach (var profile in _profiles.GetProfiles()) Profiles.Add(profile);
            SelectedProfile ??= Profiles.FirstOrDefault();

            var selectedName = SelectedProject?.Name;
            Projects.Clear();
            foreach (var site in _sites.GetSites())
            {
                var projectRoot = _workspace.ResolveProjectRoot(site.DocumentRoot);
                var kind = Directory.Exists(projectRoot) ? _workspace.Detect(projectRoot).Kind : ProjectKind.Unknown;
                Projects.Add(new ProjectSiteRow(site, projectRoot, kind));
            }
            SelectedProject = Projects.FirstOrDefault(project => project.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase)) ?? Projects.FirstOrDefault();
            RefreshXdebug();
            RefreshManagedServices();
            Status = "Ready";
            await Task.CompletedTask;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "Refresh failed";
            _dialogs.Error("Project Manager", ex.Message);
        }
    }

    private async Task CreateProjectAsync()
    {
        try
        {
            if (SelectedProfile is null) return;
            Status = "Creating project...";
            var request = _profiles.CreateRequest(SelectedProfile.Key, ProjectName.Trim(), string.IsNullOrWhiteSpace(ProjectDomain) ? null : ProjectDomain.Trim());
            var result = await _provisioning.ProvisionAsync(request);
            if (!await _hosts.EnsureAsync(result.Site.Domain))
                _dialogs.Warning("Project created", $"Project was created, but the hosts mapping for {result.Site.Domain} could not be confirmed.");
            await RefreshAsync();
            SelectedProject = Projects.FirstOrDefault(project => project.Name.Equals(result.Site.Name, StringComparison.OrdinalIgnoreCase));
            _dialogs.Info("Project created", string.Join(Environment.NewLine, result.Actions.Concat(result.Warnings.Select(value => "Warning: " + value))));
        }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Create failed"; _dialogs.Error("Project creation failed", ex.Message); }
    }

    private void BrowseImport()
    {
        var selected = _files.SelectFolder("Select an existing project directory", ImportPath);
        if (selected is null) return;
        ImportPath = selected;
        if (string.IsNullOrWhiteSpace(ImportName)) ImportName = new DirectoryInfo(selected).Name.ToLowerInvariant().Replace(' ', '-');
        DetectImport();
    }

    private void DetectImport()
    {
        try
        {
            var result = _workspace.Detect(ImportPath);
            DetectionSummary = $"{result.Kind} · {string.Join(" ", result.Evidence)}" +
                (result.RequiredPhpExtensions.Count == 0 ? string.Empty : $" Required PHP extensions: {string.Join(", ", result.RequiredPhpExtensions)}.");
        }
        catch (Exception ex) when (IsExpected(ex)) { DetectionSummary = ex.Message; }
    }

    private async Task ImportProjectAsync()
    {
        try
        {
            Status = "Importing project...";
            var site = _workspace.Import(new ProjectImportRequest(ImportPath, ImportName.Trim(), CopyIntoDevBox: CopyImport));
            if (!await _hosts.EnsureAsync(site.Domain))
                _dialogs.Warning("Project imported", $"Project was imported, but the hosts mapping for {site.Domain} could not be confirmed.");
            await RefreshAsync();
            SelectedProject = Projects.FirstOrDefault(project => project.Name.Equals(site.Name, StringComparison.OrdinalIgnoreCase));
            _dialogs.Info("Project imported", $"{site.Name} is registered as {site.Domain}.");
        }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Import failed"; _dialogs.Error("Project import failed", ex.Message); }
    }

    private async Task CheckHealthAsync()
    {
        if (SelectedProject is null) return;
        try
        {
            Status = "Checking project health...";
            var report = await _workspace.CheckHealthAsync(SelectedProject.Site);
            HealthChecks.Clear();
            foreach (var check in report.Checks) HealthChecks.Add(check);
            HealthSummary = $"{report.Kind}: {report.Checks.Count(check => check.State == ProjectHealthState.Error)} error(s), {report.Checks.Count(check => check.State == ProjectHealthState.Warning)} warning(s).";
            Status = "Ready";
        }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Health check failed"; _dialogs.Error("Project health check failed", ex.Message); }
    }

    private async Task RepairAsync()
    {
        if (SelectedProject is null) return;
        try
        {
            Status = "Repairing project...";
            var result = await _workspace.RepairAsync(SelectedProject.Site);
            await CheckHealthAsync();
            var message = result.Repaired.Count == 0 ? "No automatic repairs were required." : string.Join(Environment.NewLine, result.Repaired);
            if (result.RemainingProblems.Count > 0)
                message += Environment.NewLine + Environment.NewLine + "Remaining:" + Environment.NewLine + string.Join(Environment.NewLine, result.RemainingProblems);
            _dialogs.Info("Project repair", message);
        }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Repair failed"; _dialogs.Error("Project repair failed", ex.Message); }
    }

    private void LoadProjectCommands()
    {
        ProjectCommands.Clear();
        SelectedCommand = null;
        if (SelectedProject is null || !Directory.Exists(SelectedProject.ProjectRoot)) return;
        try
        {
            foreach (var preset in _commands.GetPresets(SelectedProject.ProjectRoot)) ProjectCommands.Add(preset);
            SelectedCommand = ProjectCommands.FirstOrDefault();
        }
        catch (Exception ex) when (IsExpected(ex)) { CommandOutput = ex.Message; }
    }

    private async Task RunProjectCommandAsync()
    {
        if (SelectedProject is null || SelectedCommand is null) return;
        try
        {
            Status = $"Running {SelectedCommand.DisplayName}...";
            var result = await _commands.RunAsync(SelectedProject.ProjectRoot, SelectedCommand.Key);
            CommandOutput = string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
            Status = result.Success ? $"Completed in {result.Duration.TotalSeconds:F1}s" : $"Command exited with code {result.ExitCode}";
        }
        catch (Exception ex) when (IsExpected(ex)) { Status = "Command failed"; CommandOutput = ex.Message; }
    }

    private void OpenProject() { if (SelectedProject is not null) _shell.Open(SelectedProject.ProjectRoot); }
    private void OpenSite() { if (SelectedProject is not null) _shell.Open($"{(SelectedProject.Site.HttpsEnabled ? "https" : "http")}://{SelectedProject.Domain}"); }

    private void RefreshXdebug()
    {
        try
        {
            var status = _xdebug.GetStatus();
            XdebugEnabled = status.Enabled;
            XdebugMode = status.Mode;
            XdebugPort = status.ClientPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            XdebugStartWithRequest = status.StartWithRequest;
            XdebugStatus = status.BinaryAvailable ? status.Enabled ? "Xdebug is enabled" : "Xdebug binary available, disabled" : "php_xdebug.dll is not installed in the active PHP runtime";
        }
        catch (Exception ex) when (IsExpected(ex)) { XdebugStatus = ex.Message; }
    }

    private void ApplyXdebug()
    {
        try
        {
            if (!int.TryParse(XdebugPort, out var port)) throw new ArgumentException("Xdebug port must be a number.");
            var status = _xdebug.Configure(new XdebugConfiguration(XdebugEnabled, XdebugMode, port, XdebugStartWithRequest));
            XdebugStatus = status.Enabled ? "Xdebug configuration applied and enabled. Restart PHP to reload it." : "Xdebug configuration applied and disabled. Restart PHP to reload it.";
        }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Xdebug configuration failed", ex.Message); }
    }

    private void AddManagedService(ManagedServiceManifest template)
    {
        try
        {
            var definition = _managedServices.GetDefinition(template);
            _managedServices.Upsert(template with { Enabled = File.Exists(definition.ExecutablePath) });
            RefreshManagedServices();
            if (!File.Exists(definition.ExecutablePath))
                _dialogs.Warning("Managed service", $"{template.DisplayName} was registered disabled because its runtime is not installed. Install it from Tools first.");
        }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Managed service", ex.Message); }
    }

    private void RefreshManagedServices()
    {
        var selectedKey = SelectedManagedService?.Key;
        ManagedServices.Clear();
        foreach (var manifest in _managedServices.GetManifests())
        {
            var definition = _managedServices.GetDefinition(manifest);
            ManagedServices.Add(new ManagedServiceRow(manifest, definition, _processManager.GetStatus(definition)));
        }
        SelectedManagedService = ManagedServices.FirstOrDefault(item => item.Key.Equals(selectedKey, StringComparison.OrdinalIgnoreCase)) ?? ManagedServices.FirstOrDefault();
    }

    private async Task StartManagedServiceAsync()
    {
        if (SelectedManagedService is null) return;
        try { await _processManager.StartAsync(SelectedManagedService.Definition); RefreshManagedServices(); }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Start service failed", ex.Message); }
    }

    private async Task StopManagedServiceAsync()
    {
        if (SelectedManagedService is null) return;
        try { await _processManager.StopAsync(SelectedManagedService.Definition); RefreshManagedServices(); }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Stop service failed", ex.Message); }
    }

    private async Task RestartManagedServiceAsync()
    {
        if (SelectedManagedService is null) return;
        try { await _processManager.RestartAsync(SelectedManagedService.Definition); RefreshManagedServices(); }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Restart service failed", ex.Message); }
    }

    private void ToggleManagedService()
    {
        if (SelectedManagedService is null) return;
        try
        {
            var manifest = SelectedManagedService.Manifest;
            if (!manifest.Enabled && !File.Exists(SelectedManagedService.Definition.ExecutablePath))
                throw new FileNotFoundException("The service runtime must be installed before it can be enabled.", SelectedManagedService.Definition.ExecutablePath);
            _managedServices.Upsert(manifest with { Enabled = !manifest.Enabled });
            RefreshManagedServices();
        }
        catch (Exception ex) when (IsExpected(ex)) { _dialogs.Error("Managed service", ex.Message); }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or HttpRequestException or Win32Exception or ArgumentException or KeyNotFoundException or TimeoutException;
}
