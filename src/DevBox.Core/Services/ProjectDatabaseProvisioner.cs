using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectDatabaseProvisioner
{
    private static readonly HashSet<string> PostgreSqlSystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "postgres", "template0", "template1"
    };

    private readonly string _rootPath;
    private readonly DatabaseManager _mysql;

    public ProjectDatabaseProvisioner(string rootPath, DatabaseManager mysql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _mysql = mysql ?? throw new ArgumentNullException(nameof(mysql));
    }

    public bool IsAvailable(string engine) => NormalizeEngine(engine) switch
    {
        "mysql" => File.Exists(Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysql.exe")),
        "mariadb" => ResolveMariaDbClient() is not null,
        "postgresql" => File.Exists(PostgresTool("psql.exe")) && File.Exists(PostgresTool("createdb.exe")),
        "none" => true,
        _ => false
    };

    public async Task<bool> DatabaseExistsAsync(
        string engine,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEngine = NormalizeEngine(engine);
        var safeName = DatabaseManager.ValidateDatabaseName(databaseName);
        switch (normalizedEngine)
        {
            case "mysql":
            {
                var effective = options ?? new DatabaseConnectionOptions();
                var values = await _mysql.ListDatabasesAsync(effective, cancellationToken).ConfigureAwait(false);
                return values.Contains(safeName, StringComparer.OrdinalIgnoreCase);
            }
            case "mariadb":
            {
                var effective = options ?? new DatabaseConnectionOptions(Port: 3316);
                effective.Validate();
                var client = ResolveMariaDbClient() ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");
                var output = await RunAsync(client, MariaDbArguments(effective, "--batch", "--skip-column-names", "--execute=SHOW DATABASES;"), MySqlPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Contains(safeName, StringComparer.OrdinalIgnoreCase);
            }
            case "postgresql":
            {
                var effective = options ?? new DatabaseConnectionOptions(Port: 5432, User: "postgres");
                effective.Validate();
                var psql = PostgresTool("psql.exe");
                EnsureFile(psql, "PostgreSQL psql client is not installed under runtime/postgresql/current/bin.");
                var output = await RunAsync(
                    psql,
                    ["--host", effective.Host, "--port", effective.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", effective.User, "--dbname", "postgres", "--tuples-only", "--no-align", "--command", $"SELECT 1 FROM pg_database WHERE datname = '{safeName}';"],
                    PgPasswordEnvironment(effective),
                    cancellationToken).ConfigureAwait(false);
                return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains("1", StringComparer.Ordinal);
            }
            case "none":
                return true;
            default:
                throw new NotSupportedException($"Database engine '{engine}' is not supported by project provisioning.");
        }
    }

    public async Task DropDatabaseAsync(
        string engine,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEngine = NormalizeEngine(engine);
        var safeName = ValidateMutableDatabaseName(normalizedEngine, databaseName);
        switch (normalizedEngine)
        {
            case "mysql":
                await _mysql.DropDatabaseAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);
                return;
            case "mariadb":
            {
                var effective = options ?? new DatabaseConnectionOptions(Port: 3316);
                effective.Validate();
                var client = ResolveMariaDbClient() ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");
                _ = await RunAsync(client, MariaDbArguments(effective, $"--execute=DROP DATABASE IF EXISTS `{safeName}`;"), MySqlPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);
                return;
            }
            case "postgresql":
            {
                var effective = options ?? new DatabaseConnectionOptions(Port: 5432, User: "postgres");
                effective.Validate();
                var dropdb = PostgresTool("dropdb.exe");
                EnsureFile(dropdb, "PostgreSQL dropdb client is not installed under runtime/postgresql/current/bin.");
                _ = await RunAsync(dropdb, ["--host", effective.Host, "--port", effective.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", effective.User, "--if-exists", safeName], PgPasswordEnvironment(effective), cancellationToken).ConfigureAwait(false);
                return;
            }
            case "none":
                return;
            default:
                throw new NotSupportedException($"Database engine '{engine}' is not supported by project provisioning.");
        }
    }

    public async Task EnsureDatabaseAsync(
        string engine,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedEngine = NormalizeEngine(engine);
        var safeName = ValidateMutableDatabaseName(normalizedEngine, databaseName);
        switch (normalizedEngine)
        {
            case "mysql":
                await _mysql.CreateDatabaseAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);
                return;
            case "mariadb":
                await EnsureMariaDbAsync(safeName, options ?? new DatabaseConnectionOptions(Port: 3316), cancellationToken).ConfigureAwait(false);
                return;
            case "postgresql":
                await EnsurePostgreSqlAsync(
                    safeName,
                    options ?? new DatabaseConnectionOptions(Port: 5432, User: "postgres"),
                    cancellationToken).ConfigureAwait(false);
                return;
            case "none":
                return;
            default:
                throw new NotSupportedException($"Database engine '{engine}' is not supported by project provisioning.");
        }
    }

    private async Task EnsureMariaDbAsync(string databaseName, DatabaseConnectionOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var client = ResolveMariaDbClient()
            ?? throw new FileNotFoundException("MariaDB client runtime is not installed under runtime/mariadb/current/bin.");
        _ = await RunAsync(
            client,
            MariaDbArguments(options, $"--execute=CREATE DATABASE IF NOT EXISTS `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"),
            MySqlPasswordEnvironment(options),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsurePostgreSqlAsync(string databaseName, DatabaseConnectionOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var psql = PostgresTool("psql.exe");
        var createdb = PostgresTool("createdb.exe");
        EnsureFile(psql, "PostgreSQL psql client is not installed under runtime/postgresql/current/bin.");
        EnsureFile(createdb, "PostgreSQL createdb client is not installed under runtime/postgresql/current/bin.");

        var environment = PgPasswordEnvironment(options);
        var check = await RunAsync(
            psql,
            ["--host", options.Host, "--port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", options.User, "--dbname", "postgres", "--tuples-only", "--no-align", "--command", $"SELECT 1 FROM pg_database WHERE datname = '{databaseName}';"],
            environment,
            cancellationToken).ConfigureAwait(false);

        if (check.Trim().Equals("1", StringComparison.Ordinal))
            return;

        await RunAsync(
            createdb,
            ["--host", options.Host, "--port", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--username", options.User, databaseName],
            environment,
            cancellationToken).ConfigureAwait(false);
    }

    private string? ResolveMariaDbClient()
    {
        var bin = Path.Combine(_rootPath, "runtime", "mariadb", "current", "bin");
        return new[] { "mariadb.exe", "mysql.exe" }
            .Select(name => Path.Combine(bin, name))
            .FirstOrDefault(File.Exists);
    }

    private string PostgresTool(string name) => Path.Combine(_rootPath, "runtime", "postgresql", "current", "bin", name);

    private static string NormalizeEngine(string engine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);
        return engine.Trim().ToLowerInvariant();
    }

    private static string ValidateMutableDatabaseName(string engine, string databaseName)
    {
        if (engine is "mysql" or "mariadb")
            return DatabaseManager.ValidateMutableDatabaseName(databaseName);

        var safeName = DatabaseManager.ValidateDatabaseName(databaseName);
        if (engine == "postgresql" && PostgreSqlSystemDatabases.Contains(safeName))
            throw new InvalidOperationException($"System database '{safeName}' cannot be modified by DevBox.");
        return safeName;
    }

    private static IReadOnlyList<string> MariaDbArguments(DatabaseConnectionOptions options, params string[] commandArguments)
    {
        var result = new List<string>
        {
            $"--host={options.Host}",
            $"--port={options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            $"--user={options.User}"
        };
        result.AddRange(commandArguments);
        return result;
    }

    private static IReadOnlyDictionary<string, string?>? MySqlPasswordEnvironment(DatabaseConnectionOptions options) =>
        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["MYSQL_PWD"] = options.Password };

    private static IReadOnlyDictionary<string, string?>? PgPasswordEnvironment(DatabaseConnectionOptions options) =>
        string.IsNullOrEmpty(options.Password) ? null : new Dictionary<string, string?> { ["PGPASSWORD"] = options.Password };

    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        var stdout = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);
        var stderr = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}." : error.Trim());
        return output;
    }

    internal static string EscapePgPass(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("PostgreSQL credential fields cannot contain line breaks.");
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static void EnsureFile(string path, string message)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(message, path);
    }
}
