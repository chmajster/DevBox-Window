using System.ComponentModel;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Abstractions;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class ProjectManagerWindow : Window
{
    private readonly DeveloperToolLauncher _developerTools = new();
    private readonly IDialogService _dialogs;

    public ProjectManagerWindow(
        IProcessManager processManager,
        IHostMappingService hosts,
        IFileDialogService files,
        IDialogService dialogs,
        IShellService shell)
    {
        InitializeComponent();
        _dialogs = dialogs;

        var root = App.DevBoxRoot;
        var sites = new SiteManager(root);
        var extensions = new PhpExtensionInspector(root);
        var certificates = new LocalCertificateManager(root);
        var workspace = new ProjectWorkspaceService(root, sites, extensions, certificates);
        var managedServices = new ManagedServiceCatalog(root);
        var provisioning = new ProjectProvisioningService(
            root,
            workspace,
            new DatabaseManager(root),
            managedServices);

        var viewModel = new ProjectManagerWindowViewModel(
            workspace,
            provisioning,
            new ProjectStackProfileService(root),
            new ProjectCommandService(root, workspace),
            new XdebugConfigurationService(root),
            managedServices,
            sites,
            processManager,
            hosts,
            files,
            dialogs,
            shell);

        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private ProjectSiteRow? SelectedProject =>
        (DataContext as ProjectManagerWindowViewModel)?.SelectedProject;

    private void OpenVsCode_Click(object sender, RoutedEventArgs e) =>
        LaunchSelectedProject("VS Code", _developerTools.OpenVsCode);

    private void OpenPhpStorm_Click(object sender, RoutedEventArgs e) =>
        LaunchSelectedProject("PhpStorm", _developerTools.OpenPhpStorm);

    private void OpenTerminal_Click(object sender, RoutedEventArgs e) =>
        LaunchSelectedProject("terminal", _developerTools.OpenTerminal);

    private void LaunchSelectedProject(string toolName, Action<string> launcher)
    {
        var project = SelectedProject;
        if (project is null)
        {
            _dialogs.Warning("Project required", "Select a project first.");
            return;
        }

        try
        {
            launcher(project.ProjectRoot);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or DirectoryNotFoundException)
        {
            _dialogs.Error($"Unable to open {toolName}", ex.Message);
        }
    }
}
