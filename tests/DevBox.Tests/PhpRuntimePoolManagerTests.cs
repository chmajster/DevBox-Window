using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PhpRuntimePoolManagerTests
{
    [Fact]
    public void GetPort_IsStableAndInDedicatedRange()
    {
        var first = PhpRuntimePoolManager.GetPort("8.5.10");
        var second = PhpRuntimePoolManager.GetPort("8.5.10");

        Assert.Equal(first, second);
        Assert.InRange(first, 20000, 49999);
    }

    [Theory]
    [InlineData("")]
    [InlineData("8.5")]
    [InlineData("../../8.5.10")]
    [InlineData("8.5.10-rc1")]
    public void GetPort_RejectsUnsupportedVersion(string version)
    {
        Assert.ThrowsAny<ArgumentException>(() => PhpRuntimePoolManager.GetPort(version));
    }

    [Fact]
    public void BuildVersionIni_RewritesExtensionDirectoryToRequestedRuntime()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime", "php", "8.5.10");
        var config = Path.Combine(root, "config", "php");
        Directory.CreateDirectory(Path.Combine(runtime, "ext"));
        Directory.CreateDirectory(config);
        File.WriteAllText(
            Path.Combine(config, "php.ini"),
            "display_errors=On\nextension_dir=runtime/php/current/ext\nextension=mysqli\n");

        try
        {
            using var manager = new PhpRuntimePoolManager(root);
            var generated = manager.BuildVersionIni("8.5.10", runtime);
            var content = File.ReadAllText(generated);
            var expected = Path.Combine(runtime, "ext").Replace('\\', '/');

            Assert.Contains($"extension_dir=\"{expected}\"", content, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime/php/current/ext", content, StringComparison.Ordinal);
            Assert.Contains("extension=mysqli", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void BuildVersionIni_RejectsRuntimeDirectoryForDifferentVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config", "php"));
        File.WriteAllText(Path.Combine(root, "config", "php", "php.ini"), "display_errors=On\n");

        try
        {
            using var manager = new PhpRuntimePoolManager(root);
            var wrongRuntime = Path.Combine(root, "runtime", "php", "8.4.0");
            Assert.Throws<InvalidOperationException>(() => manager.BuildVersionIni("8.5.10", wrongRuntime));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnsureRunningAsync_InvalidPhpCgi_ReportsControlledError()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "runtime", "php", "8.5.10");
        var config = Path.Combine(root, "config", "php");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(runtime, "php-cgi.exe"), "not a Windows executable");
        File.WriteAllText(Path.Combine(config, "php.ini"), "display_errors=On\n");

        try
        {
            using var manager = new PhpRuntimePoolManager(root);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.EnsureRunningAsync("8.5.10"));

            Assert.Contains("Unable to start PHP 8.5.10 FastCGI", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
