using System.Windows;
using DevBox.App.ViewModels;

namespace DevBox.App;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow(FirstRunViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Loaded += (_, _) => SelectInstallable();
    }

    private FirstRunViewModel ViewModel => (FirstRunViewModel)DataContext;

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void SelectInstallable_Click(object sender, RoutedEventArgs e) => SelectInstallable();

    private void SelectInstallable()
    {
        ComponentsList.SelectedItems.Clear();
        foreach (var item in ViewModel.Items.Where(item => !item.Ready && item.CanInstallAutomatically))
            ComponentsList.SelectedItems.Add(item);

        var count = ComponentsList.SelectedItems.Count;
        ProgressText.Text = count == 0
            ? "No missing components can be installed automatically."
            : $"{count} component{(count == 1 ? string.Empty : "s")} selected.";
        InstallProgress.Value = 0;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ComponentsList.SelectedItems.Clear();
        ProgressText.Text = "Select components to install.";
        InstallProgress.Value = 0;
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = ComponentsList.SelectedItems
            .OfType<SetupItemViewModel>()
            .Where(item => !item.Ready && item.CanInstallAutomatically)
            .ToArray();

        if (selected.Length == 0)
        {
            ProgressText.Text = "No installable components are selected.";
            InstallProgress.Value = 0;
            return;
        }

        ComponentsList.IsEnabled = false;
        InstallProgress.Minimum = 0;
        InstallProgress.Maximum = selected.Length;
        InstallProgress.Value = 0;

        try
        {
            for (var index = 0; index < selected.Length; index++)
            {
                var item = selected[index];
                ProgressText.Text = $"Installing {item.Name} · {index + 1} of {selected.Length}";
                await ViewModel.InstallCommand.ExecuteAsync(item);
                InstallProgress.Value = index + 1;
            }

            ViewModel.RefreshCommand.Execute(null);
            ProgressText.Text = ViewModel.IsReady
                ? "Setup complete. DevBox is ready."
                : "Selected components processed. Review any remaining manual requirements.";
        }
        finally
        {
            ComponentsList.IsEnabled = true;
        }
    }
}