using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class AddonCatalogTests
{
    [Fact]
    public void PhpMyAdmin_IsRegisteredWithExpectedMetadata()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetAddons());

            Assert.True(File.Exists(catalog.CatalogPath));
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
            DeleteRoot(root);
        }
    }

    [Fact]
    public void PhpMyAdmin_UserDirectoryWithoutOwnership_IsNotReportedAsInstalled()
    {
        var root = TempRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetAddons());

            Assert.False(catalog.IsInstalled(addon));
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "<?php echo 'user project';");
            Assert.False(catalog.IsInstalled(addon));

            AddonOwnership.WriteMarker(addon);
            Assert.True(catalog.IsInstalled(addon));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void PhpMyAdmin_LegacyDevBoxVhost_IsRecognizedForMigration()
    {
        var root = TempRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetAddons());
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "<?php echo 'legacy';");
            var vhost = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");
            File.WriteAllText(vhost, "server { server_name phpmyadmin.test; root www/phpmyadmin; }");

            Assert.True(catalog.IsInstalled(addon));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_PathTraversal_IsRejected()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "key": "unsafe",
    "displayName": "Unsafe",
    "description": "test",
    "installRelativePath": "../escape",
    "entryPointRelativePath": "../escape/index.php",
    "localUrl": "http://unsafe.test",
    "requiredPhpExtensions": [],
    "version": "1.0",
    "downloadUrl": "https://example.test/unsafe.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "archiveRootDirectory": "package"
  }
]
""");

            Assert.Throws<InvalidDataException>(() => catalog.GetAddons());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_MissingKey_IsRejectedAsInvalidData()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "displayName": "Broken",
    "description": "test",
    "installRelativePath": "www/broken",
    "entryPointRelativePath": "www/broken/index.php",
    "localUrl": "http://broken.test",
    "requiredPhpExtensions": [],
    "version": "1.0",
    "downloadUrl": "https://example.test/broken.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "archiveRootDirectory": "package"
  }
]
""");

            Assert.Throws<InvalidDataException>(() => catalog.GetAddons());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_MissingSha256_IsRejectedAsInvalidData()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "key": "broken",
    "displayName": "Broken",
    "description": "test",
    "installRelativePath": "www/broken",
    "entryPointRelativePath": "www/broken/index.php",
    "localUrl": "http://broken.test",
    "requiredPhpExtensions": [],
    "version": "1.0",
    "downloadUrl": "https://example.test/broken.zip",
    "archiveRootDirectory": "package"
  }
]
""");

            Assert.Throws<InvalidDataException>(() => catalog.GetAddons());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_WwwRootInstallPath_IsRejected()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "key": "unsafe",
    "displayName": "Unsafe",
    "description": "test",
    "installRelativePath": "www",
    "entryPointRelativePath": "www/index.php",
    "localUrl": "http://unsafe.test",
    "requiredPhpExtensions": [],
    "version": "1.0",
    "downloadUrl": "https://example.test/unsafe.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "archiveRootDirectory": "package"
  }
]
""");

            Assert.Throws<InvalidDataException>(() => catalog.GetAddons());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_PhpExtensionNames_AreNormalized()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "key": "normalized",
    "displayName": "Normalized",
    "description": "test",
    "installRelativePath": "www/normalized",
    "entryPointRelativePath": "www/normalized/index.php",
    "localUrl": "http://normalized.test",
    "requiredPhpExtensions": [" PHP_MYSQLI.DLL ", "mysqli"],
    "version": "1.0",
    "downloadUrl": "https://example.test/normalized.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "archiveRootDirectory": "package"
  }
]
""");

            var addon = Assert.Single(catalog.GetAddons());

            Assert.Equal(new[] { "mysqli" }, addon.RequiredPhpExtensions);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Manifest_PhpExtensionDirectiveInjection_IsRejected()
    {
        var root = TempRoot();
        try
        {
            var catalog = new AddonCatalog(root);
            Directory.CreateDirectory(Path.GetDirectoryName(catalog.CatalogPath)!);
            File.WriteAllText(catalog.CatalogPath, """
[
  {
    "key": "unsafe-extension",
    "displayName": "Unsafe extension",
    "description": "test",
    "installRelativePath": "www/unsafe-extension",
    "entryPointRelativePath": "www/unsafe-extension/index.php",
    "localUrl": "http://unsafe-extension.test",
    "requiredPhpExtensions": ["mysqli\nextension=evil"],
    "version": "1.0",
    "downloadUrl": "https://example.test/unsafe-extension.zip",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "archiveRootDirectory": "package"
  }
]
""");

            Assert.Throws<InvalidDataException>(() => catalog.GetAddons());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void RuntimeLayout_DoesNotCreateAddonOwnedVhost()
    {
        var root = TempRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var config = Path.Combine(root, "config", "nginx", "sites-enabled", "phpmyadmin.test.conf");

            Assert.False(File.Exists(config));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-addon-tests", Guid.NewGuid().ToString("N"));
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
