using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PhpManagerTests
{
    [Fact]
    public void SetExtensionEnabled_TogglesConfiguredExtensionAtomically()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(config);
            File.WriteAllLines(Path.Combine(config, "php.ini"), ["extension=curl", ";extension=gd"]);
            var manager = new PhpManager(root);

            Assert.True(manager.SetExtensionEnabled("gd", true));
            Assert.True(manager.GetExtensions().Single(item => item.Name == "gd").Enabled);
            Assert.True(manager.SetExtensionEnabled("curl", false));
            Assert.False(manager.GetExtensions().Single(item => item.Name == "curl").Enabled);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void GetExtensions_IgnoresExtensionDirDirective()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(config);
            File.WriteAllLines(Path.Combine(config, "php.ini"), ["extension_dir=ext", "extension=mysqli"]);
            var manager = new PhpManager(root);

            var extensions = manager.GetExtensions();

            Assert.Contains(extensions, extension => extension.Name == "mysqli" && extension.Enabled);
            Assert.DoesNotContain(extensions, extension => extension.Name == "ext");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SetExtensionEnabled_CollapsesDuplicateEntries()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(config);
            var phpIni = Path.Combine(config, "php.ini");
            File.WriteAllLines(phpIni, [";extension=mysqli", "extension=php_mysqli.dll ; duplicate"]);
            var manager = new PhpManager(root);

            Assert.True(manager.SetExtensionEnabled("mysqli", true));

            var matchingLines = File.ReadAllLines(phpIni)
                .Where(line => line.Contains("mysqli", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.Single(matchingLines);
            Assert.Equal("extension=mysqli", matchingLines[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SetExtensionEnabled_RecognizesInlineCommentAndCanonicalizesEntry()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(config);
            var phpIni = Path.Combine(config, "php.ini");
            File.WriteAllText(phpIni, "extension=php_curl.dll ; required\n");
            var manager = new PhpManager(root);

            Assert.True(manager.GetExtensions().Single(item => item.Name == "curl").Enabled);
            Assert.True(manager.SetExtensionEnabled("curl", true));

            var lines = File.ReadAllLines(phpIni);
            Assert.Single(lines);
            Assert.Equal("extension=curl", lines[0]);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SetExtensionEnabled_RejectsUnsafeName()
    {
        var root = TemporaryRoot();
        try
        {
            var config = Path.Combine(root, "config", "php");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "php.ini"), string.Empty);
            var manager = new PhpManager(root);

            Assert.Throws<ArgumentException>(() => manager.SetExtensionEnabled("../evil", true));
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
