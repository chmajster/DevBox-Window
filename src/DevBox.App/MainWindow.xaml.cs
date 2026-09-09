using System.ComponentModel;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IFeatureWindowService _featureWindows;
    private readonly IAppSettingsService _settings;

    public MainWindow(
        MainWindowViewModel viewModel,
        IFeatureWindowService featureWindows,
        IAppSettingsService settings)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _featureWindows = featureWindows ?? throw new ArgumentNullException(nameof(featureWindows));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        DataContext = _viewModel;
    }

    private void PhpNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowPhp(this);
    private void DatabasesNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowDatabases(this);
    private void SslNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSsl(this);
    private void SetupNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSetup(this);
    private void ToolsNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowTools(this);
    private void UpdatesNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowUpdates(this);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSettings(this);

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}