using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonInstaller : IDisposable
{
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

            ReplaceDirectory(stagingPath, addon.InstallPath);
            ConfigureAddon(addon);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    public Task RepairAsync(AddonDefinition addon, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(addon);
        EnsureInstallPathIsSafe(addon);

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

        if (!Directory.Exists(addon.InstallPath))
        {
            return Task.CompletedTask;
        }

        var trashRoot = Path.Combine(_rootPath, "tmp", "addons", "trash");
        Directory.CreateDirectory(trashRoot);
        var trashPath = Path.Combine(trashRoot, $"{addon.Key}-{Guid.NewGuid():N}");
        Directory.Move(addon.InstallPath, trashPath);
        Directory.Delete(trashPath, recursive: true);
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

        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    internal static void VerifySha256(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
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
            throw new InvalidDataException($"SHA-256 verification failed. Expected {Convert.ToHexString(expected).ToLowerInvariant()}, got {Convert.ToHexString(actual).ToLowerInvariant()}.");
        }
    }

    internal static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var destinationRoot = Path.GetFullPath(destinationPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var outputPath = Path.GetFullPath(Path.Combine(destinationPath, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
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

    private void ConfigureAddon(AddonDefinition addon)
    {
        if (!addon.Key.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var tempDirectory = Path.Combine(addon.InstallPath, "tmp");
        Directory.CreateDirectory(tempDirectory);
        var configPath = Path.Combine(addon.InstallPath, "config.inc.php");
        if (File.Exists(configPath))
        {
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
$cfg['Servers'][$i]['port'] = '3306';
$cfg['Servers'][$i]['compress'] = false;
$cfg['Servers'][$i]['AllowNoPassword'] = false;
$cfg['TempDir'] = 'tmp';
""";
        File.WriteAllText(configPath, config.Replace("\n", Environment.NewLine));
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

    private static void ReplaceDirectory(string stagingPath, string installPath)
    {
        var backupPath = installPath + ".backup";
        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, recursive: true);
        }

        if (Directory.Exists(installPath))
        {
            Directory.Move(installPath, backupPath);
        }

        try
        {
            Directory.Move(stagingPath, installPath);
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
        }
        catch
        {
            if (Directory.Exists(installPath))
            {
                Directory.Delete(installPath, recursive: true);
            }

            if (Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, installPath);
            }

            throw;
        }
    }
}
