using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class SiteMetadataIntegrityTests
{
    [Fact]
    public void GetSites_DuplicateDomain_IsQuarantined()
    {
        var root = TemporaryRoot();
        try
        {
            var metadata = PrepareMetadata(root);
            File.WriteAllText(metadata, JsonSerializer.Serialize(new[]
            {
                new SiteDefinition("one", "same.test", Path.Combine(root, "www", "one")),
                new SiteDefinition("two", "same.test", Path.Combine(root, "www", "two"))
            }));

            var sites = new SiteManager(root).GetSites();

            Assert.Empty(sites);
            Assert.False(File.Exists(metadata));
            Assert.Single(Directory.GetFiles(Path.GetDirectoryName(metadata)!, "sites.json.invalid-*.bak"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void GetSites_CollidingPhpPorts_IsQuarantined()
    {
        var root = TemporaryRoot();
        try
        {
            const string first = "8.4.0";
            const string second = "9.0.20882";
            Assert.Equal(PhpRuntimePoolManager.GetPort(first), PhpRuntimePoolManager.GetPort(second));

            var metadata = PrepareMetadata(root);
            File.WriteAllText(metadata, JsonSerializer.Serialize(new[]
            {
                new SiteDefinition("one", "one.test", Path.Combine(root, "www", "one"), PhpVersion: first),
                new SiteDefinition("two", "two.test", Path.Combine(root, "www", "two"), PhpVersion: second)
            }));

            var sites = new SiteManager(root).GetSites();

            Assert.Empty(sites);
            Assert.False(File.Exists(metadata));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string PrepareMetadata(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www", "one"));
        Directory.CreateDirectory(Path.Combine(root, "www", "two"));
        return Path.Combine(root, "config", "sites.json");
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-site-integrity-tests", Guid.NewGuid().ToString("N"));
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
