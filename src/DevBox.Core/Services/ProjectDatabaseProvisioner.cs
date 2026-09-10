using System.Diagnostics;
using System.Text;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectDatabaseProvisioner
{
    private readonly string _rootPath;
    private readonly DatabaseManager _mysql;

    public ProjectDatabaseProvisioner(string rootPath, DatabaseManager mysql)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _mysql = mysql ?? throw new ArgumentNullException(nameof(mysql));
    }

    public bool IsAvailable(string engine) => engine.ToLowerInvariant() switch
    {
        "mysql" => File.Exists(Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysql.exe")),
        "mariadb" => ResolveMariaDbClient() is not null,
        "postgresql" => File.Exists(PostgresTool("psql.exe")) && File.Exists(PostgresTool("createdb.exe")),
        "none" => true,
        _ => false
    };

    public async Task EnsureDatabaseAsync(
        string engine,
        string databaseName,
        DatabaseConnectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var safeName = DatabaseManager.ValidateDatabaseName(databaseName);
        switch (engine.ToLowerInvariant())
        {
            case "mysql":
                await _mysql.CreateDatabaseAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);
                return;
            case "mariadb":
                await EnsureMariaDbAsync(safeName, options ?? new DatabaseConnectionOptions(), cancellationToken).ConfigureAwait(false);
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
        await WithMariaDbConfigAsync(options, async configPath =>
        {
            await RunAsync(
                client,
                [$"--defaults-extra-file={configPath}", $"--execute=CREATE DATABASE IF NOT EXISTS `{databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"],
                null,
                cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task EnsurePostgreSqlAsync(string databaseName, DatabaseConnectionOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        var psql = PostgresTool("psql.exe");
        var createdb = PostgresTool("createdb.exe");
        EnsureFile(psql, "PostgreSQL psql client is not installed under runtime/postgresql/current/bin.");
        EnsureFile(createdb, "PostgreSQL createdb client is not installed under runtime/postgresql/current/bin.");

        await WithPgPassAsync(options, async pgPassPath =>
        {
            var environment = new Dictionary<string, string?> { ["PGPASSFILE"] = pgPassPath };
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
        }).ConfigureAwait(false);
    }

    private string? ResolveMariaDbClient()
    {
        var bin = Path.Combine(_rootPath, "runtime", "mariadb", "current", "bin");
        return new[] { "mariadb.exe", "mysql.exe" }
            .Select(name => Path.Combine(bin, name))
            .FirstOrDefault(File.Exists);
    }

    private string PostgresTool(string name) => Path.Combine(_rootPath, "runtime", "postgresql", "current", "bin", name);

    private async Task WithMariaDbConfigAsync(DatabaseConnectionOptions options, Func<string, Task> operation)
    {
        var directory = Path.Combine(_rootPath, "tmp", "mariadb");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"client-{Guid.NewGuid():N}.cnf");
        try
        {
            var text = new StringBuilder()
                .AppendLine("[client]")
                .Append("host=").AppendLine(QuoteOptionValue(options.Host))
                .Append("port=").AppendLine(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("user=").AppendLine(QuoteOptionValue(options.User));
            if (options.Password is not null) text.Append("password=").AppendLine(QuoteOptionValue(options.Password));
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            await operation(path).ConfigureAwait(false);
        }
        finally
        {
            DeleteSensitiveFile(path);
        }
    }

    private async Task WithPgPassAsync(DatabaseConnectionOptions options, Func<string, Task> operation)
    {
        var directory = Path.Combine(_rootPath, "tmp", "postgresql");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"pgpass-{Guid.NewGuid():N}.conf");
        try
        {
            var line = string.Join(':',
                EscapePgPass(options.Host),
                options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "*",
                EscapePgPass(options.User),
                EscapePgPass(options.Password ?? string.Empty));
            File.WriteAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            await operation(path).ConfigureAwait(false);
        }
        finally
        {
            DeleteSensitiveFile(path);
        }
    }

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
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment) startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}." : error.Trim());
        return output;
    }

    private static string QuoteOptionValue(string value)
    {
        var escaped = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    internal static string EscapePgPass(string value)
    {
        if (value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("PostgreSQL credential fields cannot contain line breaks.");
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal);
    }

    private static void EnsureFile(string path, string message)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(message, path);
    }

    private static void DeleteSensitiveFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
