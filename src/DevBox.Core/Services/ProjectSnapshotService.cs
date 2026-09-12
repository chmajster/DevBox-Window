using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProjectSnapshotService
{
    private const long MaximumRestoredBytes = 8L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 250_000;
    private const long MaximumMetadataBytes = 1024 * 1024;
    private const long CompressionRatioCheckThreshold = 1024 * 1024;
    private const double MaximumCompressionRatio = 200d;
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

    public async Task<ProjectSnapshotResult> CreateAsync(
        string projectPath,
        ProjectSnapshotOptions? options = null,
        IReadOnlyList<string>? databaseBackups = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ProjectSnapshotOptions();
        var projectRoot = EnsureProjectRoot(projectPath);
        var projectName = Path.GetFileName(projectRoot);
        var dbFiles = options.IncludeDatabase && databaseBackups is not null
            ? databaseBackups.Where(File.Exists).Select(Path.GetFullPath).ToArray()
            : Array.Empty<string>();
        var duplicateBackup = dbFiles
            .GroupBy(value => Path.GetFileName(value), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateBackup is not null)
            throw new ArgumentException($"Database backup list contains duplicate file name '{duplicateBackup.Key}'.", nameof(databaseBackups));

        Directory.CreateDirectory(_snapshotRoot);
        var destination = Path.Combine(_snapshotRoot, $"{SafeFileName(projectName)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.devbox-snapshot.zip");
        var temporaryDestination = destination + $".{Guid.NewGuid():N}.tmp";
        var included = new List<string>();
        var createdAt = DateTimeOffset.UtcNow;

        try
        {
            await using (var stream = new FileStream(temporaryDestination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var file in EnumerateProjectFiles(projectRoot, options))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
                    var entry = archive.CreateEntry($"project/{relative}", CompressionLevel.Optimal);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                    included.Add(relative);
                }

                foreach (var backup in dbFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry($"database/{Path.GetFileName(backup)}", CompressionLevel.Optimal);
                    await using var input = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
                }

                var metadata = new
                {
                    SchemaVersion = 1,
                    ProjectName = projectName,
                    CreatedAtUtc = createdAt,
                    Options = options,
                    DatabaseBackups = dbFiles.Select(value => Path.GetFileName(value)!).ToArray()
                };
                var metadataEntry = archive.CreateEntry("snapshot.json", CompressionLevel.Optimal);
                using var writer = new StreamWriter(metadataEntry.Open());
                await writer.WriteAsync(JsonSerializer.Serialize(metadata, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryDestination, destination);
            var info = new FileInfo(destination);
            return new ProjectSnapshotResult(destination, projectName, info.Length, createdAt, included);
        }
        finally
        {
            TryDeleteFile(temporaryDestination);
        }
    }

    public async Task<string> RestoreAsync(
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
        var staging = Path.Combine(tempRoot, "project");
        var databaseStaging = Path.Combine(tempRoot, "database");
        Directory.CreateDirectory(staging);
        var sites = new SiteManager(_rootPath);
        var previousSite = sites.GetSites().FirstOrDefault(item => item.Name.Equals(safeName, StringComparison.OrdinalIgnoreCase));
        string? previous = null;
        string? databaseDestination = null;
        string? databasePrevious = null;
        var databaseDestinationOwned = false;
        try
        {
            await ExtractSnapshotAsync(source, staging, databaseStaging, cancellationToken).ConfigureAwait(false);
            var manifestPath = Path.Combine(staging, ProjectWorkspaceService.ManifestFileName);
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("Snapshot does not contain devbox.json.");

            RewriteIdentity(staging, safeName);
            JsonObject restoredManifest;
            try
            {
                restoredManifest = JsonNode.Parse(ReadMetadataText(manifestPath, "devbox.json")) as JsonObject
                    ?? throw new InvalidDataException("Restored devbox.json must contain a JSON object.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Restored devbox.json contains invalid JSON.", ex);
            }
            var restoredDomain = GetString(restoredManifest, "Domain") ?? BuildDomain(safeName);
            var tlsRollback = new TlsRollbackStateService(_rootPath);
            var tlsStates = CaptureTlsStates(tlsRollback, previousSite?.Domain, restoredDomain);

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
                SynchronizeSite(destination, safeName, sites);
                if (Directory.Exists(databaseStaging) && Directory.EnumerateFiles(databaseStaging).Any())
                {
                    var snapshotKey = SafeFileName(Path.GetFileNameWithoutExtension(source));
                    databaseDestination = Path.Combine(_rootPath, "backups", "snapshot-restores", safeName, snapshotKey);
                    if (Directory.Exists(databaseDestination))
                    {
                        if (!overwrite)
                            throw new InvalidOperationException($"Snapshot database backup destination already exists: {databaseDestination}");
                        databasePrevious = databaseDestination + $".restore-backup-{Guid.NewGuid():N}";
                        Directory.Move(databaseDestination, databasePrevious);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination)!);
                    Directory.Move(databaseStaging, databaseDestination);
                    databaseDestinationOwned = true;
                }
            }
            catch (Exception original)
            {
                var rollbackActions = new List<Action>
                {
                    () => TryDeleteDirectory(destination),
                    () =>
                    {
                        if (previous is not null && Directory.Exists(previous) && !Directory.Exists(destination))
                            Directory.Move(previous, destination);
                    },
                    () => RestoreSite(sites, safeName, previousSite)
                };

                foreach (var tlsState in tlsStates.Reverse())
                {
                    var state = tlsState;
                    rollbackActions.Add(() => tlsRollback.Restore(state));
                }

                if (databaseDestinationOwned && databaseDestination is not null)
                {
                    var databasePath = databaseDestination;
                    rollbackActions.Add(() => TryDeleteDirectory(databasePath));
                }
                if (databasePrevious is not null)
                {
                    var previousDatabasePath = databasePrevious;
                    var databasePath = databaseDestination!;
                    rollbackActions.Add(() =>
                    {
                        if (Directory.Exists(previousDatabasePath) && !Directory.Exists(databasePath))
                            Directory.Move(previousDatabasePath, databasePath);
                    });
                }

                RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());
                throw new InvalidOperationException("Rollback executor returned unexpectedly.");
            }

            if (previous is not null)
                TryDeleteDirectory(previous);
            if (databasePrevious is not null)
                TryDeleteDirectory(databasePrevious);
            return destination;
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private void RewriteIdentity(string projectRoot, string projectName)
    {
        var manifestPath = Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName);
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(ReadMetadataText(manifestPath, "devbox.json")) as JsonObject
                ?? throw new InvalidDataException("devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Snapshot devbox.json contains invalid JSON.", ex);
        }

        var originalName = GetString(manifest, "Name") ?? Path.GetFileName(projectRoot);
        var originalDomain = GetString(manifest, "Domain");
        var domain = projectName.Equals(originalName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(originalDomain)
            ? originalDomain!
            : BuildDomain(projectName);
        SetProperty(manifest, "Name", projectName);
        SetProperty(manifest, "Domain", domain);
        AtomicWrite(manifestPath, manifest.ToJsonString(JsonOptions));

        var lockPath = Path.Combine(projectRoot, EnvironmentLockService.LockFileName);
        if (!File.Exists(lockPath))
            return;
        try
        {
            var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions)
                ?? throw new InvalidDataException("Snapshot devbox.lock.json is empty.");
            AtomicWrite(lockPath, JsonSerializer.Serialize(lockFile with
            {
                ProjectName = projectName,
                Domain = domain,
                GeneratedAtUtc = DateTimeOffset.UtcNow
            }, JsonOptions));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Snapshot devbox.lock.json contains invalid JSON.", ex);
        }
    }

    private void SynchronizeSite(string projectRoot, string name, SiteManager sites)
    {
        JsonObject manifest;
        try
        {
            manifest = JsonNode.Parse(ReadMetadataText(Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName), "devbox.json")) as JsonObject
                ?? throw new InvalidDataException("Restored devbox.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Restored devbox.json contains invalid JSON.", ex);
        }

        var domain = GetString(manifest, "Domain") ?? BuildDomain(name);
        var phpVersion = GetString(manifest, "PhpVersion");
        var https = GetBool(manifest, "Https") ?? false;
        var workspace = new ProjectWorkspaceService(_rootPath, sites, new PhpExtensionInspector(_rootPath), new LocalCertificateManager(_rootPath));
        var detection = workspace.Detect(projectRoot);
        var documentRoot = detection.Kind is ProjectKind.Laravel or ProjectKind.Symfony && Directory.Exists(Path.Combine(projectRoot, "public"))
            ? Path.Combine(projectRoot, "public")
            : projectRoot;

        var desired = new SiteDefinition(name, domain, documentRoot, "php", phpVersion, https);
        var existing = sites.GetSites().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            _ = sites.Create(name, domain, documentRoot);
        _ = sites.Update(desired);

        if (existing is not null && !existing.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase))
            DeleteCertificateFiles(existing.Domain);

        if (https)
        {
            if (OperatingSystem.IsWindows())
            {
                using var certificate = new LocalCertificateAuthorityService(_rootPath).IssueSiteCertificate(domain);
            }
            else
            {
                _ = new LocalCertificateManager(_rootPath).Ensure(domain);
            }
        }
        else
        {
            DeleteCertificateFiles(domain);
        }
    }

    private static void RestoreSite(SiteManager sites, string name, SiteDefinition? previous)
    {
        var current = sites.GetSites().FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (previous is null)
        {
            if (current is not null)
                sites.Delete(name, deleteDocumentRoot: false);
            return;
        }

        if (current is null)
            _ = sites.Create(previous.Name, previous.Domain, previous.DocumentRoot);
        _ = sites.Update(previous);
    }

    private static IReadOnlyList<TlsRollbackState> CaptureTlsStates(TlsRollbackStateService service, params string?[] domains) =>
        domains
            .Where(domain => !string.IsNullOrWhiteSpace(domain))
            .Select(domain => domain!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(service.Capture)
            .ToArray();

    private void DeleteCertificateFiles(string domain)
    {
        try
        {
            new LocalCertificateManager(_rootPath).Delete(domain);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            TryDeleteFile(CertificatePath(domain));
            TryDeleteFile(PrivateKeyPath(domain));
        }
    }

    private string CertificatePath(string domain) => Path.Combine(_rootPath, "config", "ssl", "sites", $"{domain}.crt.pem");
    private string PrivateKeyPath(string domain) => Path.Combine(_rootPath, "config", "ssl", "sites", $"{domain}.key.pem");

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

    private static async Task ExtractSnapshotAsync(string archivePath, string projectDestination, string databaseDestination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntries)
            throw new InvalidDataException("Snapshot contains too many entries.");

        await ValidateSnapshotMetadataAsync(archive, cancellationToken).ConfigureAwait(false);

        var projectRoot = Path.GetFullPath(projectDestination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var databaseRoot = Path.GetFullPath(databaseDestination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var extractedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.Equals("snapshot.json", StringComparison.Ordinal))
                continue;
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains(':', StringComparison.Ordinal) || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("Snapshot contains an unsafe entry path.");
            if (IsSymbolicLink(entry))
                throw new InvalidDataException($"Snapshot contains a symbolic link entry: {entry.FullName}");
            ValidateCompressionRatio(entry);
            if (normalized.EndsWith("/", StringComparison.Ordinal))
                continue;

            var isProject = normalized.StartsWith("project/", StringComparison.Ordinal);
            var isDatabase = normalized.StartsWith("database/", StringComparison.Ordinal);
            if (!isProject && !isDatabase)
                continue;

            total = checked(total + Math.Max(0, entry.Length));
            if (total > MaximumRestoredBytes)
                throw new InvalidDataException("Snapshot exceeds the maximum restored size.");

            var prefix = isProject ? "project/" : "database/";
            var root = isProject ? projectDestination : databaseDestination;
            var rootPrefix = isProject ? projectRoot : databaseRoot;
            var relative = normalized[prefix.Length..];
            if (string.IsNullOrWhiteSpace(relative) || relative.Contains('/', StringComparison.Ordinal) && isDatabase)
                throw new InvalidDataException("Snapshot database payload must contain plain backup files only.");
            var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Snapshot entry escapes the destination directory.");
            if (!extractedTargets.Add(target))
                throw new InvalidDataException($"Snapshot contains duplicate destination entry '{relative}'.");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, 81920, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ValidateSnapshotMetadataAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entries = archive.Entries
            .Where(entry => entry.FullName.Replace('\\', '/').Equals("snapshot.json", StringComparison.Ordinal))
            .ToArray();
        if (entries.Length != 1)
            throw new InvalidDataException("Snapshot must contain exactly one snapshot.json metadata entry.");

        var entry = entries[0];
        if (IsSymbolicLink(entry))
            throw new InvalidDataException("Snapshot metadata cannot be a symbolic link.");
        if (entry.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"snapshot.json exceeds the {MaximumMetadataBytes} byte metadata limit.");
        ValidateCompressionRatio(entry);

        await using var input = entry.Open();
        using var buffer = new MemoryStream();
        await input.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        if (buffer.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"snapshot.json exceeds the {MaximumMetadataBytes} byte metadata limit.");

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (!root.TryGetProperty("SchemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) ||
                schemaVersion != 1)
                throw new InvalidDataException("Snapshot metadata uses an unsupported schema version.");
            if (!root.TryGetProperty("ProjectName", out var projectName) ||
                projectName.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(projectName.GetString()))
                throw new InvalidDataException("Snapshot metadata is missing ProjectName.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("snapshot.json contains invalid JSON.", ex);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int unixFileTypeMask = 0xF000;
        const int unixSymbolicLink = 0xA000;
        return ((entry.ExternalAttributes >> 16) & unixFileTypeMask) == unixSymbolicLink;
    }

    private static void ValidateCompressionRatio(ZipArchiveEntry entry)
    {
        if (entry.Length >= CompressionRatioCheckThreshold && entry.CompressedLength > 0 &&
            (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio)
            throw new InvalidDataException($"Snapshot entry '{entry.FullName}' has a suspicious compression ratio.");
    }

    private string EnsureProjectRoot(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Project directory was not found: {root}");
        return PathSafety.EnsureUnderRootWithoutReparsePoints(
            _wwwRoot, root, "Project snapshots are restricted to the DevBox www directory and cannot traverse a reparse point.");
    }

    private static string? GetString(JsonObject value, string name)
    {
        foreach (var pair in value)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue node && node.TryGetValue<string>(out var text))
                return text;
        }
        return null;
    }

    private static bool? GetBool(JsonObject value, string name)
    {
        foreach (var pair in value)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue node && node.TryGetValue<bool>(out var flag))
                return flag;
        }
        return null;
    }

    private static void SetProperty(JsonObject value, string name, object? propertyValue)
    {
        var existing = value.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        value[string.IsNullOrEmpty(existing) ? name : existing] = JsonValue.Create(propertyValue);
    }

    private static string ReadMetadataText(string path, string displayName)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"{displayName} exceeds the {MaximumMetadataBytes} byte metadata limit.");
        return File.ReadAllText(path);
    }

    private static string BuildDomain(string projectName) => LocalDomainName.FromName(projectName);

    private static string NormalizeProjectName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var trimmed = value.Trim();
        if (trimmed.Length > 80 || trimmed is "." or ".." || trimmed.Any(ch => !char.IsLetterOrDigit(ch) && ch is not '-' and not '_'))
            throw new ArgumentException("Project name contains unsupported path characters.", nameof(value));
        return trimmed;
    }

    private static string SafeFileName(string value) => new(value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray());

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
            TryDeleteFile(temp);
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
}
