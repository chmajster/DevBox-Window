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
        {
            return;
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open DevBox", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Start All", null, async (_, _) => await RunAllAsync(ServiceAction.Start));
        menu.Items.Add("Restart All", null, async (_, _) => await RunAllAsync(ServiceAction.Restart));
        menu.Items.Add("Stop All", null, async (_, _) => await RunAllAsync(ServiceAction.Stop));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.RequestExit());

        var executable = Environment.ProcessPath;
        Icon? icon = null;
        if (!string.IsNullOrWhiteSpace(executable) && File.Exists(executable))
        {
            icon = Icon.ExtractAssociatedIcon(executable);
        }

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
        {
            await RunAllAsync(ServiceAction.Start).ConfigureAwait(false);
        }
    }

    private async Task RunAllAsync(ServiceAction action)
    {
        try
        {
            var definitions = _catalog.GetDefaultServices();
            var ordered = action == ServiceAction.Stop ? definitions.Reverse() : definitions;
            foreach (var definition in ordered)
            {
                if (action != ServiceAction.Stop && !File.Exists(definition.ExecutablePath))
                {
                    continue;
                }

                _ = action switch
                {
                    ServiceAction.Start => await _processManager.StartAsync(definition).ConfigureAwait(false),
                    ServiceAction.Stop => await _processManager.StopAsync(definition).ConfigureAwait(false),
                    ServiceAction.Restart => await _processManager.RestartAsync(definition).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(action))
                };
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => _dialogs.Error("Service operation failed", ex.Message));
        }
    }

    private void ShowMainWindow()
    {
        var window = _mainWindow;
        if (window is null)
        {
            return;
        }
        window.Dispatcher.Invoke(() =>
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
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
