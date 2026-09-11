using System.Drawing;
using System.Windows;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.App.Services;

public interface ITrayService : IDisposable
{
    void Initialize(Window mainWindow);
    Task StartConfiguredServicesAsync();
}

public sealed class TrayService : ITrayService
{
    private readonly ServiceCatalog _catalog;
    private readonly IProcessManager _processManager;
    private readonly IAppSettingsService _settings;
    private readonly IDialogService _dialogs;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private Window? _mainWindow;
    private bool _disposed;

    public TrayService(
        ServiceCatalog catalog,
        IProcessManager processManager,
        IAppSettingsService settings,
        IDialogService dialogs)
    {
        _catalog = catalog;
        _processManager = processManager;
        _settings = settings;
        _dialogs = dialogs;
    }

    public void Initialize(Window mainWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        if (_notifyIcon is not null)
            return;

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open DevBox", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Start All", null, async (_, _) => await RunAllFromTrayAsync(ServiceAction.Start));
        menu.Items.Add("Restart All", null, async (_, _) => await RunAllFromTrayAsync(ServiceAction.Restart));
        menu.Items.Add("Stop All", null, async (_, _) => await RunAllFromTrayAsync(ServiceAction.Stop));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.RequestExit());

        var executable = Environment.ProcessPath;
        Icon? icon = null;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
            icon = Icon.ExtractAssociatedIcon(executable);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "DevBox Windows",
            Icon = icon ?? SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    public async Task StartConfiguredServicesAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_settings.Current.StartServicesAutomatically)
            await RunAllAsync(ServiceAction.Start).ConfigureAwait(false);
    }

    private async Task RunAllFromTrayAsync(ServiceAction action)
    {
        try
        {
            await RunAllAsync(action);
        }
        catch (Exception ex)
        {
            ShowError("Service operation failed", ex.Message);
        }
    }

    private async Task RunAllAsync(ServiceAction action)
    {
        IReadOnlyList<ServiceDefinition> definitions;
        try
        {
            definitions = _catalog.GetDefaultServices();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            ShowError("Service configuration invalid", ex.Message);
            return;
        }

        var errors = new List<string>();
        var missing = new List<string>();
        foreach (var definition in definitions)
        {
            if (action != ServiceAction.Stop && !File.Exists(definition.ExecutablePath))
            {
                missing.Add(definition.DisplayName);
                continue;
            }

            try
            {
                _ = action switch
                {
                    ServiceAction.Start => await _processManager.StartAsync(definition).ConfigureAwait(false),
                    ServiceAction.Stop => await _processManager.StopAsync(definition).ConfigureAwait(false),
                    ServiceAction.Restart => await _processManager.RestartAsync(definition).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                };
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                errors.Add($"{definition.DisplayName}: {ex.Message}");
            }
        }

        if (missing.Count > 0)
            errors.Add($"Missing runtime: {string.Join(", ", missing)}");
        if (errors.Count > 0)
            ShowError("Service operation failed", string.Join(Environment.NewLine, errors));
    }

    private void ShowError(string title, string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.Invoke(() => _dialogs.Error(title, message));
    }

    private void ShowMainWindow()
    {
        var window = _mainWindow;
        if (window is null)
            return;
        window.Dispatcher.Invoke(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }

    private enum ServiceAction
    {
        Start,
        Stop,
        Restart
    }
}
