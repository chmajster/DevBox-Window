using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectTransferService
{
    private const long MaximumImportBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumImportEntries = 250_000;
    private readonly string _rootPath;
    private readonly string _wwwRoot;
    private readonly string _exportRoot;
    private readonly SiteManager _sites;
    private readonly ProjectWorkspaceService _workspace;

    public ProjectTransferService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _wwwRoot = Path.Combine(_rootPath, "www");
        _exportRoot = Path.Combine(_rootPath, "backups", "exports");
        _sites = new SiteManager(_rootPath);
        _workspace = new ProjectWorkspaceService(_rootPath, _sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
    }

    public Task<ProjectTransferResult> ExportAsync(
        string projectPath,
        ProjectSnapshotOptions? options = null,
        IReadOnlyList<string>? databaseBackups = null,
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProjectSnapshotOptions();
        var root = EnsureProjectRoot(projectPath);
        var projectName = Path.GetFileName(root);
        var manifestObject = ReadManifest(root);
        var domain = GetString(manifestObject, "Domain") ?? $"{projectName}.test";
        var dbFiles = databaseBackups?.Where(File.Exists).Select(Path.GetFullPath).ToArray() ?? Array.Empty<string>();
        Directory.CreateDirectory(_exportRoot);
        var destination = string.IsNullOrWhiteSpace(destinationPath)
            ? Path.Combine(_exportRoot, $"{SafeFileName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.devbox-project.zip")
            : Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var transferManifest = new ProjectTransferManifest
        {
            ProjectName = projectName,
            Domain = domain,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            ProjectDirectory = "project",
            EnvironmentLockFile = File.Exists(Path.Combine(root, EnvironmentLockService.LockFileName)) ? EnvironmentLockService.LockFileName : null,
            DatabaseBackups = dbFiles.Select(Path.GetFileName).ToArray()
        };

        using (var stream = new FileStream(destination, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var file in EnumerateProjectFiles(root, options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                AddFile(archive, file, $"project/{relative}");
            }
            if (options.IncludeDatabase)
            {
                foreach (var backup in dbFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AddFile(archive, backup, $"database/{Path.GetFileName(backup)}");
                }
            }
            var entry = archive.CreateEntry("transfer.json", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(JsonSerializer.Serialize(transferManifest, JsonOptions));
        }

        return Task.FromResult(new ProjectTransferResult(destination, transferManifest, new FileInfo(destination).Length));
    }

    public Task<string> ImportAsync(
        string archivePath,
        string? targetProjectName = null,
        string? targetDomain = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(archivePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("DevBox project archive was not found.", source);
        var tempRoot = Path.Combine(_rootPath, "tmp", "project-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            ExtractSafely(source, tempRoot, cancellationToken);
            var transferPath = Path.Combine(tempRoot, "transfer.json");
            if (!File.Exists(transferPath))
                throw new InvalidDataException("Project archive does not contain transfer.json.");
            ProjectTransferManifest transfer;
            try
            {
                transfer = JsonSerializer.Deserialize<ProjectTransferManifest>(File.ReadAllText(transferPath), JsonOptions)
                    ?? throw new InvalidDataException("transfer.json is empty.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("transfer.json contains invalid JSON.", ex);
            }
            if (transfer.SchemaVersion != ProjectTransferManifest.CurrentSchemaVersion)
                throw new InvalidDataException($"Unsupported project transfer schema version: {transfer.SchemaVersion}.");

            var name = NormalizeProjectName(targetProjectName ?? transfer.ProjectName);
            var domain = NormalizeDomain(targetDomain ?? (name.Equals(transfer.ProjectName, StringComparison.OrdinalIgnoreCase) ? transfer.Domain : $"{name}.test"));
            var stagedProject = Path.Combine(tempRoot, "project");
            if (!Directory.Exists(stagedProject) || !File.Exists(Path.Combine(stagedProject, ProjectWorkspaceService.ManifestFileName)))
                throw new InvalidDataException("Project archive does not contain a valid project/devbox.json payload.");

            UpdateManifestIdentity(stagedProject, name, domain);
            var destination = Path.Combine(_wwwRoot, name);
            string? previous = null;
            if (Directory.Exists(destination))
            {
                if (!overwrite)
                    throw new InvalidOperationException($"Project destination already exists: {destination}");
                previous = destination + $".import-backup-{Guid.NewGuid():N}";
                Directory.Move(destination, previous);
            }

            try
            {
                Directory.Move(stagedProject, destination);
                RegisterImportedSite(destination, name, domain);
                MoveDatabaseBackups(tempRoot, name);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (previous is not null && Directory.Exists(previous))
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

    private void RegisterImportedSite(string projectRoot, string name, string domain)
    {
        var detection = _workspace.Detect(projectRoot);
        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(projectRoot, "public"))
            ? Path.Combine(projectRoot, "public")
            : projectRoot;
        var manifest = ReadManifest(projectRoot);
        var existing = _sites.GetSites().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var site = existing ?? _sites.Create(name, domain, documentRoot);
        var phpVersion = GetString(manifest, "PhpVersion");
        if (!string.IsNullOrWhiteSpace(phpVersion))
            site = _sites.SetPhpVersion(site.Name, phpVersion);
        if (GetBool(manifest, "Https") == true)
        {
            if (OperatingSystem.IsWindows())
                using (new LocalCertificateAuthorityService(_rootPath).IssueSiteCertificate(domain)) { }
            else
                _ = new LocalCertificateManager(_rootPath).Ensure(domain);
            _ = _sites.SetHttps(site.Name, true);
        }
    }

    private void MoveDatabaseBackups(string tempRoot, string projectName)
    {
        var source = Path.Combine(tempRoot, "database");
        if (!Directory.Exists(source))
            return;
        var destination = Path.Combine(_rootPath, "backups", "imports", projectName);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target))
                target = Path.Combine(destination, $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}{Path.GetExtension(file)}");
            File.Move(file, target);
        }
    }

    private static void UpdateManifestIdentity(string projectRoot, string name, string domain)
    {
        var path = Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName);
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid JSON.", ex);
        }
        SetProperty(manifest, "Name", name);
        SetProperty(manifest, "Domain", domain);
        AtomicWrite(path, manifest.ToJsonString(JsonOptions));

        var lockPath = Path.Combine(projectRoot, EnvironmentLockService.LockFileName);
        if (File.Exists(lockPath))
        {
            try
            {
                var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(File.ReadAllText(lockPath), JsonOptions);
                if (lockFile is not null)
                    AtomicWrite(lockPath, JsonSerializer.Serialize(lockFile with { ProjectName = name, Domain = domain, GeneratedAtUtc = DateTimeOffset.UtcNow }, JsonOptions));
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Imported devbox.lock.json contains invalid JSON.", ex);
            }
        }
    }

    private static void ExtractSafely(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumImportEntries)
            throw new InvalidDataException("Project archive contains too many entries.");
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/', StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal) || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("Project archive contains an unsafe entry path.");
            total = checked(total + Math.Max(0, entry.Length));
            if (total > MaximumImportBytes)
                throw new InvalidDataException("Project archive exceeds the maximum extracted size.");
            if (normalized.EndsWith('/', StringComparison.Ordinal))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Project archive entry escapes the extraction directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static IEnumerable<string> EnumerateProjectFiles(string root, ProjectSnapshotOptions options)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var directory = stack.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
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
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    yield return file;
            }
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
            throw new InvalidOperationException("Project transfer is restricted to the DevBox www directory.");
        return root;
    }

    private static JsonObject ReadManifest(string projectRoot)
    {
        var path = Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("Project does not contain devbox.json.", path);
        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                ?? throw new InvalidDataException("devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("devbox.json contains invalid JSON.", ex);
        }
    }

    private static JsonNode? FindProperty(JsonObject value, string name)
    {
        foreach (var pair in value)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    private static string? GetString(JsonObject value, string name) => FindProperty(value, name) is JsonValue node && node.TryGetValue<string>(out var text) ? text : null;
    private static bool? GetBool(JsonObject value, string name) => FindProperty(value, name) is JsonValue node && node.TryGetValue<bool>(out var flag) ? flag : null;

    private static void SetProperty(JsonObject value, string name, object? propertyValue)
    {
        var existing = value.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        value[string.IsNullOrEmpty(existing) ? name : existing] = JsonValue.Create(propertyValue);
    }

    private static string NormalizeProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 80 || trimmed is "." or ".." || trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Project name contains unsupported characters.", nameof(value));
        return trimmed;
    }

    private static string NormalizeDomain(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (!normalized.EndsWith(".test", StringComparison.Ordinal) || normalized.Length > 253 || normalized.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '.'))
            throw new ArgumentException("Project domain must be a valid .test domain.", nameof(value));
        return normalized;
    }

    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());

    private static void AddFile(ZipArchive archive, string source, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var input = File.OpenRead(source);
        using var output = entry.Open();
        input.CopyTo(output);
    }

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

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
