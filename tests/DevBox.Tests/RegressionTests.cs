using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RegressionTests
{
    [Fact]
    public void MySqlDataDirectory_RequiresSystemDatabase()
    {
        var root = TemporaryRoot();
        try
        {
            var data = Path.Combine(root, "data", "mysql");
            Directory.CreateDirectory(data);

            Assert.False(ProcessManager.IsMySqlDataDirectoryInitialized(data));

            Directory.CreateDirectory(Path.Combine(data, "mysql"));
            Assert.True(ProcessManager.IsMySqlDataDirectoryInitialized(data));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void RuntimeManager_SortsVersionsSemantically()
    {
        var root = TemporaryRoot();
        try
        {
            var phpRoot = Path.Combine(root, "runtime", "php");
            Directory.CreateDirectory(Path.Combine(phpRoot, "8.9.0"));
            Directory.CreateDirectory(Path.Combine(phpRoot, "8.10.0"));
            File.WriteAllText(Path.Combine(phpRoot, "8.9.0", "php-cgi.exe"), "test");
            File.WriteAllText(Path.Combine(phpRoot, "8.10.0", "php-cgi.exe"), "test");

            using var client = new HttpClient();
            using var manager = new RuntimeManager(root, client);
            var installed = manager.GetInstalled("php", "php-cgi.exe");

            Assert.Equal("8.10.0", installed[0].Version);
            Assert.Equal("8.9.0", installed[1].Version);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void DeveloperTool_CommandScript_UsesCommandProcessor()
    {
        var startInfo = DeveloperToolsService.BuildStartInfo(
            @"C:\Tools\npm.cmd",
            new[] { "--version" });

        Assert.EndsWith("cmd.exe", startInfo.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/c", startInfo.ArgumentList);
        Assert.Contains(startInfo.ArgumentList, argument => argument.Contains("npm.cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PhpMyAdminRepairValidator_RejectsIncompleteConfig()
    {
        Assert.False(AddonInstaller.IsPhpMyAdminConfigUsable("<?php\n$cfg['blowfish_secret'] = 'x';"));

        var valid = """
<?php
$cfg['blowfish_secret'] = 'secret';
$cfg['Servers'][$i]['auth_type'] = 'cookie';
$cfg['Servers'][$i]['host'] = '127.0.0.1';
$cfg['Servers'][$i]['port'] = '3306';
$cfg['Servers'][$i]['AllowNoPassword'] = true;
$cfg['TempDir'] = 'tmp';
""";
        Assert.True(AddonInstaller.IsPhpMyAdminConfigUsable(valid));
    }

    [Fact]
    public void HostsEnsureMapping_RemovesConflictingEntriesForSameDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var hosts = Path.Combine(root, "hosts");
            File.WriteAllLines(hosts,
            [
                "::1 demo.test # old mapping",
                "127.0.0.1 other.test",
                "127.0.0.1 demo.test # duplicate"
            ]);

            var manager = new HostsFileManager(hosts);
            manager.EnsureMapping("127.0.0.1", "demo.test");

            var lines = File.ReadAllLines(hosts);
            Assert.Single(lines, line => line.Contains("demo.test", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("127.0.0.1 demo.test # DevBox", lines);
            Assert.Contains("127.0.0.1 other.test", lines);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SiteManager_RejectsDocumentRootOutsideWww()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var manager = new SiteManager(root);
            var unsafeRoot = Path.Combine(root, "config", "site-files");

            var error = Assert.Throws<InvalidOperationException>(() =>
                manager.Create("demo", documentRoot: unsafeRoot));

            Assert.Contains("www", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-regression-tests", Guid.NewGuid().ToString("N"));
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
