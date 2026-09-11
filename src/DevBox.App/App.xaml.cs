using System.IO;
using System.Windows;
using DevBox.App.Services;
using DevBox.App.ViewModels;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DevBox.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private SingleInstanceGuard? _singleInstanceGuard;

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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var privilegedExitCode = TryRunPrivilegedCommand(e.Args);
            if (privilegedExitCode is not null)
            {
                Shutdown(privilegedExitCode.Value);
                return;
            }

            DevBoxRoot = ResolveRoot();
            _singleInstanceGuard = SingleInstanceGuard.TryAcquire(DevBoxRoot);
            if (_singleInstanceGuard is null)
            {
                MessageBox.Show(
                    "DevBox is already running for this installation. Check the notification area if the main window is hidden.",
                    "DevBox Windows",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(0);
                return;
            }

            RuntimeLayout.EnsureInitialized(DevBoxRoot);
            StartMainApplication(e.Args);
        }
        catch (Exception ex)
        {
            TryWriteStartupLog($"Fatal startup failure: {ex}");
            MessageBox.Show(
                $"DevBox could not start.\n\n{ex.Message}\n\nSee logs/devbox-error.log for details.",
                "DevBox startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void StartMainApplication(IReadOnlyList<string> args)
    {
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

        var readinessService = _serviceProvider.GetRequiredService<EnvironmentReadinessService>();
        var readiness = readinessService.Check();
        if (readiness.Items.Any(item => !item.Ready))
            _serviceProvider.GetRequiredService<FirstRunWindow>().ShowDialog();

        var environmentReady = readinessService.Check().Items.All(item => item.Ready);
        var window = _serviceProvider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var tray = _serviceProvider.GetRequiredService<ITrayService>();
        tray.Initialize(window);

        var settings = _serviceProvider.GetRequiredService<IAppSettingsService>();
        if (settings.Current.StartServicesAutomatically && environmentReady)
        {
            ObserveBackgroundTask(tray.StartConfiguredServicesAsync(), "automatic service startup");
            ObserveBackgroundTask(StartConfiguredPhpPoolsAsync(_serviceProvider), "automatic versioned PHP pool startup");
        }
        else if (settings.Current.StartServicesAutomatically && !environmentReady)
        {
            TryWriteStartupLog("Automatic service startup was skipped because the first-run environment is still incomplete.");
        }

        var startedFromWindows = args.Any(argument => argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        if (startedFromWindows && settings.Current.MinimizeToTray)
            window.Hide();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        try
        {
            if (_serviceProvider is not null)
                StopManagedServices(_serviceProvider);
        }
        catch (Exception ex)
        {
            TryWriteStartupLog($"Unexpected shutdown failure: {ex}");
        }
        finally
        {
            if (_serviceProvider is not null)
            {
                try
                {
                    _serviceProvider.Dispose();
                }
                catch (Exception ex)
                {
                    TryWriteStartupLog($"Unable to dispose application services cleanly: {ex}");
                }
                _serviceProvider = null;
            }

            _singleInstanceGuard?.Dispose();
            _singleInstanceGuard = null;
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            base.OnExit(e);
        }
    }

    private static void StopManagedServices(IServiceProvider provider)
    {
        var processManager = provider.GetRequiredService<IProcessManager>();
        var catalog = provider.GetRequiredService<ServiceCatalog>();
        IReadOnlyList<ServiceDefinition> definitions;
        try
        {
            definitions = catalog.GetDefaultServices();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            TryWriteStartupLog($"Unable to read managed service catalog during shutdown; core services will still be stopped: {ex.Message}");
            definitions = catalog.GetCoreServices();
        }

        TryShutdownMySqlWithRememberedCredentials(provider, processManager, definitions);

        foreach (var definition in definitions)
        {
            try
            {
                processManager.StopAsync(definition).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
            {
                TryWriteStartupLog($"Unable to stop {definition.DisplayName} during DevBox exit: {ex.Message}");
            }
        }

        try
        {
            provider.GetRequiredService<PhpRuntimePoolManager>().StopAllAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            TryWriteStartupLog($"Unable to stop versioned PHP pools during DevBox exit: {ex.Message}");
        }
    }

    private static void TryShutdownMySqlWithRememberedCredentials(
        IServiceProvider provider,
        IProcessManager processManager,
        IReadOnlyList<ServiceDefinition> definitions)
    {
        var mysql = definitions.FirstOrDefault(service => service.Key.Equals("mysql", StringComparison.OrdinalIgnoreCase));
        if (mysql is null || processManager.GetStatus(mysql).State != ServiceState.Running)
            return;

        try
        {
            var requested = provider.GetRequiredService<DatabaseManager>()
                .ShutdownUsingLastSuccessfulCredentialsAsync()
                .GetAwaiter()
                .GetResult();
            if (!requested)
                return;

            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline && processManager.GetStatus(mysql).State == ServiceState.Running)
                System.Threading.Thread.Sleep(100);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
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

    private static void ObserveBackgroundTask(Task task, string operation)
    {
        ArgumentNullException.ThrowIfNull(task);
        _ = ObserveBackgroundTaskCoreAsync(task, operation);
    }

    private static async Task ObserveBackgroundTaskCoreAsync(Task task, string operation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TryWriteStartupLog($"Background {operation} failed: {ex}");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        TryWriteStartupLog($"Unobserved task exception: {e.Exception}");
        e.SetObserved();
    }

    private static void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        TryWriteStartupLog($"Unhandled UI exception: {e.Exception}");
        e.Handled = true;
        MessageBox.Show(
            $"DevBox encountered an unexpected error and must close.\n\n{e.Exception.Message}",
            "DevBox error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        RequestExit();
    }

    private static void TryWriteStartupLog(string message)
    {
        try
        {
            var root = string.IsNullOrWhiteSpace(DevBoxRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevBox Windows")
                : DevBoxRoot;
            var logDirectory = Path.Combine(root, "logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(
                Path.Combine(logDirectory, "devbox-error.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
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
            MessageBox.Show(ex.Message, "DevBox hosts update failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    private static void ValidateTestDomain(string domain)
    {
        if (!domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Elevated hosts operations are limited to .test domains.", nameof(domain));
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DEVBOX_ROOT");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured);
    }
}
