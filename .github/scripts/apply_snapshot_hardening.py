from pathlib import Path

path = Path('src/DevBox.Core/Services/ProjectSnapshotService.cs')
text = path.read_text(encoding='utf-8')


def replace_once(old: str, new: str) -> None:
    global text
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f'Expected exactly one match, got {count}: {old[:120]!r}')
    text = text.replace(old, new, 1)


def replace_between(start: str, end: str, replacement: str) -> None:
    global text
    start_index = text.index(start)
    end_index = text.index(end, start_index)
    text = text[:start_index] + replacement + text[end_index:]


replace_once(
    'using System.IO.Compression;\nusing System.Text.Json;\n',
    'using System.IO.Compression;\nusing System.Security.Cryptography;\nusing System.Text;\nusing System.Text.Json;\n')

replace_once(
    '    private const long MaximumRestoredBytes = 8L * 1024 * 1024 * 1024;\n    private const int MaximumEntries = 250_000;\n',
    '    private const long MaximumRestoredBytes = 8L * 1024 * 1024 * 1024;\n    private const int MaximumEntries = 250_000;\n    private const long MaximumMetadataBytes = 1024 * 1024;\n    private const long CompressionRatioCheckThreshold = 1024 * 1024;\n    private const double MaximumCompressionRatio = 200d;\n')

create_method = '''    public async Task<ProjectSnapshotResult> CreateAsync(
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
                    var relative = Path.GetRelativePath(projectRoot, file).Replace('\\\\', '/');
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

'''
replace_between(
    '    public async Task<ProjectSnapshotResult> CreateAsync(',
    '    public async Task<string> RestoreAsync(',
    create_method)

replace_once(
    '        string? previous = null;\n        string? databaseDestination = null;\n',
    '        string? previous = null;\n        string? databaseDestination = null;\n        string? databasePrevious = null;\n')

replace_once(
    '                restoredManifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject\n',
    '                restoredManifest = JsonNode.Parse(ReadMetadataText(manifestPath, "devbox.json")) as JsonObject\n')

replace_once(
    '''                    if (Directory.Exists(databaseDestination))
                    {
                        if (!overwrite)
                            throw new InvalidOperationException($"Snapshot database backup destination already exists: {databaseDestination}");
                        Directory.Delete(databaseDestination, recursive: true);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination)!);
                    Directory.Move(databaseStaging, databaseDestination);
''',
    '''                    if (Directory.Exists(databaseDestination))
                    {
                        if (!overwrite)
                            throw new InvalidOperationException($"Snapshot database backup destination already exists: {databaseDestination}");
                        databasePrevious = databaseDestination + $".restore-backup-{Guid.NewGuid():N}";
                        Directory.Move(databaseDestination, databasePrevious);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(databaseDestination)!);
                    Directory.Move(databaseStaging, databaseDestination);
''')

replace_once(
    '''                if (databaseDestination is not null)
                {
                    var databasePath = databaseDestination;
                    rollbackActions.Add(() => TryDeleteDirectory(databasePath));
                }

                RollbackExecutor.RethrowAfterRollback(original, rollbackActions.ToArray());
''',
    '''                if (databaseDestination is not null)
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
''')

replace_once(
    '''            if (previous is not null)
                TryDeleteDirectory(previous);
            return destination;
''',
    '''            if (previous is not null)
                TryDeleteDirectory(previous);
            if (databasePrevious is not null)
                TryDeleteDirectory(databasePrevious);
            return destination;
''')

replace_once(
    '            manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject\n',
    '            manifest = JsonNode.Parse(ReadMetadataText(manifestPath, "devbox.json")) as JsonObject\n')
replace_once(
    '            var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(File.ReadAllText(lockPath), JsonOptions)\n',
    '            var lockFile = JsonSerializer.Deserialize<EnvironmentLockFile>(ReadMetadataText(lockPath, EnvironmentLockService.LockFileName), JsonOptions)\n')
replace_once(
    '            manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName))) as JsonObject\n',
    '            manifest = JsonNode.Parse(ReadMetadataText(Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName), "devbox.json")) as JsonObject\n')

extract_method = '''    private static async Task ExtractSnapshotAsync(string archivePath, string projectDestination, string databaseDestination, CancellationToken cancellationToken)
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
            var normalized = entry.FullName.Replace('\\\\', '/');
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
            .Where(entry => entry.FullName.Replace('\\\\', '/').Equals("snapshot.json", StringComparison.Ordinal))
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

'''
replace_between(
    '    private static async Task ExtractSnapshotAsync(',
    '    private string EnsureProjectRoot(',
    extract_method)

replace_once(
    '''    private static string BuildDomain(string projectName)
    {
        var label = new string(projectName.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(label))
            label = "project";
        return $"{label}.test";
    }
''',
    '''    private static string ReadMetadataText(string path, string displayName)
    {
        var info = new FileInfo(path);
        if (info.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"{displayName} exceeds the {MaximumMetadataBytes} byte metadata limit.");
        return File.ReadAllText(path);
    }

    private static string BuildDomain(string projectName)
    {
        var label = new string(projectName.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-').ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(label))
            label = "project";
        if (label.Length > 63)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectName))).ToLowerInvariant()[..8];
            var prefix = label[..54].TrimEnd('-');
            if (prefix.Length == 0)
                prefix = "project";
            label = $"{prefix}-{hash}";
        }
        return $"{label}.test";
    }
''')

path.write_text(text, encoding='utf-8')
