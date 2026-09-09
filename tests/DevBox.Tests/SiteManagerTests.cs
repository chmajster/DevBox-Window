using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class SiteManagerTests
{
    [Fact]
    public void Create_WritesMetadataDocumentRootAndNginxConfig()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new SiteManager(root);

            var site = manager.Create("demo");

            Assert.Equal("demo.test", site.Domain);
            Assert.True(File.Exists(Path.Combine(root, "www", "demo", "index.php")));
            Assert.True(File.Exists(Path.Combine(root, "config", "nginx", "sites-enabled", "demo.test.conf")));
            Assert.True(File.Exists(Path.Combine(root, "config", "sites.json")));
            Assert.Single(manager.GetSites());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SetHttps_UpdatesMetadataAndWritesTlsVhost()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new SiteManager(root);
            manager.Create("demo");

            var updated = manager.SetHttps("demo", true);
            var config = File.ReadAllText(manager.GetNginxConfigPath("demo.test"));

            Assert.True(updated.HttpsEnabled);
            Assert.True(manager.GetSites().Single().HttpsEnabled);
            Assert.Contains("listen 443 ssl;", config, StringComparison.Ordinal);
            Assert.Contains("ssl_certificate config/ssl/sites/demo.test.crt.pem;", config, StringComparison.Ordinal);
            Assert.Contains("return 301 https://$host$request_uri;", config, StringComparison.Ordinal);

            manager.SetHttps("demo", false);
            config = File.ReadAllText(manager.GetNginxConfigPath("demo.test"));
            Assert.DoesNotContain("listen 443 ssl;", config, StringComparison.Ordinal);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_RejectsNonTestDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new SiteManager(root);
            Assert.Throws<ArgumentException>(() => manager.Create("demo", "demo.local"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_RejectsDocumentRootOutsideDevBox()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new SiteManager(root);
            var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<InvalidOperationException>(() => manager.Create("demo", documentRoot: outside));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Delete_RemovesMetadataAndVhostButKeepsFilesByDefault()
    {
        var root = TemporaryRoot();
        try
        {
            var manager = new SiteManager(root);
            var site = manager.Create("demo");

            manager.Delete("demo");

            Assert.Empty(manager.GetSites());
            Assert.False(File.Exists(manager.GetNginxConfigPath(site.Domain)));
            Assert.True(Directory.Exists(site.DocumentRoot));
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
}
