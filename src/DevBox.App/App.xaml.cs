using System.IO;
using System.Windows;
using DevBox.Core.Abstractions;
using DevBox.Core.Services;

namespace DevBox.App;

public partial class App : Application
{
    public static string DevBoxRoot { get; private set; } = string.Empty;
    public static ServiceCatalog ServiceCatalog { get; private set; } = null!;
    public static IProcessManager ProcessManager { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var privilegedExitCode = TryRunPrivilegedCommand(e.Args);
        if (privilegedExitCode is not null)
        {
            Shutdown(privilegedExitCode.Value);
            return;
        }

        DevBoxRoot = ResolveRoot();
        RuntimeLayout.EnsureInitialized(DevBoxRoot);
        ServiceCatalog = new ServiceCatalog(DevBoxRoot);
        ProcessManager = new ProcessManager();

        var window = new DevBox.App.MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProcessManager?.Dispose();
        base.OnExit(e);
    }

    private static int? TryRunPrivilegedCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 3 && args[0].Equals("--hosts-ensure", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteHostsCommand(() =>
            {
                ValidateTestDomain(args[1]);
                new HostsFileManager().EnsureMapping(args[2], args[1]);
            });
        }

        if (args.Count == 2 && args[0].Equals("--hosts-remove", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteHostsCommand(() =>
            {
                ValidateTestDomain(args[1]);
                new HostsFileManager().RemoveMapping(args[1]);
            });
        }

        return null;
    }

    private static int ExecuteHostsCommand(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "DevBox hosts update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static void ValidateTestDomain(string domain)
    {
        if (!domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Elevated hosts operations are limited to .test domains.", nameof(domain));
        }
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DEVBOX_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
