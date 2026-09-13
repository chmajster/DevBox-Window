using System.ComponentModel;
using System.Diagnostics;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectCommandService
{
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly ProjectWorkspaceService _workspaceService;

    public ProjectCommandService(string rootPath, ProjectWorkspaceService workspaceService)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www"));
        _workspaceService = workspaceService ?? throw new ArgumentNullException(nameof(workspaceService));
    }

    public IReadOnlyList<ProjectCommandPreset> GetPresets(string projectPath)
    {
        var root = EnsureProjectRoot(projectPath);
        var detection = _workspaceService.Detect(root);
        var presets = new List<ProjectCommandPreset>();

        if (File.Exists(Path.Combine(root, "composer.json")))
        {
            presets.Add(new("composer-install", "Composer install", "composer", ["install"], "Install locked PHP dependencies."));
            presets.Add(new("composer-dump-autoload", "Composer dump-autoload", "composer", ["dump-autoload"], "Regenerate Composer autoload files."));
        }

        if (File.Exists(Path.Combine(root, "package.json")))
        {
            presets.Add(new("npm-install", "npm install", "npm", ["install"], "Install Node.js dependencies."));
            presets.Add(new("npm-build", "npm run build", "npm", ["run", "build"], "Run the project's build script."));
            presets.Add(new("npm-test", "npm test", "npm", ["test"], "Run the project's test script."));
        }

        switch (detection.Kind)
        {
            case ProjectKind.Laravel:
                presets.Add(new("laravel-migrate", "Laravel migrate", "php", ["artisan", "migrate", "--no-interaction"], "Run pending Laravel database migrations."));
                presets.Add(new("laravel-optimize-clear", "Laravel optimize:clear", "php", ["artisan", "optimize:clear"], "Clear Laravel framework caches."));
                presets.Add(new("laravel-storage-link", "Laravel storage:link", "php", ["artisan", "storage:link"], "Create the Laravel public storage link."));
                presets.Add(new("laravel-test", "Laravel tests", "php", ["artisan", "test"], "Run the Laravel test suite."));
                break;
            case ProjectKind.Symfony:
                presets.Add(new("symfony-cache-clear", "Symfony cache:clear", "php", ["bin/console", "cache:clear", "--no-interaction"], "Clear the Symfony application cache."));
                presets.Add(new("symfony-migrations", "Symfony migrations", "php", ["bin/console", "doctrine:migrations:migrate", "--no-interaction"], "Run Doctrine database migrations."));
                break;
        }

        return presets;
    }

    public async Task<ProjectCommandResult> RunAsync(
        string projectPath,
        string presetKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetKey);
        var root = EnsureProjectRoot(projectPath);
        var preset = GetPresets(root).FirstOrDefault(item => item.Key.Equals(presetKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Project command preset '{presetKey}' is not available for this project.");
        var executable = ResolveTool(preset.Tool, root);
        var startInfo = DeveloperToolsService.BuildStartInfo(executable, preset.Arguments);
        startInfo.WorkingDirectory = root;

        using var process = new Process { StartInfo = startInfo };
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to start project command '{preset.DisplayName}': {ex.Message}", ex);
        }

        var outputTask = ReadBoundedAsync(process.StandardOutput, 1_048_576, cancellationToken);
        var errorTask = ReadBoundedAsync(process.StandardError, 1_048_576, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Project command '{preset.DisplayName}' exceeded the 15 minute limit.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        return new ProjectCommandResult(preset.Key, process.ExitCode, output.Trim(), error.Trim(), duration);
    }

    internal string ResolveTool(string tool, string projectRoot)
    {
        var manifest = _workspaceService.LoadManifest(projectRoot);
        return tool switch
        {
            "php" => ResolvePhp(manifest),
            "composer" => DeveloperToolsService.ResolveCommand([
                Path.Combine(_rootPath, "tools", "composer", "composer.cmd"),
                "composer.cmd", "composer.bat", "composer.exe"
            ]) ?? throw new FileNotFoundException("Composer is not available."),
            "npm" => ResolveNpm(manifest),
            "pnpm" => DeveloperToolsService.ResolveCommand(["pnpm.cmd", "pnpm.exe"])
                ?? throw new FileNotFoundException("pnpm is not available."),
            _ => throw new InvalidOperationException($"Unsupported project command tool '{tool}'.")
        };
    }

    private string ResolvePhp(DevBoxProjectManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.PhpVersion))
        {
            ValidateRuntimeVersion(manifest.PhpVersion);
            return RequireFile(
                Path.Combine(_rootPath, "runtime", "php", manifest.PhpVersion, "php.exe"),
                $"PHP {manifest.PhpVersion} is pinned by devbox.json but is not installed.");
        }

        return RequireFile(Path.Combine(_rootPath, "runtime", "php", "current", "php.exe"), "Active PHP CLI runtime is not installed.");
    }

    private string ResolveNpm(DevBoxProjectManifest? manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.NodeVersion))
        {
            ValidateRuntimeVersion(manifest.NodeVersion);
            return RequireFile(
                Path.Combine(_rootPath, "runtime", "node", manifest.NodeVersion, "npm.cmd"),
                $"Node.js {manifest.NodeVersion} is pinned by devbox.json but is not installed.");
        }

        return DeveloperToolsService.ResolveCommand(["npm.cmd", "npm.exe"])
            ?? throw new FileNotFoundException("npm is not available.");
    }

    private static void ValidateRuntimeVersion(string version)
    {
        if (version.Length > 64 || version is "." or ".." || version.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new InvalidDataException("Project runtime version contains unsupported path characters.");
    }

    private string EnsureProjectRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project directory was not found: {root}");

        return PathSafety.EnsureUnderRootWithoutReparsePoints(
            _wwwRoot,
            root,
            "Project commands are restricted to projects inside the DevBox www directory and cannot traverse a reparse point.");
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var buffer = new char[8192];
        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 64 * 1024));
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
                builder.Append(buffer, 0, Math.Min(remaining, read));
            if (read > remaining)
                truncated = true;
        }
        if (truncated)
            builder.Append(Environment.NewLine).Append("[output truncated by DevBox]");
        return builder.ToString();
    }

    private static string RequireFile(string path, string message) =>
        File.Exists(path) ? path : throw new FileNotFoundException(message, path);

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
}
