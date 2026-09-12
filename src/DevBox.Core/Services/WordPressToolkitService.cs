using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class WordPressToolkitService
{
    private readonly string _rootPath;
    private readonly string _wpCliPath;
    private readonly SiteManager _sites;
    private readonly ProjectWorkspaceService _workspace;

    public WordPressToolkitService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wpCliPath = Path.Combine(_rootPath, "tools", "wp-cli", "wp-cli.phar");
        _sites = new SiteManager(_rootPath);
        _workspace = new ProjectWorkspaceService(_rootPath, _sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
    }

    public bool IsWpCliInstalled => File.Exists(_wpCliPath);

    public void ImportWpCli(string sourcePath, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("WP-CLI package was not found.", source);
        VerifySha256(source, expectedSha256);
        var directory = Path.GetDirectoryName(_wpCliPath)!;
        Directory.CreateDirectory(directory);
        var temp = _wpCliPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(source, temp, overwrite: false);
            File.Move(temp, _wpCliPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public async Task<WordPressSiteResult> CreateSiteAsync(WordPressSiteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        if (!IsWpCliInstalled)
            throw new FileNotFoundException("WP-CLI is not installed. Import a verified wp-cli.phar into DevBox first.", _wpCliPath);

        var actions = new List<string>();
        var databaseName = string.IsNullOrWhiteSpace(request.DatabaseName) ? SafeDatabaseName(request.Name) : SafeDatabaseName(request.DatabaseName);
        var databaseOptions = request.DatabaseOptions ?? ResolveDatabaseOptions(request.DatabaseEngine);
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(request.Domain ?? LocalDomainName.FromName(request.Name));
        var databaseManager = new DatabaseManager(_rootPath);
        var projectDatabases = new ProjectDatabaseProvisioner(_rootPath, databaseManager);
        var provisioning = new ProjectProvisioningService(
            _rootPath,
            _workspace,
            databaseManager,
            new ManagedServiceCatalog(_rootPath));
        var result = await provisioning.ProvisionAsync(
            new ProjectCreateRequest(
                request.Name,
                request.Domain,
                ProjectKind.WordPress,
                request.PhpVersion,
                request.Https,
                request.DatabaseEngine,
                databaseName,
                null,
                Array.Empty<string>(),
                ["mailpit"]),
            databaseOptions,
            cancellationToken).ConfigureAwait(false);
        actions.AddRange(result.Actions);
        actions.AddRange(result.Warnings.Select(value => "Warning: " + value));

        var projectRoot = _workspace.ResolveProjectRoot(result.Site.DocumentRoot);
        try
        {
            var php = new ProjectCommandService(_rootPath, _workspace).ResolveTool("php", projectRoot);
            var download = await RunWpCliAsync(
                php,
                projectRoot,
                ["core", "download", $"--locale={request.Locale}", "--force", "--no-color"],
                null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(download, "WordPress core download");
            actions.Add("Downloaded WordPress core through WP-CLI.");

            var configArguments = new List<string>
            {
                "config", "create",
                $"--dbname={databaseName}",
                $"--dbuser={databaseOptions.User}",
                $"--dbhost={databaseOptions.Host}:{databaseOptions.Port}",
                "--skip-check",
                "--force",
                "--no-color"
            };
            string? configInput = null;
            if (string.IsNullOrEmpty(databaseOptions.Password))
            {
                configArguments.Add("--dbpass=");
            }
            else
            {
                configArguments.Add("--prompt=dbpass");
                configInput = databaseOptions.Password + Environment.NewLine;
            }
            var config = await RunWpCliAsync(php, projectRoot, configArguments, configInput, cancellationToken).ConfigureAwait(false);
            EnsureSuccess(config, "WordPress configuration");
            actions.Add("Created wp-config.php without placing the database password on the process command line.");

            var siteUrl = $"{(request.Https ? "https" : "http")}://{result.Site.Domain}";
            var installArguments = new List<string>
            {
                "core", "install",
                $"--url={siteUrl}",
                $"--title={request.SiteTitle}",
                $"--admin_user={request.AdminUser}",
                $"--admin_email={request.AdminEmail}",
                "--skip-email",
                "--prompt=admin_password",
                "--no-color"
            };
            var install = await RunWpCliAsync(
                php,
                projectRoot,
                installArguments,
                request.AdminPassword + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(install, "WordPress installation");
            actions.Add("Installed WordPress and supplied the administrator password over stdin.");

            var marker = Path.Combine(projectRoot, ".devbox-scaffold-pending");
            if (File.Exists(marker))
                File.Delete(marker);

            using var locks = new EnvironmentLockService(_rootPath);
            _ = locks.Generate(projectRoot, "wordpress-full");
            actions.Add("Generated devbox.lock.json for the WordPress environment.");

            return new WordPressSiteResult(
                projectRoot,
                result.Site.Domain,
                databaseName,
                $"{siteUrl}/wp-admin/",
                actions);
        }
        catch (Exception original)
        {
            var cleanupErrors = new List<Exception>();
            try
            {
                _sites.Delete(result.Site.Name, deleteDocumentRoot: true);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }
            if (result.DatabaseCreated)
            {
                try
                {
                    await projectDatabases.DropDatabaseAsync(request.DatabaseEngine, databaseName, databaseOptions, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    cleanupErrors.Add(ex);
                }
            }
            try
            {
                tlsRollback.Restore(tlsState);
            }
            catch (Exception ex)
            {
                cleanupErrors.Add(ex);
            }
            if (cleanupErrors.Count > 0)
            {
                var allErrors = new List<Exception> { original };
                allErrors.AddRange(cleanupErrors);
                throw new AggregateException("WordPress setup failed and cleanup was incomplete.", allErrors);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<string>> GetStatusAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var projectRoot = EnsureProjectRoot(projectPath);
        if (!IsWpCliInstalled)
            throw new FileNotFoundException("WP-CLI is not installed.", _wpCliPath);
        var php = new ProjectCommandService(_rootPath, _workspace).ResolveTool("php", projectRoot);
        var result = await RunWpCliAsync(php, projectRoot, ["core", "version", "--no-color"], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, "WordPress status");
        var lines = new List<string> { $"WordPress {result.StandardOutput.Trim()}" };
        var update = await RunWpCliAsync(php, projectRoot, ["core", "check-update", "--format=csv", "--no-color"], null, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(update, "WordPress update check");
        if (!string.IsNullOrWhiteSpace(update.StandardOutput))
            lines.Add("Core update is available.");
        else
            lines.Add("No core update reported by WP-CLI.");
        return lines;
    }

    private static void ValidateRequest(WordPressSiteRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SiteTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdminUser);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdminPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdminEmail);
        if (request.AdminPassword.Length < 12)
            throw new ArgumentException("WordPress administrator password must contain at least 12 characters.", nameof(request));
        if (!request.AdminEmail.Contains('@') || request.AdminEmail.Any(char.IsWhiteSpace))
            throw new ArgumentException("WordPress administrator email is invalid.", nameof(request));
        if (request.DatabaseEngine.Trim().ToLowerInvariant() is not ("mysql" or "mariadb"))
            throw new ArgumentException("WordPress Toolkit supports MySQL or MariaDB.", nameof(request));
        if (request.Locale.Length > 16 || request.Locale.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '_' and not '-'))
            throw new ArgumentException("WordPress locale is invalid.", nameof(request));
    }

    private string EnsureProjectRoot(string projectPath)
    {
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project directory was not found: {root}");
        return PathSafety.EnsureUnderRootWithoutReparsePoints(
            Path.Combine(_rootPath, "www"),
            root,
            "WordPress Toolkit is restricted to projects in DevBox www and cannot traverse a reparse point.");
    }

    private DatabaseConnectionOptions ResolveDatabaseOptions(string engine)
    {
        using var runtimes = new DatabaseRuntimeService(_rootPath);
        var normalized = engine.Trim().ToLowerInvariant();
        var candidate = runtimes.GetInstances(normalized)
            .OrderByDescending(item => item.State == ServiceState.Running)
            .ThenBy(item => item.Port)
            .FirstOrDefault();
        var fallbackPort = normalized == "mariadb" ? 3316 : 3306;
        return new DatabaseConnectionOptions(Port: candidate?.Port ?? fallbackPort, User: "root", Password: string.Empty);
    }

    private static string SafeDatabaseName(string value)
    {
        var result = new string(value.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_').ToArray()).Trim('_');
        if (string.IsNullOrWhiteSpace(result))
            throw new ArgumentException("Database name cannot be normalized to an empty identifier.", nameof(value));
        return result.Length <= 64 ? result : result[..64];
    }

    private async Task<WpCliResult> RunWpCliAsync(
        string phpExecutable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = phpExecutable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        startInfo.ArgumentList.Add(_wpCliPath);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["WP_CLI_DISABLE_AUTO_CHECK_UPDATE"] = "1";
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start WP-CLI.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to start WP-CLI: {ex.Message}", ex);
        }

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException("WP-CLI operation exceeded the 10 minute timeout.");
            throw;
        }
        return new WpCliResult(process.ExitCode, Truncate(await stdout.ConfigureAwait(false)), Truncate(await stderr.ConfigureAwait(false)));
    }

    private static void EnsureSuccess(WpCliResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{operation} failed: {(string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError).Trim()}");
    }

    private static void VerifySha256(string path, string expectedSha256)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Expected WP-CLI SHA-256 value is invalid.", ex);
        }
        if (expected.Length != 32)
            throw new InvalidDataException("Expected WP-CLI SHA-256 value must be 64 hexadecimal characters.");
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("WP-CLI SHA-256 verification failed.");
    }

    private static string Truncate(string value) => value.Length <= 1_048_576 ? value : value[..1_048_576] + Environment.NewLine + "[output truncated by DevBox]";

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private sealed record WpCliResult(int ExitCode, string StandardOutput, string StandardError);
}
