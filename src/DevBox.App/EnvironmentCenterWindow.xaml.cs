using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using DevBox.App.ViewModels;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class EnvironmentCenterWindow : Window
{
    private readonly EnvironmentCenterViewModel _viewModel;

    public EnvironmentCenterWindow(EnvironmentCenterViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = _viewModel;
        AddBackupBar();
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void AddBackupBar()
    {
        if (Content is not Grid rootGrid)
            return;

        rootGrid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
        foreach (UIElement child in rootGrid.Children.Cast<UIElement>().ToArray())
            Grid.SetRow(child, Grid.GetRow(child) + 1);

        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 10)
        };
        bar.Children.Add(CreateBackupButton("Backup configuration", false));
        bar.Children.Add(CreateBackupButton("Backup + projects", true));
        Grid.SetRow(bar, 0);
        rootGrid.Children.Add(bar);
    }

    private Button CreateBackupButton(string text, bool includeProjects)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = includeProjects
                ? "Create an environment backup including project source trees."
                : "Create a configuration backup without project source trees."
        };
        button.Click += async (_, _) => await CreateBackupAsync(button, includeProjects);
        return button;
    }

    private async Task CreateBackupAsync(Button button, bool includeProjects)
    {
        if (includeProjects)
        {
            var confirmed = MessageBox.Show(
                "Include all project files under DevBox www? The archive may be large.",
                "Environment backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
                return;
        }

        button.IsEnabled = false;
        var originalText = button.Content;
        button.Content = "Creating backup...";
        try
        {
            var result = await Task.Run(() => new EnvironmentBackupService(App.DevBoxRoot).Create(includeProjects));
            MessageBox.Show(
                $"Backup created successfully.\n\n{result.ArchivePath}\n\nFiles: {result.FileCount}\nProjects: {result.ProjectCount}\nArchive: {FormatBytes(result.ArchiveBytes)}",
                "Environment backup",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "Environment backup failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            button.Content = originalText;
            button.IsEnabled = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.00} GiB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):0.00} MiB";
        if (bytes >= 1024)
            return $"{bytes / 1024d:0.00} KiB";
        return $"{bytes} B";
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
