using System.IO;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Abstractions;
using DevBox.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevBox.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    public static string DevBoxRoot { get; private set; } = string.Empty;
    internal static bool IsExiting { get; private set; }

    internal static void RequestExit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

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
        services.AddSingleton(_ => new PhpRuntimePoolManager(DevBoxRoot));
        services.AddSingleton(_ => new DatabaseManager(DevBoxRoot));
        services.AddSingleton(_ => new LocalCertificateManager(DevBoxRoot));
        services.AddSingleton(_ => new DeveloperToolsService(DevBoxRoot));
        services.AddSingleton(_ => new ApplicationUpdateService(typeof(App).Assembly.GetName().Version ?? new Version(0, 2, 0)));
        services.AddSingleton<IRuntimeManager>(_ => new RuntimeManager(DevBoxRoot));
        services.AddSingleton(_ => new RuntimePlatformService(DevBoxRoot));
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
        services.AddSingleton<IAppSettingsService>(_ => new AppSettingsService(DevBoxRoot));
        services.AddSingleton<ITrayService, TrayService>();
        services.AddSingleton(provider => new DiagnosticsService(
            DevBoxRoot,
            provider.GetRequiredService<ServiceCatalog>(),
            provider.GetRequiredService<IProcessManager>()));

        services.AddTransient<PhpWindowViewModel>();
        services.AddTransient<SitePhpWindowViewModel>();
        services.AddTransient<DatabaseWindowViewModel>();
        services.AddTransient<SslWindowViewModel>();
        services.AddTransient<FirstRunViewModel>();
        services.AddTransient<ToolsWindowViewModel>();
        services.AddTransient<UpdateWindowViewModel>();
        services.AddTransient<SettingsWindowViewModel>();
        services.AddTransient<PhpWindow>();
        services.AddTransient<SitePhpWindow>();
        services.AddTransient<DatabaseWindow>();
        services.AddTransient<SslWindow>();
        services.AddTransient<FirstRunWindow>();
        services.AddTransient<ToolsWindow>();
        services.AddTransient<UpdateWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<IFeatureWindowService, FeatureWindowService>();

        services.AddSingleton(provider => new MainWindowViewModel(
            DevBoxRoot,
            provider.GetRequiredService<ServiceCatalog>(),
            provider.GetRequiredService<IProcessManager>(),
            provider.GetRequiredService<AddonCatalog>(),
            provider.GetRequiredService<AddonInstaller>(),
            provider.GetRequiredService<PhpExtensionInspector>(),
            provider.GetRequiredService<PhpRuntimePoolManager>(),
            provider.GetRequiredService<IHostMappingService>(),
            provider.GetRequiredService<SiteManager>(),
            provider.GetRequiredService<IRuntimeManager>(),
            provider.GetRequiredService<RuntimePlatformService>(),
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
        var environmentIncomplete = readiness.Items.Any(item => !item.Ready);
        if (environmentIncomplete)
        {
            TryWriteStartupLog("Environment is incomplete. DevBox will open the Modules tab so missing components can be installed without blocking startup.");
        }

        var viewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
        if (environmentIncomplete)
        {
            viewModel.NavigateCommand.Execute("Runtimes");
        }

        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var tray = _serviceProvider.GetRequiredService<ITrayService>();
        tray.Initialize(window);
        _ = tray.StartConfiguredServicesAsync();
        _ = StartConfiguredPhpPoolsAsync(_serviceProvider);

        var startedFromWindows = e.Args.Any(argument => argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        var settings = _serviceProvider.GetRequiredService<IAppSettingsService>();
        if (startedFromWindows && settings.Current.MinimizeToTray)
        {
            window.Hide();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        if (_serviceProvider is not null)
        {
            StopManagedServices(_serviceProvider);
            _serviceProvider.Dispose();
            _serviceProvider = null;
        }
        base.OnExit(e);
    }

    private static void StopManagedServices(IServiceProvider provider)
    {
        try
        {
            var processManager = provider.GetRequiredService<IProcessManager>();
            var catalog = provider.GetRequiredService<ServiceCatalog>();

            TryShutdownMySqlWithRememberedCredentials(provider, processManager, catalog);

            foreach (var definition in catalog.GetDefaultServices())
            {
                try
                {
                    processManager.StopAsync(definition).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    TryWriteStartupLog($"Unable to stop {definition.DisplayName} during DevBox exit: {ex.Message}");
                }
            }

            try
            {
                provider.GetRequiredService<PhpRuntimePoolManager>().StopAllAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                TryWriteStartupLog($"Unable to stop versioned PHP pools during DevBox exit: {ex.Message}");
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryShutdownMySqlWithRememberedCredentials(
        IServiceProvider provider,
        IProcessManager processManager,
        ServiceCatalog catalog)
    {
        var mysql = catalog.GetDefaultServices().First(service => service.Key.Equals("mysql", StringComparison.OrdinalIgnoreCase));
        if (processManager.GetStatus(mysql).State != DevBox.Core.Models.ServiceState.Running)
        {
            return;
        }

        try
        {
            var requested = provider.GetRequiredService<DatabaseManager>()
                .ShutdownUsingLastSuccessfulCredentialsAsync()
                .GetAwaiter()
                .GetResult();
            if (!requested)
            {
                return;
            }

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && processManager.GetStatus(mysql).State == DevBox.Core.Models.ServiceState.Running)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            TryWriteStartupLog($"Credential-aware MySQL shutdown failed; standard fallback will be used: {ex.Message}");
        }
    }

    private static async Task StartConfiguredPhpPoolsAsync(IServiceProvider provider)
    {
        var sites = provider.GetRequiredService<SiteManager>().GetSites();
        var pool = provider.GetRequiredService<PhpRuntimePoolManager>();
        foreach (var version in sites
                     .Select(site => site.PhpVersion)
                     .Where(version => !string.IsNullOrWhiteSpace(version))
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await pool.EnsureRunningAsync(version).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                TryWriteStartupLog($"Unable to start PHP {version} FastCGI: {ex.Message}");
            }
        }
    }

    private static void TryWriteStartupLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(DevBoxRoot, "logs"));
            File.AppendAllText(
                Path.Combine(DevBoxRoot, "logs", "devbox-error.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
            System.Windows.MessageBox.Show(ex.Message, "DevBox hosts update failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
