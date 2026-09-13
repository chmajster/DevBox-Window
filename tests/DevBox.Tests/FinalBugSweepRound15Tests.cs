using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound15Tests
{
    [Fact]
    public void TaskCenterRejectsReparseLogsRoot()
    {
        var root = NewRoot();
        var external = NewExternal("logs");
        var logs = Path.Combine(root, "logs");
        try
        {
            if (!TryCreateDirectoryLink(logs, external))
                return;

            Assert.Throws<InvalidOperationException>(() => new PlatformTaskCenter(root));
        }
        finally
        {
            TryDeleteLink(logs);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void EnvironmentShareRejectsReparseBackupsRoot()
    {
        var root = NewRoot();
        var external = NewExternal("backups");
        var backups = Path.Combine(root, "backups");
        try
        {
            if (!TryCreateDirectoryLink(backups, external))
                return;

            Assert.Throws<InvalidOperationException>(() => new RemoteEnvironmentService(root));
        }
        finally
        {
            TryDeleteLink(backups);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public async Task SelfUpdateRejectsReparseTempRootBeforeInstallerDownload()
    {
        var root = NewRoot();
        var external = NewExternal("tmp");
        var tmp = Path.Combine(root, "tmp");
        try
        {
            if (!TryCreateDirectoryLink(tmp, external))
                return;

            var version = new Version(9, 9, 9);
            var installerName = ApplicationSelfUpdateService.GetExpectedInstallerAssetName(version, RuntimeInformation.ProcessArchitecture);
            var release = $$"""
            {"tag_name":"v9.9.9","html_url":"https://github.com/chmajster/DevBox-Window/releases/tag/v9.9.9","assets":[{"name":"{{installerName}}","browser_download_url":"https://github.com/chmajster/DevBox-Window/releases/download/v9.9.9/{{installerName}}"},{"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/chmajster/DevBox-Window/releases/download/v9.9.9/SHA256SUMS.txt"}]}
            """;
            var checksum = new string('0', 64) + "  " + installerName + "\n";
            using var http = new HttpClient(new UpdateHandler(release, checksum));
            using var service = new ApplicationSelfUpdateService(root, http);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DownloadLatestInstallerAsync(new Version(0, 0, 0)));
        }
        finally
        {
            TryDeleteLink(tmp);
            Delete(root);
            Delete(external);
        }
    }

    private sealed class UpdateHandler(string release, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            var content = uri.EndsWith("/releases/latest", StringComparison.Ordinal)
                ? release
                : uri.EndsWith("/SHA256SUMS.txt", StringComparison.Ordinal)
                    ? checksum
                    : throw new InvalidOperationException("Installer download should not be reached when tmp is a reparse point.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
            });
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch { }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round15", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NewExternal(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"devbox-round15-{suffix}-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
