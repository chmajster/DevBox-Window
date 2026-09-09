using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class PhpWindow : Window
{
    public PhpWindow(PhpWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
