using System.Net;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class BundledRuntimeTests
{
    [Fact]
    public async Task InstallAsync_ActivatesBundledRuntimeWithoutNetworkRequest()
    {
        var root = TemporaryRoot();
        try
        {
            var bundledPath = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(bundledPath);
            File.WriteAllText(Path.Combine(bundledPath, "mysqld.exe"), "bundled-runtime");

            using var client = new HttpClient(new FailingHandler());
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "mysql", "MySQL", "8.4.11", null, null, Path.Combine("bin", "mysqld.exe"));

            await manager.InstallAsync(definition);

            Assert.True(File.Exists(Path.Combine(root, "runtime", "mysql", "current", "bin", "mysqld.exe")));
            var installed = Assert.Single(manager.GetInstalled("mysql", Path.Combine("bin", "mysqld.exe")));
            Assert.True(installed.IsActive);
            Assert.True(installed.IsValid);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task InstallAsync_BundledOnlyRuntimeMissing_FailsWithoutNetworkFallback()
    {
        var root = TemporaryRoot();
        try
        {
            using var client = new HttpClient(new FailingHandler());
            using var manager = new RuntimeManager(root, client);
            var definition = new RuntimeDefinition(
                "mysql", "MySQL", "8.4.11", null, null, Path.Combine("bin", "mysqld.exe"));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.InstallAsync(definition));

            Assert.Contains("not bundled", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Readiness_AllowsAutomaticMySqlActivationOnlyWhenBundleExists()
    {
        var root = TemporaryRoot();
        try
        {
            var catalog = new RuntimeCatalog();
            var readiness = new EnvironmentReadinessService(root, catalog);

            var missing = readiness.Check().Items.Single(item => item.Key == "mysql");
            Assert.False(missing.Ready);
            Assert.False(missing.CanInstallAutomatically);

            var bundledPath = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(bundledPath);
            File.WriteAllText(Path.Combine(bundledPath, "mysqld.exe"), "bundled-runtime");

            var bundled = readiness.Check().Items.Single(item => item.Key == "mysql");
            Assert.False(bundled.Ready);
            Assert.True(bundled.CanInstallAutomatically);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bundled runtime activation must not access the network.");
    }
}
