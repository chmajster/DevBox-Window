using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
        AddEnvironmentCenterNavigation();
    }

    private void PhpNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowPhp(this);
    private void DatabasesNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowDatabases(this);
    private void SslNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSsl(this);
    private void SetupNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSetup(this);
    private void ToolsNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowTools(this);
    private void EnvironmentCenterNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowEnvironmentCenter(this);
    private void UpdatesNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowUpdates(this);
    private void SettingsNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSettings(this);

    private void AddEnvironmentCenterNavigation()
    {
        var tools = FindButton(this, "Tools");
        if (tools?.Parent is not StackPanel panel)
            return;
        var button = new Button
        {
            Content = "Environment Center",
            Style = (Style)FindResource("SidebarButton")
        };
        button.Click += EnvironmentCenterNav_Click;
        var index = panel.Children.IndexOf(tools);
        panel.Children.Insert(index < 0 ? panel.Children.Count : index + 1, button);
    }

    private static Button? FindButton(DependencyObject parent, string content)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is Button button && string.Equals(button.Content?.ToString(), content, StringComparison.Ordinal))
                return button;
            if (child is DependencyObject dependency)
            {
                var nested = FindButton(dependency, content);
                if (nested is not null)
                    return nested;
            }
        }
        return null;
    }

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

        if (!App.IsExiting)
            App.RequestExit();
    }
}
