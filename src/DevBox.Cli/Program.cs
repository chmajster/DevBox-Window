using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = ResolveRoot();
            RuntimeLayout.EnsureInitialized(root);

            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            var command = args[0].ToLowerInvariant();
            return command switch
            {
                "status" => ShowStatus(root, args.Skip(1).ToArray()),
                "start" => await ChangeServiceStateAsync(root, args.Skip(1).ToArray(), ServiceAction.Start),
                "stop" => await ChangeServiceStateAsync(root, args.Skip(1).ToArray(), ServiceAction.Stop),
                "restart" => await ChangeServiceStateAsync(root, args.Skip(1).ToArray(), ServiceAction.Restart),
                "site" => await HandleSiteAsync(root, args.Skip(1).ToArray()),
                "php" => await HandlePhpAsync(root, args.Skip(1).ToArray()),
                "db" => await HandleDatabaseAsync(root, args.Skip(1).ToArray()),
                "addon" => await HandleAddonAsync(root, args.Skip(1).ToArray()),
                _ => Fail($"Unknown command '{args[0]}'. Use 'devbox --help'.")
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or ArgumentException or NotSupportedException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static int ShowStatus(string root, IReadOnlyList<string> args)
    {
        var target = args.Count == 0 ? "all" : args[0];
        var definitions = ResolveServices(root, target);
        using var processes = new ProcessManager();
        foreach (var definition in definitions)
        {
            var status = processes.GetStatus(definition);
            var pid = status.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            Console.WriteLine($"{status.DisplayName}\t{status.State}\tport={status.Port}\tpid={pid}\tversion={status.Version}");
        }
        return 0;
    }

    private static async Task<int> ChangeServiceStateAsync(string root, IReadOnlyList<string> args, ServiceAction action)
    {
        var target = args.Count == 0 ? "all" : args[0];
        var definitions = ResolveServices(root, target);
        using var processes = new ProcessManager();
        var failures = new List<string>();

        foreach (var definition in definitions)
        {
            try
            {
                var snapshot = action switch
                {
                    ServiceAction.Start => await processes.StartAsync(definition),
                    ServiceAction.Stop => await processes.StopAsync(definition),
                    ServiceAction.Restart => await processes.RestartAsync(definition),
                    _ => throw new InvalidOperationException("Unsupported service action.")
                };
                Console.WriteLine($"{definition.Key}: {snapshot.State}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"{definition.Key}: {ex.Message}");
            }
        }

        foreach (var failure in failures)
            Console.Error.WriteLine(failure);
        return failures.Count == 0 ? 0 : 1;
    }

    private static Task<int> HandleSiteAsync(string root, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail("Usage: devbox site create <name> [domain]"));

        var site = new SiteManager(root).Create(args[1], args.Count > 2 ? args[2] : null);
        Console.WriteLine($"Created {site.Name}: http://{site.Domain}");
        Console.WriteLine($"Document root: {site.DocumentRoot}");
        return Task.FromResult(0);
    }

    private static async Task<int> HandlePhpAsync(string root, IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !args[0].Equals("use", StringComparison.OrdinalIgnoreCase))
            return Fail("Usage: devbox php use <version>");

        using var runtimes = new RuntimeManager(root);
        await runtimes.ActivateAsync("php", args[1], "php-cgi.exe");
        Console.WriteLine($"Active PHP runtime: {args[1]}");
        return 0;
    }

    private static async Task<int> HandleDatabaseAsync(string root, IReadOnlyList<string> args)
    {
        if (args.Count < 2 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
            return Fail("Usage: devbox db create <name> [mysql|mariadb|postgresql]");

        var engine = args.Count > 2 ? args[2].ToLowerInvariant() : "mysql";
        var mysql = new DatabaseManager(root);
        var provisioner = new ProjectDatabaseProvisioner(root, mysql);
        if (!provisioner.IsAvailable(engine))
            return Fail($"Database runtime '{engine}' is not installed.");

        DatabaseConnectionOptions? options = engine == "postgresql"
            ? new DatabaseConnectionOptions(Port: 5432, User: "postgres")
            : null;
        await provisioner.EnsureDatabaseAsync(engine, args[1], options);
        Console.WriteLine($"Ensured {engine} database '{args[1]}'.");
        return 0;
    }

    private static async Task<int> HandleAddonAsync(string root, IReadOnlyList<string> args)
    {
        if (args.Count != 2 || !args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
            return Fail("Usage: devbox addon install <key>");

        var catalog = new AddonCatalog(root);
        var addon = catalog.GetAddons().FirstOrDefault(item => item.Key.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (addon is null)
            return Fail($"Addon '{args[1]}' was not found in config/addons.json.");

        using var installer = new AddonInstaller(root);
        await installer.InstallAsync(addon);
        Console.WriteLine($"Installed {addon.DisplayName} {addon.Version}.");
        return 0;
    }

    private static IReadOnlyList<ServiceDefinition> ResolveServices(string root, string target)
    {
        var services = new ServiceCatalog(root).GetServices();
        if (target.Equals("all", StringComparison.OrdinalIgnoreCase))
            return services;

        var service = services.FirstOrDefault(item => item.Key.Equals(target, StringComparison.OrdinalIgnoreCase));
        return service is null
            ? throw new KeyNotFoundException($"Service '{target}' was not found or is disabled.")
            : new[] { service };
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DEVBOX_ROOT");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured);
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DevBox Windows CLI");
        Console.WriteLine("  devbox status [all|service]");
        Console.WriteLine("  devbox start [all|service]");
        Console.WriteLine("  devbox stop [all|service]");
        Console.WriteLine("  devbox restart [all|service]");
        Console.WriteLine("  devbox site create <name> [domain]");
        Console.WriteLine("  devbox php use <version>");
        Console.WriteLine("  devbox db create <name> [mysql|mariadb|postgresql]");
        Console.WriteLine("  devbox addon install <key>");
        Console.WriteLine();
        Console.WriteLine("Set DEVBOX_ROOT to target a portable DevBox root explicitly.");
    }

    private enum ServiceAction
    {
        Start,
        Stop,
        Restart
    }
}
