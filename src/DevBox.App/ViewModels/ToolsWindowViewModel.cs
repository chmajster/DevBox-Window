using System.Collections.ObjectModel;
using System.ComponentModel;
using DevBox.App.Services;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.ViewModels;

public sealed class ToolRowViewModel(DeveloperToolStatus status)
{
    public string Key { get; } = status.Key;
    public string Name { get; } = status.DisplayName;
    public string Status { get; } = status.Installed ? "Installed" : "Missing";
    public string Version { get; } = status.Version ?? "—";
    public string ExecutablePath { get; } = status.ExecutablePath ?? "—";
    public string InstallMethod { get; } = status.InstallMethod;
}

public sealed class ToolsWindowViewModel : ObservableObject
{
    private readonly DeveloperToolsService _tools;
    private readonly IDialogService _dialogs;
    private string _status = "Ready";

    public ToolsWindowViewModel(DeveloperToolsService tools, IDialogService dialogs)
    {
        _tools = tools;
        _dialogs = dialogs;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        InstallComposerCommand = new AsyncRelayCommand(InstallComposerAsync);
        InstallNodeCommand = new AsyncRelayCommand(InstallNodeAsync);
        InstallPnpmCommand = new AsyncRelayCommand(InstallPnpmAsync);
    }

    public ObservableCollection<ToolRowViewModel> Tools { get; } = new();
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand InstallComposerCommand { get; }
    public AsyncRelayCommand InstallNodeCommand { get; }
    public AsyncRelayCommand InstallPnpmCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            Status = "Checking tools...";
            var statuses = await _tools.GetStatusesAsync();
            Tools.Clear();
            foreach (var tool in statuses)
            {
                Tools.Add(new ToolRowViewModel(tool));
            }
            Status = "Ready";
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "Check failed";
            _dialogs.Error("Tools check failed", ex.Message);
        }
    }

    private async Task InstallComposerAsync()
    {
        await InstallAsync("Composer", _tools.InstallComposerAsync);
    }

    private async Task InstallNodeAsync()
    {
        await InstallAsync("Node.js LTS", _tools.InstallNodeLtsAsync);
    }

    private async Task InstallPnpmAsync()
    {
        await InstallAsync("pnpm", _tools.InstallPnpmAsync);
    }

    private async Task InstallAsync(string name, Func<CancellationToken, Task> operation)
    {
        try
        {
            Status = $"Installing {name}...";
            await operation(CancellationToken.None);
            await RefreshAsync();
            _dialogs.Info("Tool installed", $"{name} installation completed.");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            Status = "Installation failed";
            _dialogs.Error($"{name} installation failed", ex.Message);
        }
    }

    private static bool IsExpected(Exception ex) =>
        ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or HttpRequestException or Win32Exception;
}
