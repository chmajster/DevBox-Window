using System.IO;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Abstractions;
using DevBox.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevBox.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    public static string DevBoxRoot { get; private set; } = string.Empty;

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

        var services = new ServiceCollection();
        services.AddSingleton(new ServiceCatalog(DevBoxRoot));
        services.AddSingleton<IProcessManager, ProcessManager>();
        services.AddSingleton(_ => new AddonCatalog(DevBoxRoot));
        services.AddSingleton(_ => new AddonInstaller(DevBoxRoot));
        services.AddSingleton(_ => new HostsFileManager());
        services.AddSingleton(_ => new PhpExtensionInspector(DevBoxRoot));
        services.AddSingleton(_ => new PhpManager(DevBoxRoot));
        services.AddSingleton(_ => new DatabaseManager(DevBoxRoot));
        services.AddSingleton(_ => new LocalCertificateManager(DevBoxRoot));
        services.AddSingleton<IRuntimeManager>(_ => new RuntimeManager(DevBoxRoot));
        services.AddSingleton(new RuntimeCatalog());
        services.AddSingleton(provider => new EnvironmentReadinessService(
            DevBoxRoot,
            provider.GetRequiredService<RuntimeCatalog>()));
        services.AddSingleton(_ => new SiteManager(DevBoxRoot));
        services.AddSingleton(_ => new LogReader(DevBoxRoot));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IHostMappingService, HostMappingService>();
        services.AddSingleton(provider => new DiagnosticsService(
            DevBoxRoot,
            provider.GetRequiredService<ServiceCatalog>(),
            provider.GetRequiredService<IProcessManager>()));

        services.AddTransient<PhpWindowViewModel>();
        services.AddTransient<DatabaseWindowViewModel>();
        services.AddTransient<SslWindowViewModel>();
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<PhpWindow>();
        services.AddTransient<DatabaseWindow>();
        services.AddTransient<SslWindow>();
        services.AddTransient<FirstRunWindow>();
        services.AddSingleton<IFeatureWindowService, FeatureWindowService>();

        services.AddSingleton(provider => new MainWindowViewModel(
            DevBoxRoot,
            provider.GetRequiredService<ServiceCatalog>(),
            provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<AddonCatalog>(),
            provider.GetRequiredService<AddonInstaller>(),
            provider.GetRequiredService<PhpExtensionInspector>(),
            provider.GetRequiredService<IHostMappingService>(),
            provider.GetRequiredService<SiteManager>(),
            provider.GetRequiredService<IRuntimeManager>(),
            provider.GetRequiredService<DiagnosticsService>(),
            provider.GetRequiredService<LogReader>(),
            provider.GetRequiredService<IDialogService>(),
            provider.GetRequiredService<IShellService>()));
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var readiness = _serviceProvider.GetRequiredService<EnvironmentReadinessService>().Check();
        if (readiness.Items.Any(item => !item.Ready && item.CanInstallAutomatically))
        {
            _serviceProvider.GetRequiredService<FirstRunWindow>().ShowDialog();
        }

        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
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
