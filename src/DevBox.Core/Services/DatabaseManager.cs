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

    public DatabaseManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _mysqlExecutable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysql.exe");
        _mysqlDumpExecutable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysqldump.exe");
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
        var safeName = ValidateDatabaseName(databaseName);
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
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

        try
        {
            await WithClientConfigAsync(options, configPath => RunAsync(
                _mysqlDumpExecutable,
                configPath,
                ["--single-transaction", "--routines", "--events", "--triggers", "--set-gtid-purged=OFF", safeName],
                backupPath,
                null,
                cancellationToken)).ConfigureAwait(false);
            return backupPath;
        }
        catch
        {
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            throw;
        }
    }

    public async Task RestoreAsync(
        string databaseName,
        string sqlFilePath,
        DatabaseConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        var safeName = ValidateDatabaseName(databaseName);
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

        var fileName = $"{databaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.sql";
        return Path.Combine(_rootPath, "backups", "databases", fileName);
    }

    private async Task<T> WithClientConfigAsync<T>(
        DatabaseConnectionOptions options,
        Func<string, Task<T>> operation)
    {
        var tempDirectory = Path.Combine(_rootPath, "tmp", "mysql");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(tempDirectory, $"client-{Guid.NewGuid():N}.cnf");

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
            File.SetAttributes(configPath, File.GetAttributes(configPath) | FileAttributes.Hidden);
            return await operation(configPath).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(configPath))
            {
                try
                {
                    File.SetAttributes(configPath, FileAttributes.Normal);
                    File.Delete(configPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
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
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        }

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string>? outputTask = standardOutputPath is null
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : null;

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

    private static void EnsureExecutable(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MySQL runtime is not installed or is incomplete.", path);
        }
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

    private sealed record ProcessResult(string StandardOutput);

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNameRegex();
}
