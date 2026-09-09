using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class PhpWindow : Window
{
    private readonly IFeatureWindowService _featureWindows;

    public PhpWindow(PhpWindowViewModel viewModel, IFeatureWindowService featureWindows)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _featureWindows = featureWindows ?? throw new ArgumentNullException(nameof(featureWindows));
    }

    private void SitePhp_Click(object sender, RoutedEventArgs e) => _featureWindows.ShowSitePhp(this);
}