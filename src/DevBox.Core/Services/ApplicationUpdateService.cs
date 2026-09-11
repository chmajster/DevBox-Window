using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class ApplicationUpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/chmajster/DevBox-Window/releases/latest";
    private const long MaximumReleaseMetadataBytes = 2L * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Version _currentVersion;
    private bool _disposed;

    public ApplicationUpdateService(Version currentVersion, HttpClient? httpClient = null)
    {
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevBox-Windows", NormalizeUserAgentVersion(currentVersion)));
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await ArchiveSafety.ReadContentBytesWithLimitAsync(
            response,
            MaximumReleaseMetadataBytes,
            "GitHub release metadata",
            cancellationToken).ConfigureAwait(false);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub release response contains invalid JSON.", ex);
        }
        using (document)
        {
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement) ||
            !root.TryGetProperty("html_url", out var urlElement))
        {
            throw new InvalidDataException("GitHub release response is missing tag_name or html_url.");
        }

        var latestVersion = ParseReleaseVersion(tagElement.GetString());
        var releaseUrl = urlElement.GetString();
        if (!Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps ||
            !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GitHub release response contains an invalid release URL.");
        }

        return new UpdateCheckResult(
            _currentVersion,
            latestVersion,
            latestVersion > _currentVersion,
            releaseUri.AbsoluteUri);
        }
    }

    internal static Version ParseReleaseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new InvalidDataException("Release tag is empty.");
        }

        var match = StableReleaseRegex().Match(tag.Trim());
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
        {
            throw new InvalidDataException($"Unsupported release tag '{tag}'. Expected vMAJOR.MINOR.PATCH.");
        }
        return version;
    }

    private static string NormalizeUserAgentVersion(Version version) =>
        $"{Math.Max(version.Major, 0)}.{Math.Max(version.Minor, 0)}.{Math.Max(version.Build, 0)}";

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

    [GeneratedRegex("^v?(\\d+\\.\\d+\\.\\d+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StableReleaseRegex();
}
