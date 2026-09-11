using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ApplicationSelfUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private const long MaximumChecksumBytes = 1024 * 1024;
    private const long MaximumInstallerDownloadBytes = 1024L * 1024 * 1024;
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

        using var releaseResponse = await _httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        releaseResponse.EnsureSuccessStatusCode();
        var releasePayload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            releaseResponse,
            MaximumReleaseMetadataBytes,
            "GitHub release metadata",
            cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(releasePayload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub release response contains invalid JSON.", ex);
        }
        using var releaseDocument = document;
        var root = releaseDocument.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement) || tagElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("html_url", out var releaseUrlElement) || releaseUrlElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("GitHub release response is missing tag_name or html_url.");

        var version = ApplicationUpdateService.ParseReleaseVersion(tagElement.GetString());
        if (version <= currentVersion)
            throw new InvalidOperationException("No newer stable DevBox release is available.");

        var releaseUrl = ValidateGitHubUrl(releaseUrlElement.GetString(), "release URL");
        var expectedInstallerName = GetExpectedInstallerAssetName(version);

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

        using var checksumsResponse = await _httpClient.GetAsync(checksumsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        checksumsResponse.EnsureSuccessStatusCode();
        var checksumsPayload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            checksumsResponse,
            MaximumChecksumBytes,
            "SHA256SUMS.txt",
            cancellationToken).ConfigureAwait(false);
        var checksums = Encoding.UTF8.GetString(checksumsPayload);
        var expectedSha256 = ParseChecksum(checksums, expectedInstallerName);

        var updateDirectory = Path.Combine(_rootPath, "tmp", "updates", version.ToString(3));
        Directory.CreateDirectory(updateDirectory);
        var installerPath = Path.Combine(updateDirectory, expectedInstallerName);
        var temporaryPath = installerPath + $".{Guid.NewGuid():N}.download";

        try
        {
            await ArchiveSafety.DownloadToFileAsync(
                _httpClient,
                new Uri(installerUrl),
                temporaryPath,
                MaximumInstallerDownloadBytes,
                "DevBox installer",
                cancellationToken).ConfigureAwait(false);

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

    internal static string GetExpectedInstallerAssetName(Version version, Architecture? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        var effectiveArchitecture = architecture ?? RuntimeInformation.ProcessArchitecture;
        var rid = effectiveArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException($"DevBox self-update does not support {effectiveArchitecture}.")
        };
        return $"DevBox-{version.ToString(3)}-{rid}-setup.exe";
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
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Trim().Length != 64)
            throw new InvalidDataException("Expected installer SHA-256 must contain 64 hexadecimal characters.");
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedSha256.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Expected installer SHA-256 is invalid.", ex);
        }
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
