using System.IO.Compression;
using System.Security.Cryptography;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class AddonInstaller
{
    private readonly HttpClient _httpClient;
    private readonly string _rootPath;

    public AddonInstaller(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task InstallAsync(AddonDefinition addon, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addon);

        var tempRoot = Path.Combine(_rootPath, "tmp", "addons", addon.Key, Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(tempRoot, "package.zip");
        var extractPath = Path.Combine(tempRoot, "extract");
        var stagingPath = Path.Combine(tempRoot, "staging");

        Directory.CreateDirectory(tempRoot);

        try
        {
            await DownloadAsync(addon.DownloadUrl, archivePath, cancellationToken);
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
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, cancellationToken);
    }

    internal static void VerifySha256(string filePath, string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64)
        {
            throw new InvalidDataException("Invalid expected SHA-256 value.");
        }

        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        var expected = expectedSha256.Trim().ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(expected)))
        {
            throw new InvalidDataException($"SHA-256 verification failed. Expected {expected}, got {actual}.");
        }
    }

    internal static void ExtractZipSafely(string archivePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var destinationRoot = Path.GetFullPath(destinationPath) + Path.DirectorySeparatorChar;

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

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var file in Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase);
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
