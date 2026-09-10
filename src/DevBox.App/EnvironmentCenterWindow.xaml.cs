using System.Windows;
using System.Windows.Controls;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class EnvironmentCenterWindow : Window
{
    private readonly EnvironmentCenterViewModel _viewModel;

    public EnvironmentCenterWindow(EnvironmentCenterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void SecretPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox password)
            _viewModel.SecretValue = password.Password;
    }

    private void WordPressPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox password)
            _viewModel.WordPressAdminPassword = password.Password;
    }
}
