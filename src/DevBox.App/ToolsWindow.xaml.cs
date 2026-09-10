using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class ToolsWindow : Window
{
    private readonly IFeatureWindowService _featureWindows;

    public ToolsWindow(ToolsWindowViewModel viewModel, IFeatureWindowService featureWindows)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _featureWindows = featureWindows ?? throw new ArgumentNullException(nameof(featureWindows));
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    private void ProjectManager_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowProjects(this);
}
