using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class SitePhpWindow : Window
{
    public SitePhpWindow(SitePhpWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
