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
            Assert.Equal("http://phpmyadmin.test", addon.LocalUrl);
            Assert.Equal(Path.Combine(root, "www", "phpmyadmin"), addon.InstallPath);
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
}
