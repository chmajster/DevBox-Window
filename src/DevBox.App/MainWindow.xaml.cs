using System.Windows;
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
