using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ApplicationSelfUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private readonly string _rootPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public ApplicationSelfUpdateService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevBox-Windows-Updater", "1.0"));
    }

    public async Task<SelfUpdatePackage> DownloadLatestInstallerAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var releaseResponse = await _httpClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(releaseStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var root = document.RootElement;
        var version = ApplicationUpdateService.ParseReleaseVersion(root.GetProperty("tag_name").GetString());
        if (version <= currentVersion)
            throw new InvalidOperationException("No newer stable DevBox release is available.");

        var releaseUrl = ValidateGitHubUrl(root.GetProperty("html_url").GetString(), "release URL");
        var expectedInstallerName = $"DevBox-{version.ToString(3)}-win-x64-setup.exe";

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub release does not contain an assets array.");

        string? installerUrl = null;
        string? checksumsUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlElement) ? urlElement.GetString() : null;
            if (name is null || url is null)
                continue;

            if (name.Equals(expectedInstallerName, StringComparison.OrdinalIgnoreCase))
                installerUrl = ValidateGitHubUrl(url, "installer download URL");
            else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                checksumsUrl = ValidateGitHubUrl(url, "checksum download URL");
        }

        if (installerUrl is null)
            throw new InvalidDataException($"Release is missing expected installer '{expectedInstallerName}'.");
        if (checksumsUrl is null)
            throw new InvalidDataException("Release is missing SHA256SUMS.txt.");

        var checksums = await _httpClient.GetStringAsync(checksumsUrl, cancellationToken).ConfigureAwait(false);
        var expectedSha256 = ParseChecksum(checksums, expectedInstallerName);

        var updateDirectory = Path.Combine(_rootPath, "tmp", "updates", version.ToString(3));
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, expectedInstallerName);
        var temporaryPath = installerPath + $".{Guid.NewGuid():N}.download";

        try
        {
            using var response = await _httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);

            VerifySha256(temporaryPath, expectedSha256);
            File.Move(temporaryPath, installerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        return new SelfUpdatePackage(version, installerPath, expectedInstallerName, expectedSha256, releaseUrl);
    }

    internal static string ParseChecksum(string checksumFile, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        foreach (var rawLine in checksumFile.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawLine.IndexOfAny([' ', '\t']);
            if (separator <= 0)
                continue;
            var hash = rawLine[..separator].Trim();
            var listedName = rawLine[separator..].Trim().TrimStart('*');
            if (!listedName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException($"SHA256SUMS.txt contains an invalid hash for '{fileName}'.");
            return hash.ToLowerInvariant();
        }
        throw new InvalidDataException($"SHA256SUMS.txt does not contain '{fileName}'.");
    }

    internal static void VerifySha256(string path, string expectedSha256)
    {
        var expected = Convert.FromHexString(expectedSha256);
        using var stream = File.OpenRead(path);
        var actual = SHA256.HashData(stream);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            throw new InvalidDataException("Downloaded DevBox installer failed SHA-256 verification.");
    }

    private static string ValidateGitHubUrl(string? value, string description)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"GitHub release contains an invalid {description}.");
        return uri.AbsoluteUri;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
