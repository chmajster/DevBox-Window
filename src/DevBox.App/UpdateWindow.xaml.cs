using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class UpdateWindow : Window
{
    public UpdateWindow(UpdateWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += async (_, _) => await viewModel.CheckAsync();
    }
}
