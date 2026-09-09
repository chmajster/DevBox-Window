using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class ToolsWindow : Window
{
    public ToolsWindow(ToolsWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }
}
