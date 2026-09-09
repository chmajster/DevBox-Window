using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AddonCatalogTests
{
    [Fact]
    public void PhpMyAdmin_IsRegisteredWithExpectedMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-addon-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetDefaultAddons());

            Assert.Equal("phpmyadmin", addon.Key);
            Assert.Equal("phpMyAdmin", addon.DisplayName);
            Assert.Equal("5.2.3", addon.Version);
            Assert.Equal("http://phpmyadmin.test", addon.LocalUrl);
            Assert.Equal(Path.Combine(root, "www", "phpmyadmin"), addon.InstallPath);
            Assert.Equal("2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f", addon.Sha256);
            Assert.StartsWith("https://files.phpmyadmin.net/", addon.DownloadUrl, StringComparison.Ordinal);
            Assert.Contains("mysqli", addon.RequiredPhpExtensions);
            Assert.Contains("mbstring", addon.RequiredPhpExtensions);
            Assert.Contains("openssl", addon.RequiredPhpExtensions);
            Assert.Contains("json", addon.RequiredPhpExtensions);
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
    public void PhpMyAdmin_IsInstalledOnlyWhenRealEntryPointExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-addon-tests", Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetDefaultAddons());

            Assert.False(catalog.IsInstalled(addon));
            File.WriteAllText(addon.EntryPointPath, "<?php echo 'phpMyAdmin';");
            Assert.True(catalog.IsInstalled(addon));
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
    public void RuntimeLayout_CreatesPhpMyAdminNginxSite()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-addon-tests", Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var config = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");

            Assert.True(File.Exists(config));
            var text = File.ReadAllText(config);
            Assert.Contains("server_name phpmyadmin.test", text);
            Assert.Contains("fastcgi_pass 127.0.0.1:9084", text);
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
