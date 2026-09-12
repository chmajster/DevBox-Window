using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectActionService
{
    private static readonly HashSet<string> AllowedTools = new(StringComparer.OrdinalIgnoreCase) { "php", "composer", "npm", "pnpm" };
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly ProjectWorkspaceService _workspace;
    private readonly ProjectCommandService _commands;

    public ProjectActionService(string rootPath, ProjectWorkspaceService workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.Combine(_rootPath, "www");
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _commands = new ProjectCommandService(_rootPath, _workspace);
    }

    public IReadOnlyList<ProjectActionDefinition> GetActions(string projectPath)
    {
        var root = EnsureProjectRoot(projectPath);
        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);
        if (!File.Exists(manifestPath))
            return Array.Empty<ProjectActionDefinition>();

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (node is null)
                return Array.Empty<ProjectActionDefinition>();
            var actionsNode = FindProperty(node, "actions");
            if (actionsNode is null)
                return Array.Empty<ProjectActionDefinition>();
            var actions = actionsNode.Deserialize<List<ProjectActionDefinition?>>(JsonOptions) ?? new List<ProjectActionDefinition?>();
            ValidateActions(actions);
            return actions.Select(item => item!).Where(item => item.Enabled).ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid project actions.", ex);
        }
    }

    public void SetActions(string projectPath, IReadOnlyList<ProjectActionDefinition> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var root = EnsureProjectRoot(projectPath);
        var manifestPath = Path.Combine(root, ProjectWorkspaceService.ManifestFileName);
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("devbox.json is required before project actions can be configured.", manifestPath);
        ValidateActions(actions);

        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject
                ?? throw new InvalidDataException("devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid JSON.", ex);
        }

        manifest["Actions"] = JsonSerializer.SerializeToNode(actions, JsonOptions);
        AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));
    }

    public async Task<ProjectActionResult> RunAsync(string projectPath, string actionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKey);
        var root = EnsureProjectRoot(projectPath);
        var action = GetActions(root).FirstOrDefault(item => item.Key.Equals(actionKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Project action '{actionKey}' is not defined or enabled.");
        ValidateAction(action);

        var executable = _commands.ResolveTool(action.Executable.ToLowerInvariant(), root);
        var workingDirectory = ResolveWorkingDirectory(root, action.WorkingDirectory);
        var startInfo = DeveloperToolsService.BuildStartInfo(executable, action.Arguments);
        startInfo.WorkingDirectory = workingDirectory;

        using var process = new Process { StartInfo = startInfo };
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Unable to start project action '{action.DisplayName}'.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Unable to start project action '{action.DisplayName}': {ex.Message}", ex);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(action.TimeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Project action '{action.DisplayName}' exceeded its {action.TimeoutSeconds} second timeout.");
            throw;
        }

        var stdout = Truncate(await stdoutTask.ConfigureAwait(false));
        var stderr = Truncate(await stderrTask.ConfigureAwait(false));
        return new ProjectActionResult(action.Key, process.ExitCode, Stopwatch.GetElapsedTime(started), stdout.Trim(), stderr.Trim());
    }

    public async Task<IReadOnlyList<ProjectActionResult>> RunAllAsync(string projectPath, bool stopOnFailure = true, CancellationToken cancellationToken = default)
    {
        var results = new List<ProjectActionResult>();
        foreach (var action in GetActions(projectPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunAsync(projectPath, action.Key, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            if (stopOnFailure && !result.Succeeded)
                break;
        }
        return results;
    }

    private static void ValidateActions(IEnumerable<ProjectActionDefinition?> actions)
    {
        var materialized = actions.ToArray();
        foreach (var action in materialized)
            ValidateAction(action);
        var duplicate = materialized
            .Where(action => action is not null)
            .GroupBy(action => action!.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Project actions contain duplicate key '{duplicate.Key}'.");
    }

    private static void ValidateAction(ProjectActionDefinition? action)
    {
        if (action is null)
            throw new InvalidDataException("Project actions cannot contain null entries.");
        if (string.IsNullOrWhiteSpace(action.Key) || action.Key.Length > 64 || action.Key.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not '.'))
            throw new InvalidDataException("Project action key contains unsupported characters.");
        if (string.IsNullOrWhiteSpace(action.DisplayName) || action.DisplayName.Length > 120)
            throw new InvalidDataException($"Project action '{action.Key}' display name is invalid.");
        if (!AllowedTools.Contains(action.Executable ?? string.Empty))
            throw new InvalidDataException($"Project action '{action.Key}' uses unsupported tool '{action.Executable}'. Allowed tools: {string.Join(", ", AllowedTools.OrderBy(value => value))}.");
        if (action.Arguments is null || action.Arguments.Count > 64 || action.Arguments.Any(value => value is null || value.Length > 2048 || value.Contains('\0')))
            throw new InvalidDataException($"Project action '{action.Key}' contains invalid arguments.");
        if (action.TimeoutSeconds is < 1 or > 3600)
            throw new InvalidDataException($"Project action '{action.Key}' timeout must be between 1 and 3600 seconds.");
        if (!string.IsNullOrWhiteSpace(action.WorkingDirectory) && Path.IsPathRooted(action.WorkingDirectory))
            throw new InvalidDataException($"Project action '{action.Key}' working directory must be relative to the project root.");
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
            "Project actions are restricted to the DevBox www directory and cannot traverse a reparse point.");
    }

    private static string ResolveWorkingDirectory(string projectRoot, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
            return projectRoot;
        var path = Path.GetFullPath(Path.Combine(projectRoot, relative));
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Project action working directory does not exist: {path}");
        if (path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return projectRoot;
        return PathSafety.EnsureUnderRootWithoutReparsePoints(
            projectRoot,
            path,
            "Project action working directory escapes the project root or traverses a reparse point.");
    }

    private static JsonNode? FindProperty(JsonObject value, string name)
    {
        foreach (var pair in value)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }
        return null;
    }

    private static string Truncate(string value) => value.Length <= 1_048_576 ? value : value[..1_048_576] + Environment.NewLine + "[output truncated by DevBox]";

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
