using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Abstractions;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class ProjectManagerWindow : Window
{
    public ProjectManagerWindow(
        IProcessManager processManager,
        IHostMappingService hosts,
        IFileDialogService files,
        IDialogService dialogs,
        IShellService shell)
    {
        InitializeComponent();

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
            new ProjectCommandService(root),
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
}
