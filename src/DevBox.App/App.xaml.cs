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

        DevBoxRoot = ResolveRoot();
        RuntimeLayout.EnsureInitialized(DevBoxRoot);
        ServiceCatalog = new ServiceCatalog(DevBoxRoot);
        ProcessManager = new ProcessManager();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ProcessManager?.Dispose();
        base.OnExit(e);
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
