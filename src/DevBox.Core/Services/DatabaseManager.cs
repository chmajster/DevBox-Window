using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class DatabaseManager
{
    private readonly string _rootPath;
    private readonly string _mysqlExecutable;
    private readonly string _mysqlDumpExecutable;
    private readonly string _mysqlAdminExecutable;
    private readonly object _credentialsGate = new();
    private DatabaseConnectionOptions? _lastSuccessfulOptions;

    public DatabaseManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _mysqlExecutable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysql.exe");
        _mysqlDumpExecutable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysqldump.exe");
        _mysqlAdminExecutable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysqladmin.exe");
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlExecutable);

        return await WithClientConfigAsync(options, async configPath =>
        {
            var result = await RunAsync(
                _mysqlExecutable,
                configPath,
                ["--batch", "--skip-column-names", "--execute=SHOW DATABASES;"],
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            return result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }).ConfigureAwait(false);
    }

    public async Task CreateDatabaseAsync(
        string databaseName,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateMutableDatabaseName(databaseName);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlExecutable);

        await WithClientConfigAsync(options, configPath => RunAsync(
            _mysqlExecutable,
            configPath,
            [$"--execute=CREATE DATABASE IF NOT EXISTS `{safeName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;"],
            null,
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task DropDatabaseAsync(
        string databaseName,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateMutableDatabaseName(databaseName);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlExecutable);

        await WithClientConfigAsync(options, configPath => RunAsync(
            _mysqlExecutable,
            configPath,
            [$"--execute=DROP DATABASE IF EXISTS `{safeName}`;"],
            null,
            null,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task CloneDatabaseAsync(
        string sourceDatabase,
        string destinationDatabase,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateDatabaseName(sourceDatabase);
        var destination = ValidateMutableDatabaseName(destinationDatabase);
        if (source.Equals(destination, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Source and destination database names must be different.", nameof(destinationDatabase));
        }

        var databases = await ListDatabasesAsync(options, cancellationToken).ConfigureAwait(false);
        if (!databases.Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Source database '{source}' does not exist.");
        }
        if (databases.Contains(destination, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Destination database '{destination}' already exists.");
        }

        var tempDirectory = Path.Combine(_rootPath, "tmp", "mysql", "clone");
        Directory.CreateDirectory(tempDirectory);
        var backupPath = Path.Combine(tempDirectory, $"{source}-{Guid.NewGuid():N}.sql");
        var restoreStarted = false;
        Exception? operationFailure = null;
        try
        {
            await BackupAsync(source, backupPath, options, cancellationToken).ConfigureAwait(false);
            restoreStarted = true;
            await RestoreAsync(destination, backupPath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception original)
        {
            operationFailure = original;
            if (!restoreStarted)
                throw;

            try
            {
                using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await DropDatabaseAsync(destination, options, rollbackTimeout.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackError)
            {
                var aggregate = new AggregateException(
                    $"Database clone failed and rollback of '{destination}' also failed.",
                    original,
                    rollbackError);
                operationFailure = aggregate;
                throw aggregate;
            }
            throw;
        }
        finally
        {
            try
            {
                DeleteFileIfExists(backupPath);
            }
            catch (Exception cleanupError) when (operationFailure is not null)
            {
                throw new AggregateException(
                    "Database clone failed and cleanup of its temporary backup also failed.",
                    operationFailure,
                    cleanupError);
            }
        }
    }

    public async Task RenameDatabaseAsync(
        string sourceDatabase,
        string destinationDatabase,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var source = ValidateMutableDatabaseName(sourceDatabase);
        var destination = ValidateMutableDatabaseName(destinationDatabase);
        await CloneDatabaseAsync(source, destination, options, cancellationToken).ConfigureAwait(false);
        try
        {
            await DropDatabaseAsync(source, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Database was cloned to '{destination}', but DevBox could not remove the original '{source}'. Both databases were retained to avoid data loss.",
                ex);
        }
    }

    public async Task<DatabaseInfo> GetDatabaseInfoAsync(
        string databaseName,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateDatabaseName(databaseName);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlExecutable);

        return await WithClientConfigAsync(options, async configPath =>
        {
            var query = $"SELECT s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME, " +
                        $"COALESCE(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0) " +
                        $"FROM information_schema.SCHEMATA s " +
                        $"LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME " +
                        $"WHERE s.SCHEMA_NAME = '{safeName}' " +
                        $"GROUP BY s.SCHEMA_NAME, s.DEFAULT_CHARACTER_SET_NAME, s.DEFAULT_COLLATION_NAME;";

            var result = await RunAsync(
                _mysqlExecutable,
                configPath,
                ["--batch", "--skip-column-names", $"--execute={query}"],
                null,
                null,
                cancellationToken).ConfigureAwait(false);

            var line = result.StandardOutput
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (line is null)
            {
                throw new InvalidOperationException($"Database '{safeName}' does not exist.");
            }

            var fields = line.Split('\t');
            if (fields.Length != 4 || !long.TryParse(fields[3], out var sizeBytes))
            {
                throw new InvalidDataException("MySQL returned an unexpected database metadata format.");
            }

            return new DatabaseInfo(fields[0], fields[1], fields[2], sizeBytes);
        }).ConfigureAwait(false);
    }

    public async Task<string> BackupAsync(
        string databaseName,
        string? destinationPath,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateDatabaseName(databaseName);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlDumpExecutable);

        var backupPath = ResolveBackupPath(safeName, destinationPath);
        var backupDirectory = Path.GetDirectoryName(backupPath)!;
        Directory.CreateDirectory(backupDirectory);
        var temporaryBackupPath = Path.Combine(
            backupDirectory,
            $".{Path.GetFileName(backupPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await WithClientConfigAsync(options, configPath => RunAsync(
                _mysqlDumpExecutable,
                configPath,
                ["--single-transaction", "--routines", "--events", "--triggers", "--set-gtid-purged=OFF", safeName],
                temporaryBackupPath,
                null,
                cancellationToken)).ConfigureAwait(false);
            File.Move(temporaryBackupPath, backupPath, overwrite: true);
            return backupPath;
        }
        finally
        {
            TryDeleteFile(temporaryBackupPath);
        }
    }

    public async Task RestoreAsync(
        string databaseName,
        string sqlFilePath,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateMutableDatabaseName(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlFilePath);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureExecutable(_mysqlExecutable);

        var fullSqlPath = Path.GetFullPath(sqlFilePath);
        if (!File.Exists(fullSqlPath))
        {
            throw new FileNotFoundException("SQL backup file was not found.", fullSqlPath);
        }

        await CreateDatabaseAsync(safeName, options, cancellationToken).ConfigureAwait(false);
        await WithClientConfigAsync(options, configPath => RunAsync(
            _mysqlExecutable,
            configPath,
            [safeName],
            null,
            fullSqlPath,
            cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ShutdownUsingLastSuccessfulCredentialsAsync(CancellationToken cancellationToken = default)
    {
        DatabaseConnectionOptions? options;
        lock (_credentialsGate)
        {
            options = _lastSuccessfulOptions;
        }

        if (options is null || !File.Exists(_mysqlAdminExecutable))
        {
            return false;
        }

        options.Validate();
        await WithClientConfigAsync(options, configPath => RunAsync(
            _mysqlAdminExecutable,
            configPath,
            ["--protocol=tcp", "shutdown"],
            null,
            null,
            cancellationToken)).ConfigureAwait(false);
        return true;
    }

    internal DatabaseConnectionOptions? GetLastSuccessfulConnectionOptions()
    {
        lock (_credentialsGate)
        {
            return _lastSuccessfulOptions;
        }
    }

    internal static string ValidateDatabaseName(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        var normalized = databaseName.Trim();
        if (!DatabaseNameRegex().IsMatch(normalized))
        {
            throw new ArgumentException("Database name may contain only letters, digits and underscores and must be 1-64 characters long.", nameof(databaseName));
        }
        return normalized;
    }

    internal static string ValidateMutableDatabaseName(string databaseName)
    {
        var normalized = ValidateDatabaseName(databaseName);
        if (SystemDatabases.Contains(normalized))
        {
            throw new InvalidOperationException($"System database '{normalized}' cannot be modified by DevBox.");
        }
        return normalized;
    }

    private void RememberSuccessfulOptions(DatabaseConnectionOptions options)
    {
        lock (_credentialsGate)
        {
            _lastSuccessfulOptions = options;
        }
    }

    private string ResolveBackupPath(string databaseName, string? destinationPath)
    {
        if (!string.IsNullOrWhiteSpace(destinationPath))
        {
            var full = Path.GetFullPath(destinationPath);
            if (!full.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Database backup destination must use the .sql extension.", nameof(destinationPath));
            }
            return full;
        }

        var fileName = $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.sql";
        return Path.Combine(_rootPath, "backups", "databases", fileName);
    }

    private async Task<T> WithClientConfigAsync<T>(
        DatabaseConnectionOptions options,
        Func<string, Task<T>> operation)
    {
        var tempDirectory = Path.Combine(_rootPath, "tmp", "mysql");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, $"client-{Guid.NewGuid():N}.cnf");
        Exception? operationFailure = null;

        try
        {
            var config = new StringBuilder()
                .AppendLine("[client]")
                .Append("host=").AppendLine(QuoteOptionValue(options.Host))
                .Append("port=").AppendLine(options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append("user=").AppendLine(QuoteOptionValue(options.User));

            if (options.Password is not null)
            {
                config.Append("password=").AppendLine(QuoteOptionValue(options.Password));
            }

            File.WriteAllText(configPath, config.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.SetAttributes(configPath, File.GetAttributes(configPath) | FileAttributes.Hidden | FileAttributes.Temporary);
            var result = await operation(configPath).ConfigureAwait(false);
            RememberSuccessfulOptions(options);
            return result;
        }
        catch (Exception ex)
        {
            operationFailure = ex;
            throw;
        }
        finally
        {
            try
            {
                SecureDeleteClientConfig(configPath);
            }
            catch (Exception cleanupError) when (operationFailure is not null)
            {
                throw new AggregateException(
                    "MySQL operation failed and cleanup of its temporary credential file also failed.",
                    operationFailure,
                    cleanupError);
            }
        }
    }

    private async Task WithClientConfigAsync(
        DatabaseConnectionOptions options,
        Func<string, Task> operation)
    {
        await WithClientConfigAsync<object?>(options, async configPath =>
        {
            await operation(configPath).ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        string clientConfigPath,
        IReadOnlyList<string> arguments,
        string? standardOutputPath,
        string? standardInputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = standardInputPath is not null
        };

        startInfo.ArgumentList.Add($"--defaults-extra-file={clientConfigPath}");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
            }
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}: {ex.Message}", ex);
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string>? outputTask = standardOutputPath is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : null;

        try
        {
            if (standardOutputPath is not null)
            {
                await using var output = new FileStream(standardOutputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                var outputCopyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
                await Task.WhenAll(process.WaitForExitAsync(cancellationToken), outputCopyTask).ConfigureAwait(false);
            }
            else if (standardInputPath is not null)
            {
                await using var input = new FileStream(standardInputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            var error = await errorTask.ConfigureAwait(false);
            var outputText = outputTask is null ? string.Empty : await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}."
                    : error.Trim());
            }

            return new ProcessResult(outputText);
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            DeleteFileIfExists(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MySQL runtime is not installed or is incomplete.", path);
        }
    }

    private static void SecureDeleteClientConfig(string path)
    {
        if (!File.Exists(path))
            return;

        File.SetAttributes(path, FileAttributes.Normal);
        var length = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var zeros = new byte[4096];
            long remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(zeros.Length, remaining);
                stream.Write(zeros, 0, count);
                remaining -= count;
            }
            stream.Flush(flushToDisk: true);
        }
        File.Delete(path);
    }

    private static string QuoteOptionValue(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
    {
        "information_schema",
        "mysql",
        "performance_schema",
        "sys"
    };

    private sealed record ProcessResult(string StandardOutput);

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNameRegex();
}
