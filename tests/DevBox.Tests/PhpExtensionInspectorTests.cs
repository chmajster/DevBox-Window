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
    public void EnsureConfigured_DoesNotTreatExtensionDirAsConfiguredExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-php-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configDirectory = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(configDirectory);
            var phpIni = Path.Combine(configDirectory, "php.ini");
            File.WriteAllText(phpIni, "extension_dir=mysqli\n");
            var inspector = new PhpExtensionInspector(root);

            var changed = inspector.EnsureConfigured(["mysqli"]);

            Assert.True(changed);
            var lines = File.ReadAllLines(phpIni);
            Assert.Contains(lines, line => line.Equals("extension=mysqli", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void EnsureConfigured_RecognizesInlineCommentWithoutAddingDuplicate()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-php-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configDirectory = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(configDirectory);
            var phpIni = Path.Combine(configDirectory, "php.ini");
            File.WriteAllText(phpIni, "extension=php_mysqli.dll ; required by addon\n");
            var inspector = new PhpExtensionInspector(root);

            var changed = inspector.EnsureConfigured(["mysqli"]);

            Assert.False(changed);
            Assert.Single(File.ReadAllLines(phpIni));
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

    [Fact]
    public async Task CheckAsync_InvalidPhpExecutable_ReportsRuntimeUnavailableInsteadOfThrowing()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-php-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var runtimeDirectory = Path.Combine(root, "runtime", "php", "current");
            Directory.CreateDirectory(runtimeDirectory);
            File.WriteAllText(Path.Combine(runtimeDirectory, "php.exe"), "this is not a Windows executable");

            var inspector = new PhpExtensionInspector(root);
            var result = await inspector.CheckAsync(["mysqli"]);

            Assert.False(result.RuntimeAvailable);
            Assert.Contains("mysqli", result.MissingExtensions);
            Assert.Contains("Unable to start PHP CLI", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
