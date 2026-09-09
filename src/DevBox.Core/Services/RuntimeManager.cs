using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class RuntimeManager : IRuntimeManager, IDisposable
{
    private const string VersionMarker = ".devbox-version";
    private readonly string _rootPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public RuntimeManager(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public IReadOnlyList<RuntimeInstallation> GetInstalled(string runtimeKey, string executableRelativePath)
    {
        ThrowIfDisposed();
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        ValidateRelativePath(executableRelativePath, nameof(executableRelativePath));

        var runtimeRoot = RuntimeRoot(runtimeKey);
        if (!Directory.Exists(runtimeRoot))
        {
            return Array.Empty<RuntimeInstallation>();
        }

        var activeVersion = ReadVersionMarker(Path.Combine(runtimeRoot, "current"));
        return Directory.GetDirectories(runtimeRoot)
            .Where(path => !Path.GetFileName(path).Equals("current", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).Contains(".backup-", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var version = Path.GetFileName(path);
                var executable = Path.Combine(path, executableRelativePath);
                return new RuntimeInstallation(
                    runtimeKey,
                    version,
                    path,
                    version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase),
                    File.Exists(executable));
            })
            .OrderByDescending(item => ParseVersionForSort(item.Version), Comparer<Version>.Default)
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task InstallAsync(RuntimeDefinition definition, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);

        var tempRoot = Path.Combine(_rootPath, "tmp", "runtimes", definition.Key, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "package.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        var stagingPath = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await DownloadAsync(definition.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);
            VerifySha256(archivePath, definition.Sha256);
            ExtractZipSafely(archivePath, extractPath);

            var sourcePath = string.IsNullOrWhiteSpace(definition.ArchiveRootDirectory)
                ? extractPath
                : Path.Combine(extractPath, definition.ArchiveRootDirectory);

            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidDataException($"Archive root '{definition.ArchiveRootDirectory}' was not found.");
            }

            CopyDirectory(sourcePath, stagingPath);
            ValidateRuntimeExecutable(stagingPath, definition.ExecutableRelativePath);
            File.WriteAllText(Path.Combine(stagingPath, VersionMarker), definition.Version);

            var installPath = VersionPath(definition.Key, definition.Version);
            ReplaceDirectory(stagingPath, installPath);
            await ActivateAsync(definition.Key, definition.Version, definition.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public Task ActivateAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        ValidateSegment(version, nameof(version));
        ValidateRelativePath(executableRelativePath, nameof(executableRelativePath));

        var sourcePath = VersionPath(runtimeKey, version);
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Runtime {runtimeKey} {version} is not installed.");
        }

        ValidateRuntimeExecutable(sourcePath, executableRelativePath);

        var runtimeRoot = RuntimeRoot(runtimeKey);
        Directory.CreateDirectory(runtimeRoot);
        var stagingPath = Path.Combine(runtimeRoot, $".current-{Guid.NewGuid():N}");
        CopyDirectory(sourcePath, stagingPath);
        File.WriteAllText(Path.Combine(stagingPath, VersionMarker), version);
        ReplaceDirectory(stagingPath, Path.Combine(runtimeRoot, "current"));
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string runtimeKey, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        ValidateSegment(version, nameof(version));

        var runtimeRoot = RuntimeRoot(runtimeKey);
        var activeVersion = ReadVersionMarker(Path.Combine(runtimeRoot, "current"));
        if (version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The active runtime version cannot be removed. Activate another version first.");
        }

        var installPath = VersionPath(runtimeKey, version);
        if (Directory.Exists(installPath))
        {
            Directory.Delete(installPath, recursive: true);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal static void VerifySha256(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64)
        {
            throw new InvalidDataException("Invalid expected SHA-256 value.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Invalid expected SHA-256 value.", ex);
        }

        using var stream = File.OpenRead(filePath);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("SHA-256 verification failed for runtime package.");
        }
    }

    internal static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var destinationRoot = Path.GetFullPath(destinationPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var normalizedEntry = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            var outputPath = Path.GetFullPath(Path.Combine(destinationPath, normalizedEntry));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Unsafe ZIP entry detected: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            entry.ExtractToFile(outputPath, overwrite: true);
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Runtime download URL must use HTTPS.");
        }

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private string RuntimeRoot(string runtimeKey) => Path.Combine(_rootPath, "runtime", runtimeKey);

    private string VersionPath(string runtimeKey, string version) => Path.Combine(RuntimeRoot(runtimeKey), version);

    private static Version ParseVersionForSort(string version) =>
        Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0);

    private static void ValidateDefinition(RuntimeDefinition definition)
    {
        ValidateSegment(definition.Key, nameof(definition.Key));
        ValidateSegment(definition.Version, nameof(definition.Version));
        ValidateRelativePath(definition.ExecutableRelativePath, nameof(definition.ExecutableRelativePath));
        if (!string.IsNullOrWhiteSpace(definition.ArchiveRootDirectory))
        {
            ValidateRelativePath(definition.ArchiveRootDirectory, nameof(definition.ArchiveRootDirectory));
        }
    }

    private static void ValidateRuntimeExecutable(string root, string executableRelativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executablePath = Path.GetFullPath(Path.Combine(root, executableRelativePath));
        if (!executablePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(executablePath))
        {
            throw new InvalidDataException($"Runtime executable '{executableRelativePath}' was not found in the package.");
        }
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("Value contains unsafe path characters.", parameterName);
        }
    }

    private static void ValidateRelativePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Path.IsPathRooted(value))
        {
            throw new ArgumentException("Path must be relative.", parameterName);
        }

        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
        {
            throw new ArgumentException("Relative path cannot contain parent traversal.", parameterName);
        }
    }

    private static string? ReadVersionMarker(string directory)
    {
        var marker = Path.Combine(directory, VersionMarker);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var target = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void ReplaceDirectory(string stagingPath, string installPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(installPath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InvalidOperationException("Target path has no parent directory.");
        }

        Directory.CreateDirectory(parent);
        var backupPath = installPath + $".backup-{Guid.NewGuid():N}";
        if (Directory.Exists(installPath))
        {
            Directory.Move(installPath, backupPath);
        }

        try
        {
            Directory.Move(stagingPath, installPath);
            TryDeleteDirectory(backupPath);
        }
        catch
        {
            TryDeleteDirectory(installPath);
            if (Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, installPath);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
