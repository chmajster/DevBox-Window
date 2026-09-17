using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DevBox.Core.Services;

public sealed record EnvironmentBackupResult(
    string ArchivePath,
    long ArchiveBytes,
    DateTimeOffset CreatedAtUtc,
    int FileCount,
    bool IncludesProjects,
    int ProjectCount);

public sealed class EnvironmentBackupService
{
    public const int BackupSchemaVersion = 1;
    private const int MaxFiles = 200_000;
    private const long MaxUncompressedBytes = 4L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _rootPath;

    public EnvironmentBackupService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public EnvironmentBackupResult Create(bool includeProjects = false)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var outputRoot = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _rootPath,
            Path.Combine(_rootPath, "backups", "environment-backups"),
            "Environment backup output cannot traverse a reparse point.");
        Directory.CreateDirectory(outputRoot);

        var suffix = $"{createdAt:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var finalPath = Path.Combine(outputRoot, $"devbox-environment-{suffix}.zip");
        var temporaryPath = Path.Combine(outputRoot, $".devbox-environment-{suffix}.tmp");
        var counters = new BackupCounters();
        var projectCount = 0;

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddConfiguration(archive, counters);
                AddSecretMetadata(archive, counters);

                if (includeProjects)
                    projectCount = AddProjects(archive, counters);

                AddJson(archive, counters, "manifest.json", new
                {
                    schemaVersion = BackupSchemaVersion,
                    createdAtUtc = createdAt,
                    includesProjects = includeProjects,
                    projectCount,
                    fileCount = counters.Files,
                    uncompressedBytes = counters.Bytes,
                    limits = new
                    {
                        maxFiles = MaxFiles,
                        maxUncompressedBytes = MaxUncompressedBytes
                    },
                    exclusions = new[]
                    {
                        "config/secrets.dpapi.json values",
                        "TLS private material under config/ssl",
                        "PFX/private-key files",
                        "database data directories and database backups",
                        "runtime binaries",
                        "temporary files and logs"
                    }
                });
            }

            File.Move(temporaryPath, finalPath);
            return new EnvironmentBackupResult(
                finalPath,
                new FileInfo(finalPath).Length,
                createdAt,
                counters.Files,
                includeProjects,
                projectCount);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private void AddConfiguration(ZipArchive archive, BackupCounters counters)
    {
        var configRoot = Path.Combine(_rootPath, "config");
        if (!Directory.Exists(configRoot))
            return;

        foreach (var path in EnumerateSafeFiles(configRoot))
        {
            var relative = Path.GetRelativePath(configRoot, path).Replace('\\', '/');
            if (ShouldExcludeConfiguration(relative))
                continue;

            AddFile(archive, counters, path, "config/" + relative);
        }
    }

    private void AddSecretMetadata(ZipArchive archive, BackupCounters counters)
    {
        IReadOnlyList<string> keys;
        try
        {
            keys = new SecureSecretStore(_rootPath).ListKeys();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            AddJson(archive, counters, "metadata/secrets.json", new
            {
                available = false,
                error = ex.GetType().Name,
                keys = Array.Empty<string>()
            });
            return;
        }

        AddJson(archive, counters, "metadata/secrets.json", new
        {
            available = true,
            count = keys.Count,
            keys
        });
    }

    private int AddProjects(ZipArchive archive, BackupCounters counters)
    {
        var projectsRoot = Path.Combine(_rootPath, "www");
        if (!Directory.Exists(projectsRoot))
            return 0;

        var projectCount = 0;
        foreach (var directory in Directory.EnumerateDirectories(projectsRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(directory))
                continue;

            var projectName = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(projectName))
                continue;

            projectCount++;
            foreach (var path in EnumerateSafeFiles(directory))
            {
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                AddFile(archive, counters, path, $"projects/{projectName}/{relative}");
            }
        }
        return projectCount;
    }

    private static bool ShouldExcludeConfiguration(string relative)
    {
        var normalized = relative.Replace('\\', '/').TrimStart('/');
        if (normalized.Equals("secrets.dpapi.json", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("secrets.dpapi.json.lock", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ssl/", StringComparison.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(normalized);
        return extension.Equals(".pfx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".p12", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".key", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            string[] files;
            string[] directories;
            try
            {
                files = Directory.GetFiles(current, "*", SearchOption.TopDirectoryOnly);
                directories = Directory.GetDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsReparsePoint(file))
                    yield return file;
            }
            foreach (var directory in directories)
            {
                if (!IsReparsePoint(directory))
                    pending.Push(directory);
            }
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void AddFile(ZipArchive archive, BackupCounters counters, string sourcePath, string entryName)
    {
        var info = new FileInfo(sourcePath);
        info.Refresh();
        if (!info.Exists)
            return;

        Reserve(counters, info.Length);
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = entry.Open();
        input.CopyTo(output);
    }

    private static void AddJson(ZipArchive archive, BackupCounters counters, string entryName, object value)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
        Reserve(counters, bytes.LongLength);
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        entry.LastWriteTime = DateTimeOffset.UtcNow;
        using var output = entry.Open();
        output.Write(bytes);
    }

    private static void Reserve(BackupCounters counters, long bytes)
    {
        if (bytes < 0)
            throw new InvalidDataException("Backup source has an invalid size.");
        if (counters.Files >= MaxFiles)
            throw new InvalidDataException($"Environment backup exceeds the {MaxFiles} file limit.");
        if (counters.Bytes > MaxUncompressedBytes - bytes)
            throw new InvalidDataException($"Environment backup exceeds the {MaxUncompressedBytes} byte uncompressed limit.");
        counters.Files++;
        counters.Bytes += bytes;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class BackupCounters
    {
        public int Files { get; set; }
        public long Bytes { get; set; }
    }
}
