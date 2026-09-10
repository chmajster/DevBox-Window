using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class XdebugConfigurationServiceTests
{
    [Fact]
    public void Configure_EnablesXdebugAndWritesCanonicalDirectives()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var extensionDirectory = Path.Combine(root, "runtime", "php", "current", "ext");
            Directory.CreateDirectory(extensionDirectory);
            File.WriteAllText(Path.Combine(extensionDirectory, "php_xdebug.dll"), "binary");
            var phpIni = Path.Combine(root, "config", "php", "php.ini");
            File.AppendAllLines(phpIni,
            [
                ";zend_extension=php_xdebug.dll",
                "xdebug.mode=develop",
                "xdebug.client_port=9000",
                "xdebug.start_with_request=no"
            ]);

            var service = new XdebugConfigurationService(root);
            var status = service.Configure(new XdebugConfiguration(true, "debug,develop", 9003, "trigger"));

            Assert.True(status.Enabled);
            Assert.True(status.BinaryAvailable);
            Assert.Equal("debug,develop", status.Mode);
            Assert.Equal(9003, status.ClientPort);
            Assert.Equal("trigger", status.StartWithRequest);

            var text = File.ReadAllText(phpIni);
            Assert.Contains("zend_extension=php_xdebug.dll", text);
            Assert.Contains("xdebug.mode=debug,develop", text);
            Assert.Contains("xdebug.client_port=9003", text);
            Assert.Contains("xdebug.start_with_request=trigger", text);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Configure_DisablesAndCollapsesDuplicateXdebugDirectives()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var extensionDirectory = Path.Combine(root, "runtime", "php", "current", "ext");
            Directory.CreateDirectory(extensionDirectory);
            File.WriteAllText(Path.Combine(extensionDirectory, "php_xdebug.dll"), "binary");
            var phpIni = Path.Combine(root, "config", "php", "php.ini");
            File.AppendAllLines(phpIni,
            [
                "zend_extension=php_xdebug.dll",
                "; zend_extension = php_xdebug.dll",
                "xdebug.mode=debug",
                "xdebug.mode=profile"
            ]);

            var service = new XdebugConfigurationService(root);
            var status = service.Configure(new XdebugConfiguration(false, "debug", 9003, "trigger"));

            Assert.False(status.Enabled);
            var lines = File.ReadAllLines(phpIni);
            Assert.Single(lines, XdebugConfigurationService.IsXdebugZendExtension);
            Assert.Single(lines, line => line.TrimStart().StartsWith("xdebug.mode=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Configure_RequiresXdebugBinaryWhenEnabling()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = new XdebugConfigurationService(root);

            Assert.Throws<FileNotFoundException>(() => service.Configure(new XdebugConfiguration(true)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Configure_RejectsInvalidPort(int port)
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = new XdebugConfigurationService(root);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                service.Configure(new XdebugConfiguration(false, ClientPort: port)));
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
