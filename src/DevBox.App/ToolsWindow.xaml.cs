using System.ComponentModel;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class ToolsWindow : Window
{
    private readonly IFeatureWindowService _featureWindows;
    private readonly IDialogService _dialogs;
    private readonly IFileDialogService _files;

    public ToolsWindow(
        ToolsWindowViewModel viewModel,
        IFeatureWindowService featureWindows,
        IDialogService dialogs,
        IFileDialogService files)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _featureWindows = featureWindows ?? throw new ArgumentNullException(nameof(featureWindows));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private void ProjectManager_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowProjects(this);

    private async void InstallMailpit_Click(object sender, RoutedEventArgs e) => await InstallOptionalRuntimeAsync("mailpit", "Mailpit");

    private async void InstallRedis_Click(object sender, RoutedEventArgs e) => await InstallOptionalRuntimeAsync("redis", "Garnet Redis-compatible server");

    private void InstallXdebug_Click(object sender, RoutedEventArgs e)
    {
        var source = _files.OpenXdebugDll();
        if (source is null) return;

        try
        {
            var result = new XdebugBinaryInstaller(App.DevBoxRoot).InstallFromFile(source);
            _dialogs.Info(
                "Xdebug installed",
                $"Xdebug DLL was installed as php_xdebug.dll. SHA-256: {result.Sha256}{Environment.NewLine}Enable and configure it in Project Manager, then restart PHP.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _dialogs.Error("Xdebug installation failed", ex.Message);
        }
    }

    private async Task InstallOptionalRuntimeAsync(string key, string displayName)
    {
        try
        {
            using var runtimeManager = new RuntimeManager(App.DevBoxRoot);
            var services = new ManagedServiceCatalog(App.DevBoxRoot);
            var installer = new OptionalRuntimeInstaller(runtimeManager, new OptionalRuntimeCatalog(), services);
            var manifest = await installer.InstallAsync(key);
            _dialogs.Info("Runtime installed", $"{displayName} {manifest.Version} was installed, activated and registered as a managed DevBox service.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or PlatformNotSupportedException or Win32Exception)
        {
            _dialogs.Error($"{displayName} installation failed", ex.Message);
        }
    }
}
