using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PhpExtensionInspectorTests
{
    [Fact]
    public void EnsureConfigured_AddsMissingExtensionsWithoutDuplicatingConfiguredOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-php-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configDirectory = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(configDirectory);
            var phpIni = Path.Combine(configDirectory, "php.ini");
            File.WriteAllText(phpIni, "extension=mysqli\n;extension=mbstring\n");
            var inspector = new PhpExtensionInspector(root);

            var changed = inspector.EnsureConfigured(["mysqli", "mbstring", "openssl", "json"]);

            Assert.True(changed);
            var content = File.ReadAllText(phpIni);
            Assert.Equal(1, content.Split("extension=mysqli", StringSplitOptions.None).Length - 1);
            Assert.Contains("extension=mbstring", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("extension=openssl", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("extension=json", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CheckAsync_MissingPhpRuntime_ReportsRequiredExtensionsAsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-php-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var inspector = new PhpExtensionInspector(root);
            var result = await inspector.CheckAsync(["mysqli", "mbstring"]);

            Assert.False(result.RuntimeAvailable);
            Assert.Contains("mysqli", result.MissingExtensions);
            Assert.Contains("mbstring", result.MissingExtensions);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
