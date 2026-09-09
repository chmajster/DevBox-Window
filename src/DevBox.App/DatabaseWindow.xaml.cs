using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class DatabaseWindow : Window
{
    private readonly DatabaseWindowViewModel _viewModel;

    public DatabaseWindow(DatabaseWindowViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.RefreshAsync();
    }

    private void PasswordInput_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        _viewModel.Password = PasswordInput.Password;
    }
}
