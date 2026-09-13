using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AddonOwnershipRegressionTests
{
    [Fact]
    public void LegacyVhost_PrefixRootDoesNotClaimOwnership()
    {
        var root = NewRoot();
        try
        {
            var addon = Definition(root);
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "user-owned");
            var vhost = VhostPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(vhost)!);
            File.WriteAllText(vhost, """
server {
    listen 80;
    server_name phpmyadmin.test;
    root www/phpmyadmin-backup;
}
""");

            Assert.False(AddonOwnership.IsOwned(root, addon));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void LegacyVhost_CommentedManagedDirectivesDoNotClaimOwnership()
    {
        var root = NewRoot();
        try
        {
            var addon = Definition(root);
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "user-owned");
            var vhost = VhostPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(vhost)!);
            File.WriteAllText(vhost, """
server {
    server_name phpmyadmin.test;
    root www/custom-phpmyadmin;
    # root www/phpmyadmin;
}
""");

            Assert.False(AddonOwnership.IsOwned(root, addon));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void LegacyVhost_ExactGeneratedDirectivesAreRecognized()
    {
        var root = NewRoot();
        try
        {
            var addon = Definition(root);
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "legacy-managed");
            var vhost = VhostPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(vhost)!);
            File.WriteAllText(vhost, """
server {
    listen 80;
    server_name phpmyadmin.test;
    root www/phpmyadmin;
}
""");

            Assert.True(AddonOwnership.IsOwned(root, addon));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static AddonDefinition Definition(string root)
    {
        var install = Path.Combine(root, "www", "phpmyadmin");
        return new AddonDefinition(
            "phpmyadmin",
            "phpMyAdmin",
            "Database UI",
            install,
            Path.Combine(install, "index.php"),
            "http://phpmyadmin.test",
            ["mysqli"],
            "5.2.3",
            "https://example.test/phpmyadmin.zip",
            new string('a', 64),
            "phpMyAdmin-5.2.3-all-languages");
    }

    private static string VhostPath(string root) =>
        Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-addon-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
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
