using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class RuntimeManager : IRuntimeManager, IDisposable
{
    private const string VersionMarker = ".devbox-version";
    private const long MaximumRuntimeDownloadBytes = 1536L * 1024 * 1024;
    private const long MaximumRuntimeExtractedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumRuntimeArchiveEntries = 100_000;
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
            return Array.Empty<RuntimeInstallation>();

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

    public Task InstallAsync(RuntimeDefinition definition, CancellationToken cancellationToken = default) =>
        InstallAsync(definition, progress: null, cancellationToken);

    public async Task InstallAsync(RuntimeDefinition definition, IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);
        progress?.Report(0);
        using var runtimeLock = await AcquireRuntimeLockAsync(definition.Key, cancellationToken).ConfigureAwait(false);
        await InstallUnderLockAsync(definition, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallUnderLockAsync(RuntimeDefinition definition, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(definition);
        ValidateDefinition(definition);

        var bundledPath = VersionPath(definition.Key, definition.Version);
        if (Directory.Exists(bundledPath))
        {
            try
            {
                progress?.Report(85);
                ValidateRuntimeExecutable(bundledPath, definition.ExecutableRelativePath);
                progress?.Report(95);
                await ActivateUnderLockAsync(
                    definition.Key,
                    definition.Version,
                    definition.ExecutableRelativePath,
                    cancellationToken).ConfigureAwait(false);
                progress?.Report(100);
                return;
            }
            catch (InvalidDataException) when (definition.HasRemotePackage)
            {
                QuarantineInvalidRuntime(bundledPath);
            }
        }

        if (!definition.HasRemotePackage)
            throw new InvalidOperationException($"Runtime {definition.DisplayName} {definition.Version} is not bundled and has no verified remote package.");

        var tempRoot = Path.Combine(_rootPath, "tmp", "runtimes", definition.Key, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "package.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        var stagingPath = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(tempRoot);

        try
        {
            progress?.Report(5);
            IProgress<int>? downloadProgress = progress is null ? null : new MappedProgress(progress, 5, 60);
            await DownloadAsync(definition.DownloadUrl!, archivePath, downloadProgress, cancellationToken).ConfigureAwait(false);
            progress?.Report(68);
            VerifySha256(archivePath, definition.Sha256!);
            progress?.Report(74);
            ExtractZipSafely(archivePath, extractPath);

            var sourcePath = string.IsNullOrWhiteSpace(definition.ArchiveRootDirectory)
                ? extractPath
                : Path.Combine(extractPath, definition.ArchiveRootDirectory);
            if (!Directory.Exists(sourcePath))
                throw new InvalidDataException($"Archive root '{definition.ArchiveRootDirectory}' was not found.");

            progress?.Report(82);
            CopyDirectory(sourcePath, stagingPath, cancellationToken);
            ValidateRuntimeExecutable(stagingPath, definition.ExecutableRelativePath);
            File.WriteAllText(Path.Combine(stagingPath, VersionMarker), definition.Version);

            progress?.Report(90);
            var installPath = VersionPath(definition.Key, definition.Version);
            ReplaceDirectory(stagingPath, installPath);
            progress?.Report(95);
            await ActivateUnderLockAsync(definition.Key, definition.Version, definition.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
            progress?.Report(100);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task ActivateAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var runtimeLock = await AcquireRuntimeLockAsync(runtimeKey, cancellationToken).ConfigureAwait(false);
        await ActivateUnderLockAsync(runtimeKey, version, executableRelativePath, cancellationToken).ConfigureAwait(false);
    }

    internal Task ActivateUnderLockAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        ValidateSegment(version, nameof(version));
        ValidateRelativePath(executableRelativePath, nameof(executableRelativePath));

        var sourcePath = VersionPath(runtimeKey, version);
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Runtime {runtimeKey} {version} is not installed.");

        ValidateRuntimeExecutable(sourcePath, executableRelativePath);

        var runtimeRoot = RuntimeRoot(runtimeKey);
        Directory.CreateDirectory(runtimeRoot);
        var stagingPath = Path.Combine(runtimeRoot, $".current-{Guid.NewGuid():N}");
        CopyDirectory(sourcePath, stagingPath, cancellationToken);
        File.WriteAllText(Path.Combine(stagingPath, VersionMarker), version);
        ReplaceDirectory(stagingPath, Path.Combine(runtimeRoot, "current"));
        return Task.CompletedTask;
    }

    public async Task RemoveAsync(string runtimeKey, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var runtimeLock = await AcquireRuntimeLockAsync(runtimeKey, cancellationToken).ConfigureAwait(false);
        await RemoveUnderLockAsync(runtimeKey, version, cancellationToken).ConfigureAwait(false);
    }

    private Task RemoveUnderLockAsync(string runtimeKey, string version, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        ValidateSegment(version, nameof(version));

        var runtimeRoot = RuntimeRoot(runtimeKey);
        var activeVersion = ReadVersionMarker(Path.Combine(runtimeRoot, "current"));
        if (version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The active runtime version cannot be removed. Activate another version first.");

        if (runtimeKey.Equals("php", StringComparison.OrdinalIgnoreCase))
        {
            var dependents = new SiteManager(_rootPath).GetSites()
                .Where(site => site.PhpVersion?.Equals(version, StringComparison.OrdinalIgnoreCase) == true)
                .Select(site => site.Domain)
                .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (dependents.Length > 0)
                throw new InvalidOperationException($"PHP {version} cannot be removed because it is assigned to: {string.Join(", ", dependents)}.");
        }

        var installPath = VersionPath(runtimeKey, version);
        if (Directory.Exists(installPath))
            Directory.Delete(installPath, recursive: true);

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    internal static void VerifySha256(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64)
            throw new InvalidDataException("Invalid expected SHA-256 value.");

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
            throw new InvalidDataException("SHA-256 verification failed for runtime package.");
    }

    internal static void ExtractZipSafely(string archivePath, string destinationPath) =>
        ArchiveSafety.ExtractZipSafely(
            archivePath,
            destinationPath,
            MaximumRuntimeExtractedBytes,
            MaximumRuntimeArchiveEntries,
            "Runtime");

    private async Task DownloadAsync(string url, string destination, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Runtime download URL must use HTTPS.");

        await ArchiveSafety.DownloadToFileAsync(
            _httpClient,
            uri,
            destination,
            MaximumRuntimeDownloadBytes,
            "Runtime",
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    internal Task<FileStream> AcquireRuntimeLockAsync(string runtimeKey, CancellationToken cancellationToken)
    {
        ValidateSegment(runtimeKey, nameof(runtimeKey));
        return CrossProcessFileLock.AcquireAsync(
            Path.Combine(_rootPath, "tmp", "locks", $"runtime-{runtimeKey.ToLowerInvariant()}.lock"),
            cancellationToken,
            TimeSpan.FromSeconds(30));
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
            ValidateRelativePath(definition.ArchiveRootDirectory, nameof(definition.ArchiveRootDirectory));

        var hasUrl = !string.IsNullOrWhiteSpace(definition.DownloadUrl);
        var hasHash = !string.IsNullOrWhiteSpace(definition.Sha256);
        if (hasUrl != hasHash)
            throw new InvalidDataException("A remote runtime definition must provide both an HTTPS URL and a pinned SHA-256 value.");
    }

    private static void ValidateRuntimeExecutable(string root, string executableRelativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var executablePath = Path.GetFullPath(Path.Combine(root, executableRelativePath));
        if (!executablePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(executablePath))
            throw new InvalidDataException($"Runtime executable '{executableRelativePath}' was not found in the package.");
    }

    private static void ValidateSegment(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value is "." or ".." || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new ArgumentException("Value contains unsafe path characters.", parameterName);
    }

    private static void ValidateRelativePath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (Path.IsPathRooted(value))
            throw new ArgumentException("Path must be relative.", parameterName);

        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(part => part == ".."))
            throw new ArgumentException("Relative path cannot contain parent traversal.", parameterName);
    }

    private static string? ReadVersionMarker(string directory)
    {
        var marker = Path.Combine(directory, VersionMarker);
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    private static void CopyDirectory(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            throw new InvalidOperationException("Target path has no parent directory.");

        Directory.CreateDirectory(parent);
        var backupPath = installPath + $".backup-{Guid.NewGuid():N}";
        if (Directory.Exists(installPath))
            Directory.Move(installPath, backupPath);

        try
        {
            Directory.Move(stagingPath, installPath);
            TryDeleteDirectory(backupPath);
        }
        catch
        {
            TryDeleteDirectory(installPath);
            if (Directory.Exists(backupPath))
                Directory.Move(backupPath, installPath);
            throw;
        }
    }

    private static void QuarantineInvalidRuntime(string path)
    {
        if (!Directory.Exists(path))
            return;
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("Runtime path has no parent directory.");
        var name = Path.GetFileName(path);
        var quarantine = Path.Combine(parent, $".invalid-{name}-{Guid.NewGuid():N}");
        Directory.Move(path, quarantine);
        TryDeleteDirectory(quarantine);
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class MappedProgress(IProgress<int> target, int start, int span) : IProgress<int>
    {
        public void Report(int value)
        {
            var normalized = Math.Clamp(value, 0, 100);
            target.Report(start + (int)Math.Round(normalized * (span / 100d)));
        }
    }
}
