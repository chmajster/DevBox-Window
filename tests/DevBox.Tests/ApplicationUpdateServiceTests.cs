using System.Net;
using System.Net.Http;
using System.Text;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ApplicationUpdateServiceTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("2.0.1", 2, 0, 1)]
    public void ParseReleaseVersion_StableTag_ReturnsVersion(string tag, int major, int minor, int patch)
    {
        var version = ApplicationUpdateService.ParseReleaseVersion(tag);
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-beta.1")]
    public void ParseReleaseVersion_UnsupportedTag_Throws(string tag)
    {
        Assert.Throws<InvalidDataException>(() => ApplicationUpdateService.ParseReleaseVersion(tag));
    }

    [Fact]
    public async Task CheckAsync_NewerRelease_ReturnsUpdateAvailable()
    {
        const string payload = """
{"tag_name":"v1.4.0","html_url":"https://github.com/chmajster/DevBox-Window/releases/tag/v1.4.0"}
""";
        using var http = new HttpClient(new StaticHandler(payload));
        using var service = new ApplicationUpdateService(new Version(1, 3, 0), http);

        var result = await service.CheckAsync();

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 4, 0), result.LatestVersion);
        Assert.StartsWith("https://github.com/chmajster/DevBox-Window/releases/", result.ReleaseUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAsync_NonGithubUrl_IsRejected()
    {
        const string payload = """
{"tag_name":"v1.4.0","html_url":"https://example.com/fake"}
""";
        using var http = new HttpClient(new StaticHandler(payload));
        using var service = new ApplicationUpdateService(new Version(1, 3, 0), http);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    private sealed class StaticHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
    }
}
