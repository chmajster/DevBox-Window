using System.Security.Cryptography;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonInstaller : IDisposable
{
    private const long MaximumAddonDownloadBytes = 256L * 1024 * 1024;
    private const long MaximumAddonExtractedBytes = 1024L * 1024 * 1024;
    private const int MaximumAddonArchiveEntries = 50_000;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _rootPath;
    private bool _disposed;

    public AddonInstaller(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async Task InstallAsync(AddonDefinition addon, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(addon);
        EnsureInstallPathIsSafe(addon);
        using var addonLock = await CrossProcessFileLock.AcquireAsync(AddonLockPath(addon), cancellationToken, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        var tempRoot = Path.Combine(_rootPath, "tmp", "addons", addon.Key, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "package.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        var stagingPath = Path.Combine(tempRoot, "staging");

        Directory.CreateDirectory(tempRoot);

        try
        {
            await DownloadAsync(addon.DownloadUrl, archivePath, cancellationToken).ConfigureAwait(false);
            VerifySha256(archivePath, addon.Sha256);
            ExtractZipSafely(archivePath, extractPath);

            var sourcePath = Path.Combine(extractPath, addon.ArchiveRootDirectory);
            if (!Directory.Exists(sourcePath))
            {
                throw new InvalidDataException($"Archive root '{addon.ArchiveRootDirectory}' was not found.");
            }

            CopyDirectory(sourcePath, stagingPath);

            if (!File.Exists(Path.Combine(stagingPath, "index.php")))
            {
                throw new InvalidDataException("Downloaded addon does not contain index.php.");
            }

            var backupPath = SwapInStagingDirectory(stagingPath, addon.InstallPath);
            try
            {
                ConfigureAddon(addon);
            }
            catch
            {
                RollbackInstallation(addon.InstallPath, backupPath);
                if (backupPath is null)
                {
                    DeleteAddonNginxConfig(addon);
                }
                throw;
            }

            TryDeleteDirectory(backupPath);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public Task RepairAsync(AddonDefinition addon, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(addon);
        EnsureInstallPathIsSafe(addon);
        using var addonLock = CrossProcessFileLock.Acquire(AddonLockPath(addon), TimeSpan.FromSeconds(30));

        if (!File.Exists(addon.EntryPointPath))
        {
            throw new InvalidOperationException($"{addon.DisplayName} is not installed.");
        }

        ConfigureAddon(addon);
        return Task.CompletedTask;
    }

    public Task UninstallAsync(AddonDefinition addon, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(addon);
        EnsureInstallPathIsSafe(addon);
        using var addonLock = CrossProcessFileLock.Acquire(AddonLockPath(addon), TimeSpan.FromSeconds(30));

        string? trashPath = null;
        if (Directory.Exists(addon.InstallPath))
        {
            var trashRoot = Path.Combine(_rootPath, "tmp", "addons", "trash");
            Directory.CreateDirectory(trashRoot);
            trashPath = Path.Combine(trashRoot, $"{addon.Key}-{Guid.NewGuid():N}");
            Directory.Move(addon.InstallPath, trashPath);
        }

        try
        {
            DeleteAddonNginxConfig(addon);
        }
        catch
        {
            if (trashPath is not null && Directory.Exists(trashPath) && !Directory.Exists(addon.InstallPath))
                Directory.Move(trashPath, addon.InstallPath);
            throw;
        }

        TryDeleteDirectory(trashPath);
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

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("Addon download URL must use HTTPS.");
        }

        await ArchiveSafety.DownloadToFileAsync(
            _httpClient,
            uri,
            destination,
            MaximumAddonDownloadBytes,
            "Addon",
            cancellationToken).ConfigureAwait(false);
    }

    internal static void VerifySha256(string filePath, string expectedSha256)
    {
        var normalizedExpectedSha256 = expectedSha256?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedExpectedSha256) || normalizedExpectedSha256.Length != 64)
        {
            throw new InvalidDataException("Invalid expected SHA-256 value.");
        }

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(normalizedExpectedSha256);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Invalid expected SHA-256 value.", ex);
        }

        using var stream = File.OpenRead(filePath);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException($"SHA-256 verification failed. Expected {Convert.ToHexString(expected).ToLowerInvariant()}, got {Convert.ToHexString(actual).ToLowerInvariant()}.");
        }
    }

    internal static void ExtractZipSafely(string archivePath, string destinationPath) =>
        ArchiveSafety.ExtractZipSafely(
            archivePath,
            destinationPath,
            MaximumAddonExtractedBytes,
            MaximumAddonArchiveEntries,
            "Addon");

    internal static bool IsPhpMyAdminConfigUsable(string content, int? expectedPort = null)
    {
        if (string.IsNullOrWhiteSpace(content) || !content.Contains("<?php", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requiredFragments = new[]
        {
            "$cfg['blowfish_secret']",
            "$cfg['Servers'][$i]['auth_type']",
            "$cfg['Servers'][$i]['host']",
            "$cfg['Servers'][$i]['port']",
            "$cfg['Servers'][$i]['AllowNoPassword'] = true",
            "$cfg['TempDir']"
        };
        if (!requiredFragments.All(fragment => content.Contains(fragment, StringComparison.Ordinal)))
            return false;
        return expectedPort is null ||
               content.Contains($"$cfg['Servers'][$i]['port'] = '{expectedPort.Value}';", StringComparison.Ordinal);
    }

    private void ConfigureAddon(AddonDefinition addon)
    {
        WriteAddonNginxConfig(addon);

        if (!addon.Key.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tempDirectory = Path.Combine(addon.InstallPath, "tmp");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(addon.InstallPath, "config.inc.php");
        var databasePort = ResolvePhpMyAdminPort();
        if (File.Exists(configPath))
        {
            var existing = File.ReadAllText(configPath);
            if (IsPhpMyAdminConfigUsable(existing, databasePort))
                return;
        }

        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var config = $$"""
<?php
$cfg['blowfish_secret'] = '{{secret}}';
$i = 0;
$i++;
$cfg['Servers'][$i]['auth_type'] = 'cookie';
$cfg['Servers'][$i]['host'] = '127.0.0.1';
$cfg['Servers'][$i]['port'] = '{{databasePort}}';
$cfg['Servers'][$i]['compress'] = false;
$cfg['Servers'][$i]['AllowNoPassword'] = true;
$cfg['TempDir'] = 'tmp';
""";
        AtomicWrite(configPath, config.Replace("\n", Environment.NewLine));
    }

    private int ResolvePhpMyAdminPort()
    {
        using var databases = new DatabaseRuntimeService(_rootPath);
        return SelectPhpMyAdminPort(databases.GetInstances());
    }

    internal static int SelectPhpMyAdminPort(IEnumerable<DatabaseRuntimeInstance> instances)
    {
        var selected = instances
            .Where(item => item.Engine is DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb)
            .OrderByDescending(item => item.State == ServiceState.Running)
            .ThenBy(item => item.Engine == DatabaseEngineKind.MySql ? 0 : 1)
            .ThenBy(item => item.Port)
            .FirstOrDefault();
        return selected?.Port ?? 3306;
    }

    private void WriteAddonNginxConfig(AddonDefinition addon)
    {
        if (!Uri.TryCreate(addon.LocalUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Addon local URL must use a valid .test host.");
        }

        var relativeRoot = Path.GetRelativePath(_rootPath, addon.InstallPath).Replace('\\', '/');
        if (relativeRoot.StartsWith("../", StringComparison.Ordinal) || relativeRoot == "..")
        {
            throw new InvalidOperationException("Addon install path escapes the DevBox root.");
        }

        var configPath = GetAddonNginxConfigPath(addon);
        var config = $$"""
server {
    listen 80;
    server_name {{uri.Host}};
    root {{relativeRoot}};
    index index.php index.html;

    location / {
        try_files $uri $uri/ /index.php?$query_string;
    }

    location ~ \.php$ {
        include config/nginx/fastcgi_params;
        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;
        fastcgi_pass 127.0.0.1:9084;
    }
}
""";
        AtomicWrite(configPath, config.Replace("\n", Environment.NewLine));
    }

    private void DeleteAddonNginxConfig(AddonDefinition addon)
    {
        var configPath = GetAddonNginxConfigPath(addon);
        if (File.Exists(configPath))
        {
            File.Delete(configPath);
        }
    }

    private string GetAddonNginxConfigPath(AddonDefinition addon)
    {
        var host = new Uri(addon.LocalUrl).Host;
        return Path.Combine(_rootPath, "config", "nginx", "sites-enabled", $"{host}.conf");
    }

    private string AddonLockPath(AddonDefinition addon)
    {
        var safeKey = new string(addon.Key.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
        return Path.Combine(_rootPath, "tmp", "locks", $"addon-{safeKey}.lock");
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void EnsureInstallPathIsSafe(AddonDefinition addon)
    {
        var wwwRoot = Path.GetFullPath(Path.Combine(_rootPath, "www")).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var installPath = Path.GetFullPath(addon.InstallPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!installPath.StartsWith(wwwRoot, StringComparison.OrdinalIgnoreCase) || installPath.Equals(wwwRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Addon install path must be a child of the DevBox www directory.");
        }
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

    private static string? SwapInStagingDirectory(string stagingPath, string installPath)
    {
        var installParent = Path.GetDirectoryName(Path.GetFullPath(installPath));
        if (string.IsNullOrWhiteSpace(installParent))
        {
            throw new InvalidOperationException("Addon install path has no parent directory.");
        }

        Directory.CreateDirectory(installParent);
        string? backupPath = null;
        if (Directory.Exists(installPath))
        {
            backupPath = installPath + $".backup-{Guid.NewGuid():N}";
            Directory.Move(installPath, backupPath);
        }

        try
        {
            Directory.Move(stagingPath, installPath);
            return backupPath;
        }
        catch
        {
            if (backupPath is not null && Directory.Exists(backupPath) && !Directory.Exists(installPath))
            {
                Directory.Move(backupPath, installPath);
            }
            throw;
        }
    }

    private static void RollbackInstallation(string installPath, string? backupPath)
    {
        DeleteDirectoryIfExists(installPath);
        if (backupPath is not null && Directory.Exists(backupPath))
        {
            Directory.Move(backupPath, installPath);
        }
    }

    private static void DeleteDirectoryIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }
        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            DeleteDirectoryIfExists(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
