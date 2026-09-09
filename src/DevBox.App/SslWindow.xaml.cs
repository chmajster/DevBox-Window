using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class SslWindow : Window
{
    public SslWindow(SslWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
