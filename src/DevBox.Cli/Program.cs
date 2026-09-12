using System.Text.Json;
using System.Text.Json.Serialization;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Cli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        var json = args.Any(value => value.Equals("--json", StringComparison.OrdinalIgnoreCase));
        args = args.Where(value => !value.Equals("--json", StringComparison.OrdinalIgnoreCase)).ToArray();
        try
        {
            var root = ResolveRoot();
            RuntimeLayout.EnsureInitialized(root);
            if (args.Length == 0 || IsHelp(args[0]))
            {
                PrintHelp();
                return 0;
            }

            return args[0].ToLowerInvariant() switch
            {
                "status" => ShowStatus(root, args[1..], json),
                "start" => await ChangeServiceStateAsync(root, args[1..], ServiceAction.Start, json),
                "stop" => await ChangeServiceStateAsync(root, args[1..], ServiceAction.Stop, json),
                "restart" => await ChangeServiceStateAsync(root, args[1..], ServiceAction.Restart, json),
                "site" => await HandleSiteAsync(root, args[1..], json),
                "php" => await HandlePhpAsync(root, args[1..], json),
                "db" => await HandleDatabaseAsync(root, args[1..], json),
                "addon" => await HandleAddonAsync(root, args[1..], json),
                "runtime" => await HandleRuntimeAsync(root, args[1..], json),
                "env" => await HandleEnvironmentAsync(root, args[1..], json),
                "project" => await HandleProjectAsync(root, args[1..], json),
                "diagnostics" => HandleDiagnostics(root, json),
                "secret" => HandleSecret(root, args[1..], json),
                "wordpress" => await HandleWordPressAsync(root, args[1..], json),
                _ => Fail($"Unknown command '{args[0]}'. Use 'devbox --help'.", json)
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or ArgumentException or NotSupportedException or KeyNotFoundException or FormatException or OverflowException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            return Fail(ex.Message, json, 1);
        }
    }

    private static int ShowStatus(string root, IReadOnlyList<string> args, bool json)
    {
        var target = args.Count == 0 ? "all" : args[0];
        var definitions = ResolveServices(root, target);
        using var processes = new ProcessManager();
        var snapshots = definitions.Select(processes.GetStatus).ToArray();
        if (json)
            WriteJson(snapshots);
        else
        {
            foreach (var status in snapshots)
                Console.WriteLine($"{status.DisplayName}\t{status.State}\tport={status.Port}\tpid={status.ProcessId?.ToString() ?? "-"}\tversion={status.Version}");
        }
        return 0;
    }

    private static async Task<int> ChangeServiceStateAsync(string root, IReadOnlyList<string> args, ServiceAction action, bool json)
    {
        var definitions = ResolveServices(root, args.Count == 0 ? "all" : args[0]);
        using var processes = new ProcessManager();
        var snapshots = new List<ServiceSnapshot>();
        var failures = new List<object>();
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
                snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or FileNotFoundException or System.ComponentModel.Win32Exception)
            {
                failures.Add(new { service = definition.Key, error = ex.Message });
            }
        }
        if (json)
            WriteJson(new { services = snapshots, failures });
        else
        {
            foreach (var snapshot in snapshots)
                Console.WriteLine($"{snapshot.Key}: {snapshot.State}");
            foreach (var failure in failures)
                Console.Error.WriteLine(JsonSerializer.Serialize(failure));
        }
        return failures.Count == 0 ? 0 : 1;
    }

    private static Task<int> HandleSiteAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count < 2 || !args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail("Usage: devbox site create <name> [domain]", json));
        var site = new SiteManager(root).Create(args[1], args.Count > 2 ? args[2] : null);
        WriteValue(site, json, $"Created {site.Name}: http://{site.Domain}{Environment.NewLine}Document root: {site.DocumentRoot}");
        return Task.FromResult(0);
    }

    private static async Task<int> HandlePhpAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count != 2 || !args[0].Equals("use", StringComparison.OrdinalIgnoreCase))
            return Fail("Usage: devbox php use <version>", json);
        using var runtimes = new RuntimeManager(root);
        await runtimes.ActivateAsync("php", args[1], "php-cgi.exe");
        WriteValue(new { runtime = "php", version = args[1], active = true }, json, $"Active PHP runtime: {args[1]}");
        return 0;
    }

    private static async Task<int> HandleRuntimeAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox runtime <list|install|use|remove> ...", json);
        using var runtimes = new RuntimePlatformService(root);
        switch (args[0].ToLowerInvariant())
        {
            case "list":
            {
                var values = runtimes.GetStatuses(args.Count > 1 ? args[1] : null);
                if (json) WriteJson(values);
                else foreach (var value in values)
                    Console.WriteLine($"{value.Package.Key}\t{value.Package.Version}\t{value.Package.Architecture}\tinstalled={value.Installed}\tactive={value.Active}\tvalid={value.Valid}\tsupport={value.SupportState}");
                return 0;
            }
            case "install" when args.Count == 3:
                await runtimes.InstallAsync(args[1], args[2]);
                WriteValue(new { runtime = args[1], version = args[2], installed = true }, json, $"Installed {args[1]} {args[2]}.");
                return 0;
            case "use" when args.Count == 3:
                await runtimes.ActivateAsync(args[1], args[2]);
                WriteValue(new { runtime = args[1], version = args[2], active = true }, json, $"Activated {args[1]} {args[2]}.");
                return 0;
            case "remove" when args.Count == 3:
                await runtimes.RemoveAsync(args[1], args[2]);
                WriteValue(new { runtime = args[1], version = args[2], removed = true }, json, $"Removed {args[1]} {args[2]}.");
                return 0;
            default:
                return Fail("Usage: devbox runtime list [key] | install <key> <version> | use <key> <version> | remove <key> <version>", json);
        }
    }

    private static async Task<int> HandleDatabaseAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox db <create|runtime|backup|restore> ...", json);

        if (args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
                return Fail("Usage: devbox db create <name> [mysql|mariadb|postgresql]", json);
            var engine = args.Count > 2 ? args[2].ToLowerInvariant() : "mysql";
            var mysql = new DatabaseManager(root);
            var provisioner = new ProjectDatabaseProvisioner(root, mysql);
            if (!provisioner.IsAvailable(engine))
                return Fail($"Database runtime '{engine}' is not installed.", json);
            DatabaseConnectionOptions? options = engine == "postgresql" ? new DatabaseConnectionOptions(Port: 5432, User: "postgres") : null;
            await provisioner.EnsureDatabaseAsync(engine, args[1], options);
            WriteValue(new { engine, database = args[1] }, json, $"Ensured {engine} database '{args[1]}'.");
            return 0;
        }

        using var runtime = new DatabaseRuntimeService(root);
        if (args[0].Equals("runtime", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2)
                return Fail("Usage: devbox db runtime <list|register|start|stop|restart> ...", json);
            switch (args[1].ToLowerInvariant())
            {
                case "list":
                {
                    var values = runtime.GetInstances(args.Count > 2 ? args[2] : null);
                    if (json) WriteJson(values);
                    else foreach (var value in values)
                        Console.WriteLine($"{value.Engine}\t{value.Version}\tport={value.Port}\tstate={value.State}\tinitialized={value.Initialized}\tpid={value.ProcessId?.ToString() ?? "-"}");
                    return 0;
                }
                case "register" when args.Count is 4 or 5:
                {
                    int? port = null;
                    if (args.Count == 5)
                    {
                        if (!int.TryParse(args[4], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedPort) || parsedPort is < 1 or > 65535)
                            return Fail("Database runtime port must be a number from 1 to 65535.", json);
                        port = parsedPort;
                    }
                    var value = runtime.Register(args[2], args[3], port);
                    WriteValue(value, json, $"Registered {value.Engine} {value.Version} on port {value.Port}.");
                    return 0;
                }
                case "start" when args.Count == 4:
                {
                    var value = await runtime.StartAsync(args[2], args[3]);
                    WriteValue(value, json, $"{value.DisplayName}: {value.State}");
                    return 0;
                }
                case "stop" when args.Count == 4:
                {
                    var value = await runtime.StopAsync(args[2], args[3]);
                    WriteValue(value, json, $"{value.DisplayName}: {value.State}");
                    return 0;
                }
                case "restart" when args.Count == 4:
                {
                    var value = await runtime.RestartAsync(args[2], args[3]);
                    WriteValue(value, json, $"{value.DisplayName}: {value.State}");
                    return 0;
                }
                default:
                    return Fail("Usage: devbox db runtime list [engine] | register <engine> <version> [port] | start|stop|restart <engine> <version>", json);
            }
        }

        if (args[0].Equals("backup", StringComparison.OrdinalIgnoreCase) && args.Count is 4 or 5)
        {
            var result = await runtime.BackupAsync(args[1], args[2], args[3], destinationPath: args.Count == 5 ? args[4] : null);
            WriteValue(result, json, result.BackupPath);
            return 0;
        }
        if (args[0].Equals("restore", StringComparison.OrdinalIgnoreCase) && args.Count == 5)
        {
            await runtime.RestoreAsync(args[1], args[2], args[3], args[4]);
            WriteValue(new { engine = args[1], version = args[2], database = args[3], restored = true }, json, $"Restored {args[3]} from {args[4]}.");
            return 0;
        }
        return Fail("Usage: devbox db backup <engine> <version> <database> [destination] | restore <engine> <version> <database> <backup>", json);
    }

    private static async Task<int> HandleAddonAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count != 2 || !args[0].Equals("install", StringComparison.OrdinalIgnoreCase))
            return Fail("Usage: devbox addon install <key>", json);
        var catalog = new AddonCatalog(root);
        var addon = catalog.GetAddons().FirstOrDefault(item => item.Key.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (addon is null)
            return Fail($"Addon '{args[1]}' was not found in config/addons.json.", json);
        using var installer = new AddonInstaller(root);
        await installer.InstallAsync(addon);
        WriteValue(new { addon = addon.Key, addon.Version, installed = true }, json, $"Installed {addon.DisplayName} {addon.Version}.");
        return 0;
    }

    private static async Task<int> HandleEnvironmentAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox env <profiles|lock|apply|drift|export|import> ...", json);
        switch (args[0].ToLowerInvariant())
        {
            case "profiles":
            {
                var profiles = new EnvironmentProfileService(root).GetProfiles();
                if (json) WriteJson(profiles);
                else foreach (var profile in profiles)
                    Console.WriteLine($"{profile.Key}\t{profile.DisplayName}\t{profile.Kind}\t{profile.Description}");
                return 0;
            }
            case "lock" when args.Count is 2 or 3:
            {
                using var locks = new EnvironmentLockService(root);
                var value = locks.Generate(ProjectPath(root, args[1]), args.Count == 3 ? args[2] : null);
                WriteValue(value, json, Path.Combine(ProjectPath(root, args[1]), EnvironmentLockService.LockFileName));
                return 0;
            }
            case "apply" when args.Count is 2 or 3:
            {
                using var locks = new EnvironmentLockService(root);
                var value = args.Count == 3
                    ? await locks.ApplyProfileAsync(ProjectPath(root, args[1]), args[2])
                    : await locks.ApplyLockAsync(ProjectPath(root, args[1]));
                WriteValue(value, json, $"Applied environment. {value.Applied.Count} action(s), {value.Warnings.Count} warning(s).");
                return value.Warnings.Count == 0 ? 0 : 1;
            }
            case "drift" when args.Count == 2:
            {
                using var locks = new EnvironmentLockService(root);
                var drift = locks.GetDrift(ProjectPath(root, args[1]));
                if (json) WriteJson(new { drift });
                else if (drift.Count == 0) Console.WriteLine("No environment drift detected.");
                else foreach (var item in drift) Console.WriteLine(item);
                return drift.Count == 0 ? 0 : 1;
            }
            case "export" when args.Count is 2 or 3:
            {
                var service = new RemoteEnvironmentService(root);
                var path = service.ExportProjectLock(ProjectPath(root, args[1]), args.Count == 3 ? args[2] : null);
                WriteValue(new { path }, json, path);
                return 0;
            }
            case "import" when args.Count is 2 or 3:
            {
                var service = new RemoteEnvironmentService(root);
                var result = service.Import(args[1], replaceExisting: args.Count == 3 && args[2].Equals("--replace", StringComparison.OrdinalIgnoreCase));
                WriteValue(result, json, $"Imported environment profile {result.ProfileKey}.");
                return 0;
            }
            default:
                return Fail("Usage: devbox env profiles | lock <project> [profile] | apply <project> [profile] | drift <project> | export <project> [destination] | import <file> [--replace]", json);
        }
    }

    private static async Task<int> HandleProjectAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox project <snapshot|restore|export|import|action|clone> ...", json);
        switch (args[0].ToLowerInvariant())
        {
            case "snapshot" when args.Count == 2:
            {
                var result = await new ProjectSnapshotService(root).CreateAsync(ProjectPath(root, args[1]));
                WriteValue(result, json, result.SnapshotPath);
                return 0;
            }
            case "restore" when args.Count is 3 or 4:
            {
                var path = await new ProjectSnapshotService(root).RestoreAsync(args[1], args[2], args.Count == 4 && args[3].Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
                WriteValue(new { projectRoot = path }, json, path);
                return 0;
            }
            case "export" when args.Count is 2 or 3:
            {
                var result = await new ProjectTransferService(root).ExportAsync(ProjectPath(root, args[1]), destinationPath: args.Count == 3 ? args[2] : null);
                WriteValue(result, json, result.ArchivePath);
                return 0;
            }
            case "import" when args.Count is >= 2 and <= 5:
            {
                var name = args.Count > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
                var domain = args.Count > 3 && !args[3].StartsWith("--", StringComparison.Ordinal) ? args[3] : null;
                var overwrite = args.Any(value => value.Equals("--overwrite", StringComparison.OrdinalIgnoreCase));
                var path = await new ProjectTransferService(root).ImportAsync(args[1], name, domain, overwrite);
                WriteValue(new { projectRoot = path }, json, path);
                return 0;
            }
            case "clone" when args.Count == 3:
            {
                var transfer = new ProjectTransferService(root);
                var exported = await transfer.ExportAsync(ProjectPath(root, args[1]), new ProjectSnapshotOptions(IncludeDatabase: false));
                var path = await transfer.ImportAsync(exported.ArchivePath, args[2]);
                try { File.Delete(exported.ArchivePath); } catch (IOException) { }
                WriteValue(new { projectRoot = path }, json, path);
                return 0;
            }
            case "action" when args.Count >= 3:
            {
                var workspace = CreateWorkspace(root);
                var service = new ProjectActionService(root, workspace);
                var project = ProjectPath(root, args[2]);
                switch (args[1].ToLowerInvariant())
                {
                    case "list":
                    {
                        var values = service.GetActions(project);
                        if (json) WriteJson(values); else foreach (var value in values) Console.WriteLine($"{value.Key}\t{value.DisplayName}\t{value.Executable}");
                        return 0;
                    }
                    case "run" when args.Count == 4:
                    {
                        var result = await service.RunAsync(project, args[3]);
                        WriteValue(result, json, result.StandardOutput);
                        return result.ExitCode;
                    }
                    case "run-all" when args.Count == 3:
                    {
                        var results = await service.RunAllAsync(project);
                        if (json) WriteJson(results); else foreach (var result in results) Console.WriteLine($"{result.Key}: exit={result.ExitCode} {result.Duration}");
                        return results.All(value => value.Succeeded) ? 0 : 1;
                    }
                }
                return Fail("Usage: devbox project action list <project> | run <project> <action> | run-all <project>", json);
            }
            default:
                return Fail("Usage: devbox project snapshot <project> | restore <snapshot> <name> [--overwrite] | export <project> [destination] | import <archive> [name] [domain] [--overwrite] | clone <project> <new-name> | action ...", json);
        }
    }

    private static int HandleDiagnostics(string root, bool json)
    {
        var report = new AdvancedDiagnosticsService(root).Run();
        if (json) WriteJson(report);
        else foreach (var finding in report.Findings)
            Console.WriteLine($"{finding.Severity}\t{finding.Area}\t{finding.Summary}\t{finding.Details}");
        return report.HasErrors ? 1 : 0;
    }

    private static int HandleSecret(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox secret <list|set|delete> ...", json);
        var store = new SecureSecretStore(root);
        switch (args[0].ToLowerInvariant())
        {
            case "list":
            {
                var keys = store.ListKeys();
                if (json) WriteJson(keys); else foreach (var key in keys) Console.WriteLine(key);
                return 0;
            }
            case "set" when args.Count == 2:
            {
                var value = ReadSecret("Secret value: ");
                try
                {
                    store.Set(args[1], value);
                    WriteValue(new { key = args[1], stored = true }, json, $"Stored secret '{args[1]}'.");
                }
                finally
                {
                    value = string.Empty;
                }
                return 0;
            }
            case "delete" when args.Count == 2:
            {
                var removed = store.Delete(args[1]);
                WriteValue(new { key = args[1], removed }, json, removed ? $"Deleted secret '{args[1]}'." : $"Secret '{args[1]}' was not found.");
                return removed ? 0 : 1;
            }
            default:
                return Fail("Usage: devbox secret list | set <key> | delete <key>. Secret values are read from stdin and are never accepted as command-line arguments.", json);
        }
    }

    private static async Task<int> HandleWordPressAsync(string root, IReadOnlyList<string> args, bool json)
    {
        if (args.Count == 0)
            return Fail("Usage: devbox wordpress <wpcli-import|status|create> ...", json);
        var wordpress = new WordPressToolkitService(root);
        switch (args[0].ToLowerInvariant())
        {
            case "wpcli-import" when args.Count == 3:
                wordpress.ImportWpCli(args[1], args[2]);
                WriteValue(new { installed = true }, json, "Imported verified WP-CLI.");
                return 0;
            case "status" when args.Count == 2:
            {
                var status = await wordpress.GetStatusAsync(ProjectPath(root, args[1]));
                if (json) WriteJson(status); else foreach (var line in status) Console.WriteLine(line);
                return 0;
            }
            case "create" when args.Count is 5 or 6:
            {
                var password = ReadSecret("WordPress admin password: ");
                try
                {
                    var result = await wordpress.CreateSiteAsync(new WordPressSiteRequest(
                        args[1],
                        args[2],
                        args[3],
                        password,
                        args[4],
                        Domain: args.Count == 6 ? args[5] : null));
                    WriteValue(result, json, result.AdminUrl);
                    return 0;
                }
                finally
                {
                    password = string.Empty;
                }
            }
            default:
                return Fail("Usage: devbox wordpress wpcli-import <file> <sha256> | status <project> | create <name> <title> <admin-user> <admin-email> [domain]", json);
        }
    }

    private static ProjectWorkspaceService CreateWorkspace(string root)
    {
        var sites = new SiteManager(root);
        return new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
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

    private static string ProjectPath(string root, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, "www", value));
    }

    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine() ?? string.Empty;
        Console.Error.Write(prompt);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar))
                value.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return value.ToString();
    }

    private static string ResolveRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DEVBOX_ROOT");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? AppContext.BaseDirectory : configured);
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help";

    private static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteValue(object value, bool json, string text)
    {
        if (json) WriteJson(value); else Console.WriteLine(text);
    }

    private static int Fail(string message, bool json, int exitCode = 2)
    {
        if (json)
            Console.Error.WriteLine(JsonSerializer.Serialize(new { error = message }, JsonOptions));
        else
            Console.Error.WriteLine($"Error: {message}");
        return exitCode;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DevBox Windows CLI 2.0");
        Console.WriteLine("Global: append --json for machine-readable output.");
        Console.WriteLine("  devbox status|start|stop|restart [all|service]");
        Console.WriteLine("  devbox runtime list [key]");
        Console.WriteLine("  devbox runtime install|use|remove <key> <version>");
        Console.WriteLine("  devbox db create <name> [mysql|mariadb|postgresql]");
        Console.WriteLine("  devbox db runtime list [engine]");
        Console.WriteLine("  devbox db runtime register <engine> <version> [port]");
        Console.WriteLine("  devbox db runtime start|stop|restart <engine> <version>");
        Console.WriteLine("  devbox db backup <engine> <version> <database> [destination]");
        Console.WriteLine("  devbox db restore <engine> <version> <database> <backup>");
        Console.WriteLine("  devbox env profiles|lock|apply|drift|export|import ...");
        Console.WriteLine("  devbox project snapshot|restore|export|import|clone|action ...");
        Console.WriteLine("  devbox diagnostics");
        Console.WriteLine("  devbox secret list|set|delete ...");
        Console.WriteLine("  devbox wordpress wpcli-import|status|create ...");
        Console.WriteLine("  devbox addon install <key>");
        Console.WriteLine("  devbox site create <name> [domain]");
        Console.WriteLine("Set DEVBOX_ROOT to target a portable DevBox root explicitly.");
    }

    private enum ServiceAction
    {
        Start,
        Stop,
        Restart
    }
}
