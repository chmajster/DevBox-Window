using System.IO.Compression;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectSnapshotService
{
    private const long MaximumRestoredBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 250_000;
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly string _snapshotRoot;

    public ProjectSnapshotService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.Combine(_rootPath, "www");
        _snapshotRoot = Path.Combine(_rootPath, "backups", "projects");
    }

    public Task<ProjectSnapshotResult> CreateAsync(
        string projectPath,
        ProjectSnapshotOptions? options = null,
        IReadOnlyList<string>? databaseBackups = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProjectSnapshotOptions();
        var projectRoot = EnsureProjectRoot(projectPath);
        var projectName = Path.GetFileName(projectRoot);
        Directory.CreateDirectory(_snapshotRoot);
        var destination = Path.Combine(_snapshotRoot, $"{SafeFileName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.devbox-snapshot.zip");
        var included = new List<string>();

        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (var file in EnumerateProjectFiles(projectRoot, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                var entry = archive.CreateEntry($"project/{relative}", CompressionLevel.Optimal);
                using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: false);
                using var output = entry.Open();
                input.CopyTo(output);
                included.Add(relative);
            }

            if (options.IncludeDatabase && databaseBackups is not null)
            {
                foreach (var backup in databaseBackups.Where(File.Exists))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var full = Path.GetFullPath(backup);
                    var entry = archive.CreateEntry($"database/{Path.GetFileName(full)}", CompressionLevel.Optimal);
                    using var input = File.OpenRead(full);
                    using var output = entry.Open();
                    input.CopyTo(output);
                }
            }

            var metadata = new
            {
                SchemaVersion = 1,
                ProjectName = projectName,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Options = options,
                DatabaseBackups = databaseBackups?.Where(File.Exists).Select(Path.GetFileName).ToArray() ?? Array.Empty<string>()
            };
            var metadataEntry = archive.CreateEntry("snapshot.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(metadataEntry.Open());
            writer.Write(JsonSerializer.Serialize(metadata, JsonOptions));
        }

        var info = new FileInfo(destination);
        return Task.FromResult(new ProjectSnapshotResult(destination, projectName, info.Length, DateTimeOffset.UtcNow, included));
    }

    public Task<string> RestoreAsync(
        string snapshotPath,
        string destinationProjectName,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(snapshotPath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Project snapshot was not found.", source);
        var safeName = NormalizeProjectName(destinationProjectName);
        var destination = Path.Combine(_wwwRoot, safeName);
        var tempRoot = Path.Combine(_rootPath, "tmp", "snapshot-restore", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(tempRoot, safeName);
        Directory.CreateDirectory(staging);
        string? previous = null;
        try
        {
            ExtractProject(source, staging, cancellationToken);
            if (!File.Exists(Path.Combine(staging, ProjectWorkspaceService.ManifestFileName)))
                throw new InvalidDataException("Snapshot does not contain devbox.json.");

            if (Directory.Exists(destination))
            {
                if (!overwrite)
                    throw new InvalidOperationException($"Project destination already exists: {destination}");
                previous = destination + $".restore-backup-{Guid.NewGuid():N}";
                Directory.Move(destination, previous);
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (previous is not null && Directory.Exists(previous) && !Directory.Exists(destination))
                    Directory.Move(previous, destination);
                throw;
            }

            if (previous is not null)
                TryDeleteDirectory(previous);
            return Task.FromResult(destination);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private IEnumerable<string> EnumerateProjectFiles(string root, ProjectSnapshotOptions options)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var name = Path.GetFileName(child);
                if (!options.IncludeGitDirectory && name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!options.IncludeVendor && name.Equals("vendor", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!options.IncludeNodeModules && name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    continue;
                stack.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    continue;
                yield return file;
            }
        }
    }

    private static void ExtractProject(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Snapshot contains too many entries.");
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.FullName.StartsWith("project/", StringComparison.Ordinal) || entry.FullName.EndsWith('/', StringComparison.Ordinal))
                continue;
            if (entry.FullName.Contains(':', StringComparison.Ordinal) || entry.FullName.Contains("..", StringComparison.Ordinal))
                throw new InvalidDataException("Snapshot contains an unsafe entry path.");
            total = checked(total + entry.Length);
            if (total > MaximumRestoredBytes)
                throw new InvalidDataException("Snapshot exceeds the maximum restored size.");
            var relative = entry.FullName["project/".Length..].Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Snapshot entry escapes the destination directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private string EnsureProjectRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project directory was not found: {root}");
        var www = Path.GetFullPath(_wwwRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(www, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Project snapshots are restricted to the DevBox www directory.");
        return root;
    }

    private static string NormalizeProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 80 || trimmed is "." or ".." || trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Project name contains unsupported path characters.", nameof(value));
        return trimmed;
    }

    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
