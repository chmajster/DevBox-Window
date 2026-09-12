using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class RuntimePlatformService : IDisposable
{
    private const long MaximumImportedRuntimeBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumImportedEntries = 100_000;
    private readonly string _rootPath;
    private readonly string _catalogPath;
    private readonly string _releaseCatalogPath;
    private readonly RuntimeManager _runtimeManager;
    private bool _disposed;

    public RuntimePlatformService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _catalogPath = SafeManagedPath(
            Path.Combine(_rootPath, "config", "runtime-catalog.json"),
            "Runtime catalog cannot escape the DevBox root or traverse a reparse point.");
        _releaseCatalogPath = SafeManagedPath(
            Path.Combine(_rootPath, "config", "runtime-catalog.release.json"),
            "Release runtime catalog cannot escape the DevBox root or traverse a reparse point.");
        _runtimeManager = new RuntimeManager(_rootPath, httpClient);
    }

    public IReadOnlyList<RuntimePackageEntry> GetCatalog()
    {
        ThrowIfDisposed();
        var release = LoadCatalog(_releaseCatalogPath, "config/runtime-catalog.release.json");
        var custom = LoadCatalog(_catalogPath, "config/runtime-catalog.json");
        return BuiltInCatalog()
            .Concat(release)
            .Concat(custom)
            .GroupBy(item => $"{item.Key}|{item.Version}|{item.Architecture}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => ParseVersion(item.Version))
            .ThenByDescending(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public RuntimePackageEntry GetPackage(string key, string version)
    {
        ValidateSegment(key, nameof(key));
        ValidateSegment(version, nameof(version));
        var architecture = RuntimeInformation.ProcessArchitecture;
        var architectureName = CurrentArchitecture();
        return GetCatalog()
                   .Where(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                  item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
                                  IsPackageArchitectureCompatible(item.Architecture, architecture))
                   .OrderByDescending(item => item.Architecture.Equals(architectureName, StringComparison.OrdinalIgnoreCase))
                   .ThenByDescending(item => item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase))
                   .FirstOrDefault()
               ?? throw new KeyNotFoundException($"Runtime package '{key}' version '{version}' for {architectureName} was not found in the catalog.");
    }

    public IReadOnlyList<RuntimeVersionStatus> GetStatuses(string? runtimeKey = null)
    {
        ThrowIfDisposed();
        if (!string.IsNullOrWhiteSpace(runtimeKey))
            ValidateSegment(runtimeKey, nameof(runtimeKey));

        var architecture = RuntimeInformation.ProcessArchitecture;
        var architectureName = CurrentArchitecture();
        var packages = GetCatalog()
            .Where(item => string.IsNullOrWhiteSpace(runtimeKey) || item.Key.Equals(runtimeKey, StringComparison.OrdinalIgnoreCase))
            .Where(item => IsPackageArchitectureCompatible(item.Architecture, architecture))
            .GroupBy(item => $"{item.Key}|{item.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(item => item.Architecture.Equals(architectureName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(item => item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase))
                .First())
            .ToArray();
        var statuses = new List<RuntimeVersionStatus>();

        foreach (var package in packages)
        {
            var installations = _runtimeManager.GetInstalled(package.Key, package.ExecutableRelativePath);
            var installation = installations.FirstOrDefault(item => item.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase));
            statuses.Add(new RuntimeVersionStatus(
                package,
                installation is not null,
                installation?.IsActive ?? false,
                installation?.IsValid ?? false,
                DetermineSupportState(package.EndOfLifeDate)));
        }

        var candidateKeys = new HashSet<string>(packages.Select(item => item.Key), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(runtimeKey))
        {
            candidateKeys.Add(runtimeKey);
        }
        else
        {
            var runtimeRoot = SafeManagedPath(
                Path.Combine(_rootPath, "runtime"),
                "Runtime discovery root cannot escape the DevBox root or be a reparse point.");
            if (Directory.Exists(runtimeRoot))
            {
                foreach (var directory in Directory.GetDirectories(runtimeRoot))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    candidateKeys.Add(Path.GetFileName(directory));
                }
            }
        }

        foreach (var key in candidateKeys)
        {
            string executable;
            try
            {
                executable = GuessExecutable(key);
            }
            catch (KeyNotFoundException)
            {
                continue;
            }

            var displayName = packages.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? key;
            foreach (var installation in _runtimeManager.GetInstalled(key, executable))
            {
                if (packages.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                                         item.Version.Equals(installation.Version, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var installedPackage = new RuntimePackageEntry
                {
                    Key = key,
                    DisplayName = displayName,
                    Version = installation.Version,
                    Architecture = architectureName,
                    ExecutableRelativePath = executable
                };
                statuses.Add(new RuntimeVersionStatus(
                    installedPackage,
                    Installed: true,
                    Active: installation.IsActive,
                    Valid: installation.IsValid,
                    SupportState: RuntimeSupportState.Unknown));
            }
        }

        return statuses
            .OrderBy(item => item.Package.Key, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => ParseVersion(item.Package.Version))
            .ThenByDescending(item => item.Package.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task InstallAsync(string key, string version, CancellationToken cancellationToken = default) =>
        InstallAsync(key, version, progress: null, cancellationToken);

    public async Task InstallAsync(
        string key,
        string version,
        IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var package = GetPackage(key, version);
        await _runtimeManager.InstallAsync(package.ToRuntimeDefinition(), progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateAsync(string key, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSegment(key, nameof(key));
        ValidateSegment(version, nameof(version));
        var package = GetPackageOrInstalledPackage(key, version);
        await _runtimeManager.ActivateAsync(key, version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateSegment(key, nameof(key));
        ValidateSegment(version, nameof(version));
        _ = GetPackageOrInstalledPackage(key, version);
        await _runtimeManager.RemoveAsync(key, version, cancellationToken).ConfigureAwait(false);
    }

    public async Task ImportLocalArchiveAsync(
        RuntimePackageEntry package,
        string archivePath,
        string expectedSha256,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        cancellationToken.ThrowIfCancellationRequested();

        ValidatePackage(package);
        ValidateSha256(expectedSha256, "Expected runtime archive SHA-256");
        var sourceArchive = Path.GetFullPath(archivePath);
        if (!File.Exists(sourceArchive))
            throw new FileNotFoundException("Runtime archive was not found.", sourceArchive);
        VerifySha256(sourceArchive, expectedSha256);

        var tempRoot = SafeManagedPath(
            Path.Combine(_rootPath, "tmp", "runtime-imports", Guid.NewGuid().ToString("N")),
            "Runtime import temporary directory cannot escape the DevBox root or traverse a reparse point.");
        var extractRoot = SafeManagedPath(
            Path.Combine(tempRoot, "extract"),
            "Runtime import extraction directory cannot escape the DevBox root or traverse a reparse point.");
        var staging = SafeManagedPath(
            Path.Combine(tempRoot, "staging"),
            "Runtime import staging directory cannot escape the DevBox root or traverse a reparse point.");
        Directory.CreateDirectory(tempRoot);
        try
        {
            ArchiveSafety.ExtractZipSafely(sourceArchive, extractRoot, MaximumImportedRuntimeBytes, MaximumImportedEntries, "Runtime import");
            var source = string.IsNullOrWhiteSpace(package.ArchiveRootDirectory)
                ? extractRoot
                : PathSafety.EnsureUnderRootWithoutReparsePoints(
                    extractRoot,
                    Path.GetFullPath(Path.Combine(extractRoot, package.ArchiveRootDirectory)),
                    "Runtime archive root escapes the extracted directory or traverses a reparse point.",
                    allowRoot: true);
            if (!Directory.Exists(source))
                throw new InvalidDataException($"Archive root '{package.ArchiveRootDirectory}' does not exist.");

            CopyDirectory(source, staging, cancellationToken);
            var executable = PathSafety.EnsureUnderRootWithoutReparsePoints(
                staging,
                Path.GetFullPath(Path.Combine(staging, package.ExecutableRelativePath)),
                "Runtime executable path escapes the package directory or traverses a reparse point.");
            if (!File.Exists(executable))
                throw new InvalidDataException($"Runtime executable '{package.ExecutableRelativePath}' was not found in the package.");
            File.WriteAllText(Path.Combine(staging, ".devbox-version"), package.Version);

            using var runtimeLock = await _runtimeManager.AcquireRuntimeLockAsync(package.Key, cancellationToken).ConfigureAwait(false);
            var installRoot = SafeManagedPath(
                Path.Combine(_rootPath, "runtime", package.Key),
                "Runtime import install root cannot escape the DevBox root or traverse a reparse point.");
            var installPath = SafeManagedPath(
                Path.Combine(installRoot, package.Version),
                "Runtime import install path cannot escape the DevBox root or traverse a reparse point.");
            Directory.CreateDirectory(installRoot);
            if (Directory.Exists(installPath))
                throw new InvalidOperationException($"Runtime {package.Key} {package.Version} is already installed.");
            Directory.Move(staging, installPath);

            try
            {
                if (activate)
                    await _runtimeManager.ActivateUnderLockAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception original)
            {
                try
                {
                    if (Directory.Exists(installPath))
                        Directory.Delete(installPath, recursive: true);
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    throw new AggregateException(
                        $"Runtime import failed and rollback of {package.Key} {package.Version} was incomplete.",
                        original,
                        rollbackError);
                }
                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public void SaveCatalogEntry(RuntimePackageEntry package)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(package);
        ValidatePackage(package);
        var lockPath = SafeManagedPath(
            _catalogPath + ".lock",
            "Runtime catalog lock cannot escape the DevBox root or traverse a reparse point.");
        using var mutationLock = CrossProcessFileLock.Acquire(lockPath, TimeSpan.FromSeconds(15));
        var custom = LoadCatalog(_catalogPath, "config/runtime-catalog.json").ToList();
        var index = custom.FindIndex(item =>
            item.Key.Equals(package.Key, StringComparison.OrdinalIgnoreCase) &&
            item.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase) &&
            item.Architecture.Equals(package.Architecture, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            custom[index] = package;
        else
            custom.Add(package);

        Directory.CreateDirectory(Path.GetDirectoryName(_catalogPath)!);
        AtomicWrite(_catalogPath, JsonSerializer.Serialize(custom, JsonOptions));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _runtimeManager.Dispose();
    }

    private RuntimePackageEntry GetPackageOrInstalledPackage(string key, string version)
    {
        try
        {
            return GetPackage(key, version);
        }
        catch (KeyNotFoundException)
        {
            var runtimeRoot = SafeManagedPath(
                Path.Combine(_rootPath, "runtime", key, version),
                "Installed runtime path cannot escape the DevBox root or traverse a reparse point.");
            if (!Directory.Exists(runtimeRoot))
                throw;
            var executable = GuessExecutable(key);
            return new RuntimePackageEntry
            {
                Key = key,
                DisplayName = key,
                Version = version,
                Architecture = CurrentArchitecture(),
                ExecutableRelativePath = executable
            };
        }
    }

    private IReadOnlyList<RuntimePackageEntry> LoadCatalog(string path, string displayPath)
    {
        if (!File.Exists(path))
            return Array.Empty<RuntimePackageEntry>();
        try
        {
            var packages = JsonSerializer.Deserialize<List<RuntimePackageEntry?>>(File.ReadAllText(path), JsonOptions)
                ?? new List<RuntimePackageEntry?>();
            if (packages.Any(package => package is null))
                throw new InvalidDataException($"{displayPath} contains a null entry.");

            var materialized = packages.Select(package => package!).ToArray();
            foreach (var package in materialized)
                ValidatePackage(package);

            var duplicate = materialized
                .GroupBy(
                    package => $"{package.Key}|{package.Version}|{package.Architecture}",
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                throw new InvalidDataException($"{displayPath} contains a duplicate runtime key/version/architecture entry.");

            return materialized;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{displayPath} contains invalid JSON.", ex);
        }
    }

    private static IReadOnlyList<RuntimePackageEntry> BuiltInCatalog()
    {
        var architecture = CurrentArchitecture();
        var node = new NodeRuntimeCatalog().GetRecommended();
        return
        [
            new RuntimePackageEntry
            {
                Key = "php",
                DisplayName = "PHP",
                Version = "8.5.10",
                Architecture = "x64",
                ExecutableRelativePath = "php-cgi.exe",
                DownloadUrl = "https://downloads.php.net/~windows/releases/archives/php-8.5.10-nts-Win32-vs17-x64.zip",
                Sha256 = "22ec430195984d233eb9e62c637a945bbcda06efca2f392d9d96d62c6acd34f8",
                Recommended = true
            },
            new RuntimePackageEntry
            {
                Key = "nginx",
                DisplayName = "Nginx",
                Version = "1.31.5",
                Architecture = "any",
                ExecutableRelativePath = "nginx.exe",
                DownloadUrl = "https://nginx.org/download/nginx-1.31.5.zip",
                Sha256 = "00ad32a2bf66cee0ec8eb194347e8e79917f47017ccd3ad4bebf5574fabe002c",
                ArchiveRootDirectory = "nginx-1.31.5",
                Recommended = true
            },
            new RuntimePackageEntry
            {
                Key = "mysql",
                DisplayName = "MySQL",
                Version = "8.4.11",
                Architecture = "x64",
                ExecutableRelativePath = Path.Combine("bin", "mysqld.exe"),
                ArchiveRootDirectory = "mysql-8.4.11-winx64",
                Recommended = true
            },
            new RuntimePackageEntry
            {
                Key = "node",
                DisplayName = "Node.js LTS",
                Version = node.Version,
                Architecture = architecture,
                ExecutableRelativePath = node.ExecutableRelativePath,
                DownloadUrl = node.DownloadUrl,
                Sha256 = node.Sha256,
                ArchiveRootDirectory = node.ArchiveRootDirectory,
                Recommended = true
            }
        ];
    }

    private static string GuessExecutable(string key) => key.ToLowerInvariant() switch
    {
        "php" => "php-cgi.exe",
        "nginx" => "nginx.exe",
        "node" => "node.exe",
        "mysql" => Path.Combine("bin", "mysqld.exe"),
        "mariadb" => Path.Combine("bin", "mysqld.exe"),
        "postgresql" => Path.Combine("bin", "postgres.exe"),
        _ => throw new KeyNotFoundException($"No executable convention is known for runtime '{key}'. Add it to config/runtime-catalog.json.")
    };

    private static RuntimeSupportState DetermineSupportState(DateOnly? eol)
    {
        if (eol is null)
            return RuntimeSupportState.Unknown;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (eol < today)
            return RuntimeSupportState.EndOfLife;
        if (eol <= today.AddYears(1))
            return RuntimeSupportState.Maintenance;
        return RuntimeSupportState.Current;
    }

    private static void ValidatePackage(RuntimePackageEntry package)
    {
        ValidateSegment(package.Key, "Runtime key");
        ValidateSegment(package.Version, "Runtime version");
        if (string.IsNullOrWhiteSpace(package.DisplayName))
            throw new InvalidDataException("Runtime display name is required.");
        if (string.IsNullOrWhiteSpace(package.Architecture) ||
            package.Architecture.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidDataException("Runtime architecture is invalid.");
        ValidateRelativePath(package.ExecutableRelativePath, "Runtime executable path");
        if (!string.IsNullOrWhiteSpace(package.ArchiveRootDirectory))
            ValidateRelativePath(package.ArchiveRootDirectory, "Runtime archive root");
        if (!string.IsNullOrWhiteSpace(package.DownloadUrl))
        {
            if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Runtime catalog download URL must use HTTPS.");
            ValidateSha256(package.Sha256, "Runtime catalog SHA-256");
        }
        else if (!string.IsNullOrWhiteSpace(package.Sha256))
        {
            throw new InvalidDataException("Runtime catalog SHA-256 cannot be specified without a download URL.");
        }
    }

    private static void ValidateSha256(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
            throw new InvalidDataException($"{label} must contain 64 hexadecimal characters.");
        try
        {
            if (Convert.FromHexString(value.Trim()).Length != 32)
                throw new InvalidDataException($"{label} must contain 64 hexadecimal characters.");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"{label} must contain 64 hexadecimal characters.", ex);
        }
    }

    private static void ValidateSegment(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw new InvalidDataException($"{label} is invalid.");
    }

    private static void ValidateRelativePath(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new InvalidDataException($"{label} must be a non-empty relative path.");
        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidDataException($"{label} cannot contain traversal segments.");
    }

    private static void VerifySha256(string path, string expected)
    {
        ValidateSha256(expected, "Expected runtime archive SHA-256");
        var expectedBytes = Convert.FromHexString(expected.Trim());
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedBytes))
            throw new InvalidDataException("SHA-256 verification failed for imported runtime archive.");
    }

    private string SafeManagedPath(string path, string message) =>
        PathSafety.EnsureUnderRootWithoutReparsePoints(_rootPath, path, message);

    private static void CopyDirectory(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Runtime package root cannot be a reparse point.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime package contains a reparse point.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime package contains a reparse point.");
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), cancellationToken);
        }
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

    internal static bool IsPackageArchitectureCompatible(string packageArchitecture, Architecture architecture)
    {
        if (packageArchitecture.Equals("any", StringComparison.OrdinalIgnoreCase))
            return true;
        var current = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => architecture.ToString().ToLowerInvariant()
        };
        if (packageArchitecture.Equals(current, StringComparison.OrdinalIgnoreCase))
            return true;
        return OperatingSystem.IsWindows() && architecture == Architecture.Arm64 &&
               packageArchitecture.Equals("x64", StringComparison.OrdinalIgnoreCase);
    }

    private static string CurrentArchitecture() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
    };

    private static Version ParseVersion(string value) => Version.TryParse(value, out var version) ? version : new Version(0, 0);

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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
