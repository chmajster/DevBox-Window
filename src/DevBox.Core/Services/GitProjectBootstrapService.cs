using System.ComponentModel;
using System.Diagnostics;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class GitProjectBootstrapService
{
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly SiteManager _sites;
    private readonly ProjectWorkspaceService _workspace;

    public GitProjectBootstrapService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.Combine(_rootPath, "www");
        _sites = new SiteManager(_rootPath);
        _workspace = new ProjectWorkspaceService(_rootPath, _sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
    }

    public async Task<GitBootstrapResult> BootstrapAsync(GitBootstrapRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var repository = ValidateRepositoryUrl(request.RepositoryUrl);
        var projectName = NormalizeProjectName(request.ProjectName);
        var branch = NormalizeBranch(request.Branch);
        var domain = NormalizeDomain(request.Domain ?? $"{projectName}.test");
        var tlsRollback = new TlsRollbackStateService(_rootPath);
        var tlsState = tlsRollback.Capture(domain);
        Directory.CreateDirectory(_wwwRoot);
        var destination = Path.Combine(_wwwRoot, projectName);
        var destinationExisted = Directory.Exists(destination);
        if (destinationExisted && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new InvalidOperationException($"Project destination is not empty: {destination}");

        var git = DeveloperToolsService.ResolveCommand(["git.exe", "git.cmd", "git"])
            ?? throw new FileNotFoundException("Git is not available in PATH. Install Git for Windows before cloning projects.");
        var tempRoot = Path.Combine(_rootPath, "tmp", "git-bootstrap", Guid.NewGuid().ToString("N"));
        var cloneRoot = Path.Combine(tempRoot, "repository");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var args = new List<string> { "clone", "--depth", "1", "--no-tags" };
            if (branch is not null)
            {
                args.Add("--branch");
                args.Add(branch);
                args.Add("--single-branch");
            }
            args.Add(repository.AbsoluteUri);
            args.Add(cloneRoot);
            var clone = await RunGitAsync(git, args, tempRoot, cancellationToken).ConfigureAwait(false);
            if (clone.ExitCode != 0)
                throw new InvalidOperationException($"Git clone failed: {TrimOutput(clone.StandardError)}");

            var detection = _workspace.Detect(cloneRoot);
            if (detection.Kind == ProjectKind.Node)
                throw new NotSupportedException("Node-only repositories are not yet registered as Nginx/PHP Sites. Use a PHP/WordPress/Laravel/Symfony repository or import the Node project manually.");

            var imported = _workspace.Import(new ProjectImportRequest(
                cloneRoot,
                projectName,
                domain,
                null,
                false,
                CopyIntoDevBox: true));
            var projectRoot = _workspace.ResolveProjectRoot(imported.DocumentRoot);

            EnvironmentApplyResult? environment = null;
            using (var locks = new EnvironmentLockService(_rootPath))
            {
                if (!string.IsNullOrWhiteSpace(request.ProfileKey))
                {
                    environment = await locks.ApplyProfileAsync(projectRoot, request.ProfileKey, cancellationToken).ConfigureAwait(false);
                    if (environment.Warnings.Count > 0)
                        throw new InvalidOperationException("Environment profile application was incomplete: " + string.Join(" | ", environment.Warnings));
                }
                else
                {
                    _ = locks.Generate(projectRoot);
                }
            }

            IReadOnlyList<ProjectActionResult> actions = Array.Empty<ProjectActionResult>();
            if (request.RunBootstrapActions)
            {
                var actionService = new ProjectActionService(_rootPath, _workspace);
                if (actionService.GetActions(projectRoot).Count > 0)
                    actions = await actionService.RunAllAsync(projectRoot, stopOnFailure: true, cancellationToken).ConfigureAwait(false);
                else
                    actions = await RunSafeDetectedBootstrapAsync(projectRoot, detection.Kind, cancellationToken).ConfigureAwait(false);
            }

            var failedAction = actions.FirstOrDefault(action => !action.Succeeded);
            if (failedAction is not null)
                throw new InvalidOperationException($"Bootstrap action '{failedAction.Key}' failed with exit code {failedAction.ExitCode}: {TrimOutput(failedAction.StandardError)}");

            return new GitBootstrapResult(projectRoot, detection.Kind, environment, actions);
        }
        catch
        {
            if (Directory.Exists(destination))
            {
                try
                {
                    var site = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase));
                    if (site is not null)
                        _sites.Delete(site.Name);
                }
                catch (Exception) { }
                TryRollbackDestination(destination, destinationExisted);
            }
            try { tlsRollback.Restore(tlsState); }
            catch (Exception rollbackError)
            {
                throw new AggregateException("Git bootstrap failed and TLS rollback was incomplete.", rollbackError);
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private async Task<IReadOnlyList<ProjectActionResult>> RunSafeDetectedBootstrapAsync(string projectRoot, ProjectKind kind, CancellationToken cancellationToken)
    {
        var results = new List<ProjectActionResult>();
        var commands = new ProjectCommandService(_rootPath, _workspace);
        var available = commands.GetPresets(projectRoot).Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var preset in new[] { "composer-install", "npm-install" })
        {
            if (!available.Contains(preset))
                continue;
            var result = await commands.RunAsync(projectRoot, preset, cancellationToken).ConfigureAwait(false);
            results.Add(new ProjectActionResult(result.PresetKey, result.ExitCode, result.Duration, result.StandardOutput, result.StandardError));
            if (result.ExitCode != 0)
                break;
        }
        return results;
    }

    private static Uri ValidateRepositoryUrl(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Git repository URL must be an absolute HTTPS URL.", nameof(value));
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Git repository URL must not embed credentials.", nameof(value));
        if (uri.IsLoopback)
            throw new ArgumentException("Loopback Git repository URLs are not allowed by the remote clone workflow.", nameof(value));
        return uri;
    }

    private static string NormalizeProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length > 80 || normalized is "." or ".." || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Project name contains unsupported characters.", nameof(value));
        return normalized;
    }

    private static string? NormalizeBranch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        if (normalized.Length > 200 || normalized.StartsWith('-') || normalized.Contains("..", StringComparison.Ordinal) || normalized.Contains("@{", StringComparison.Ordinal) || normalized.Any(char.IsWhiteSpace) || normalized.Any(ch => char.IsControl(ch)))
            throw new ArgumentException("Git branch name contains unsafe characters.", nameof(value));
        return normalized;
    }

    private static string NormalizeDomain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (!normalized.EndsWith(".test", StringComparison.Ordinal) || normalized.Length > 253 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '.'))
            throw new ArgumentException("Project domain must be a normalized .test domain.", nameof(value));
        return normalized;
    }

    private static async Task<ProcessResult> RunGitAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = DeveloperToolsService.BuildStartInfo(executable, arguments);
        startInfo.WorkingDirectory = workingDirectory;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start Git.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to start Git: {ex.Message}", ex);
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
                throw new TimeoutException("Git operation exceeded the 10 minute timeout.");
            throw;
        }
        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static string TrimOutput(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 2000 ? normalized : normalized[..2000];
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static void TryRollbackDestination(string path, bool existedBefore)
    {
        try
        {
            if (!Directory.Exists(path))
                return;
            if (!existedBefore)
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            foreach (var file in Directory.EnumerateFiles(path))
                File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(path))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
