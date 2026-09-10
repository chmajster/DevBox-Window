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
    private readonly RuntimeManager _runtimeManager;
    private bool _disposed;

    public RuntimePlatformService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _catalogPath = Path.Combine(_rootPath, "config", "runtime-catalog.json");
        _runtimeManager = new RuntimeManager(_rootPath, httpClient);
    }

    public IReadOnlyList<RuntimePackageEntry> GetCatalog()
    {
        ThrowIfDisposed();
        var custom = LoadCustomCatalog();
        return BuiltInCatalog()
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
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var architecture = CurrentArchitecture();
        return GetCatalog().FirstOrDefault(item =>
                   item.Key.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                   item.Version.Equals(version, StringComparison.OrdinalIgnoreCase) &&
                   (item.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase) || item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase)))
               ?? throw new KeyNotFoundException($"Runtime package '{key}' version '{version}' for {architecture} was not found in the catalog.");
    }

    public IReadOnlyList<RuntimeVersionStatus> GetStatuses(string? runtimeKey = null)
    {
        ThrowIfDisposed();
        var packages = GetCatalog()
            .Where(item => string.IsNullOrWhiteSpace(runtimeKey) || item.Key.Equals(runtimeKey, StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Architecture.Equals("any", StringComparison.OrdinalIgnoreCase) || item.Architecture.Equals(CurrentArchitecture(), StringComparison.OrdinalIgnoreCase))
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

        return statuses;
    }

    public async Task InstallAsync(string key, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var package = GetPackage(key, version);
        await _runtimeManager.InstallAsync(package.ToRuntimeDefinition(), cancellationToken).ConfigureAwait(false);
    }

    public async Task ActivateAsync(string key, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var package = GetPackageOrInstalledPackage(key, version);
        await _runtimeManager.ActivateAsync(key, version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string key, string version, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
        var sourceArchive = Path.GetFullPath(archivePath);
        if (!File.Exists(sourceArchive))
            throw new FileNotFoundException("Runtime archive was not found.", sourceArchive);
        VerifySha256(sourceArchive, expectedSha256);

        var tempRoot = Path.Combine(_rootPath, "tmp", "runtime-imports", Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(tempRoot, "extract");
        var staging = Path.Combine(tempRoot, "staging");
        Directory.CreateDirectory(tempRoot);
        try
        {
            ArchiveSafety.ExtractZipSafely(sourceArchive, extractRoot, MaximumImportedRuntimeBytes, MaximumImportedEntries, "Runtime import");
            var source = string.IsNullOrWhiteSpace(package.ArchiveRootDirectory)
                ? extractRoot
                : Path.GetFullPath(Path.Combine(extractRoot, package.ArchiveRootDirectory));
            EnsureUnderOrEqual(source, extractRoot, "Runtime archive root escapes the extracted directory.");
            if (!Directory.Exists(source))
                throw new InvalidDataException($"Archive root '{package.ArchiveRootDirectory}' does not exist.");

            CopyDirectory(source, staging);
            var executable = Path.GetFullPath(Path.Combine(staging, package.ExecutableRelativePath));
            EnsureUnder(executable, staging, "Runtime executable path escapes the package directory.");
            if (!File.Exists(executable))
                throw new InvalidDataException($"Runtime executable '{package.ExecutableRelativePath}' was not found in the package.");
            File.WriteAllText(Path.Combine(staging, ".devbox-version"), package.Version);

            var installRoot = Path.Combine(_rootPath, "runtime", package.Key);
            var installPath = Path.Combine(installRoot, package.Version);
            Directory.CreateDirectory(installRoot);
            if (Directory.Exists(installPath))
                throw new InvalidOperationException($"Runtime {package.Key} {package.Version} is already installed.");
            Directory.Move(staging, installPath);

            if (activate)
                await _runtimeManager.ActivateAsync(package.Key, package.Version, package.ExecutableRelativePath, cancellationToken).ConfigureAwait(false);
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
        var custom = LoadCustomCatalog().ToList();
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
            var runtimeRoot = Path.Combine(_rootPath, "runtime", key, version);
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

    private IReadOnlyList<RuntimePackageEntry> LoadCustomCatalog()
    {
        if (!File.Exists(_catalogPath))
            return Array.Empty<RuntimePackageEntry>();
        try
        {
            var packages = JsonSerializer.Deserialize<List<RuntimePackageEntry>>(File.ReadAllText(_catalogPath), JsonOptions)
                ?? new List<RuntimePackageEntry>();
            foreach (var package in packages)
                ValidatePackage(package);
            return packages;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("config/runtime-catalog.json contains invalid JSON.", ex);
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
        if (string.IsNullOrWhiteSpace(package.Key) || package.Key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || package.Key.Contains(Path.DirectorySeparatorChar) || package.Key.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Runtime key is invalid.");
        if (string.IsNullOrWhiteSpace(package.Version) || package.Version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || package.Version.Contains(Path.DirectorySeparatorChar) || package.Version.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Runtime version is invalid.");
        if (string.IsNullOrWhiteSpace(package.ExecutableRelativePath) || Path.IsPathRooted(package.ExecutableRelativePath))
            throw new InvalidDataException("Runtime executable path must be relative.");
        if (!string.IsNullOrWhiteSpace(package.DownloadUrl))
        {
            if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Runtime catalog download URL must use HTTPS.");
            if (string.IsNullOrWhiteSpace(package.Sha256) || package.Sha256.Trim().Length != 64)
                throw new InvalidDataException("Remote runtime catalog entries require a pinned SHA-256 digest.");
        }
    }

    private static void VerifySha256(string path, string expected)
    {
        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Expected runtime archive SHA-256 is invalid.", ex);
        }
        if (expectedBytes.Length != 32)
            throw new InvalidDataException("Expected runtime archive SHA-256 must contain 64 hexadecimal characters.");
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expectedBytes))
            throw new InvalidDataException("SHA-256 verification failed for imported runtime archive.");
    }

    private static void EnsureUnder(string candidate, string root, string message)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    private static void EnsureUnderOrEqual(string candidate, string root, string message)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var childPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        if (!normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalizedCandidate.StartsWith(childPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(message);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime package contains a reparse point.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Runtime package contains a reparse point.");
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
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
