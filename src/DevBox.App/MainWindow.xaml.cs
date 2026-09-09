using System.Windows;
using System.Windows.Controls;
using DevBox.App.Services;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly IFeatureWindowService _featureWindows;

    public MainWindow(MainWindowViewModel viewModel, IFeatureWindowService featureWindows)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _featureWindows = featureWindows ?? throw new ArgumentNullException(nameof(featureWindows));
        DataContext = _viewModel;
        AddFeatureNavigation();
    }

    private void AddFeatureNavigation()
    {
        if (Content is not Grid root)
        {
            return;
        }

        var sidebar = root.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetColumn(border) == 0);
        if (sidebar?.Child is not DockPanel dock)
        {
            return;
        }

        var navigation = dock.Children
            .OfType<StackPanel>()
            .FirstOrDefault(panel => DockPanel.GetDock(panel) == Dock.Top);
        if (navigation is null)
        {
            return;
        }

        navigation.Children.Insert(3, CreateFeatureButton("PHP", PhpNav_Click));
        navigation.Children.Insert(4, CreateFeatureButton("Databases", DatabasesNav_Click));
        navigation.Children.Insert(5, CreateFeatureButton("SSL", SslNav_Click));
        navigation.Children.Add(CreateFeatureButton("Setup", SetupNav_Click));
    }

    private Button CreateFeatureButton(string content, RoutedEventHandler clickHandler)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("SidebarButton")
        };
        button.Click += clickHandler;
        return button;
    }

    private void PhpNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowPhp(this);
    private void DatabasesNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowDatabases(this);
    private void SslNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSsl(this);
    private void SetupNav_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSetup(this);

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
