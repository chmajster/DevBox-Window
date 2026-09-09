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
