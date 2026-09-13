using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AddonMarketplaceRegressionTests
{
    [Fact]
    public void SaveLocalCatalog_WritesPathsRelativeToDevBoxRoot()
    {
        var root = TemporaryRoot();
        try
        {
            var installPath = Path.Combine(root, "www", "local-addon");
            var addon = new AddonDefinition(
                "local-addon",
                "Local ADDON",
                "Regression fixture",
                installPath,
                Path.Combine(installPath, "index.php"),
                "http://local-addon.test",
                Array.Empty<string>(),
                "1.0.0",
                "https://example.test/local-addon.zip",
                new string('a', 64),
                "local-addon");

            using var marketplace = new AddonMarketplaceService(root);
            marketplace.SaveLocalCatalog([addon]);

            var catalogPath = Path.Combine(root, "config", "addons.local.json");
            using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
            var entry = Assert.Single(document.RootElement.EnumerateArray().ToArray());
            Assert.Equal("www/local-addon", entry.GetProperty("installRelativePath").GetString());
            Assert.Equal("www/local-addon/index.php", entry.GetProperty("entryPointRelativePath").GetString());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SaveLocalCatalog_RejectsAddonPathOutsideDevBoxRoot()
    {
        var root = TemporaryRoot();
        var outside = Path.Combine(Path.GetTempPath(), "outside-addon-" + Guid.NewGuid().ToString("N"));
        try
        {
            var addon = new AddonDefinition(
                "outside-addon",
                "Outside ADDON",
                "Regression fixture",
                outside,
                Path.Combine(outside, "index.php"),
                "http://outside-addon.test",
                Array.Empty<string>(),
                "1.0.0",
                "https://example.test/outside-addon.zip",
                new string('a', 64),
                "outside-addon");

            using var marketplace = new AddonMarketplaceService(root);
            Assert.Throws<InvalidDataException>(() => marketplace.SaveLocalCatalog([addon]));
        }
        finally
        {
            TryDelete(root);
            TryDelete(outside);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-marketplace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
