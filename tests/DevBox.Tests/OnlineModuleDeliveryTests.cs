using DevBox.App.ViewModels;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class OnlineModuleDeliveryTests
{
    [Fact]
    public void RuntimeRow_RemotePhpPackage_ExposesInstallAction()
    {
        var package = new RuntimePackageEntry
        {
            Key = "php",
            DisplayName = "PHP",
            Version = "8.5.10",
            Architecture = "x64",
            ExecutableRelativePath = "php-cgi.exe",
            DownloadUrl = "https://example.test/php.zip",
            Sha256 = new string('a', 64),
            Recommended = true
        };

        var row = new RuntimeRowViewModel(
            new RuntimeVersionStatus(package, Installed: false, Active: false, Valid: false, RuntimeSupportState.Current),
            Path.GetTempPath());

        Assert.Equal("Available online", row.Status);
        Assert.True(row.CanDownload);
        Assert.False(row.CanActivate);
        Assert.False(row.CanRemove);
        Assert.Equal(package.DownloadUrl, row.InstallPath);
    }

    [Fact]
    public void RuntimeRow_InstallProgress_ExposesPercentageAndStage()
    {
        var package = new RuntimePackageEntry
        {
            Key = "php",
            DisplayName = "PHP",
            Version = "8.5.10",
            Architecture = "x64",
            ExecutableRelativePath = "php-cgi.exe",
            DownloadUrl = "https://example.test/php.zip",
            Sha256 = new string('a', 64),
            Recommended = true
        };

        var row = new RuntimeRowViewModel(
            new RuntimeVersionStatus(package, Installed: false, Active: false, Valid: false, RuntimeSupportState.Current),
            Path.GetTempPath());

        row.BeginInstall();
        row.SetInstallProgress(42);

        Assert.False(row.CanDownload);
        Assert.Equal(42, row.ProgressPercent);
        Assert.Equal("42%", row.ProgressText);
        Assert.Equal("Downloading... 42%", row.Status);
    }

    [Fact]
    public void RuntimeRow_InstallProgress_IgnoresLateLowerPercentage()
    {
        var package = new RuntimePackageEntry
        {
            Key = "nginx",
            DisplayName = "Nginx",
            Version = "1.31.5",
            Architecture = "any",
            ExecutableRelativePath = "nginx.exe",
            DownloadUrl = "https://example.test/nginx.zip",
            Sha256 = new string('a', 64),
            Recommended = true
        };

        var row = new RuntimeRowViewModel(
            new RuntimeVersionStatus(package, Installed: false, Active: false, Valid: false, RuntimeSupportState.Current),
            Path.GetTempPath());

        row.BeginInstall();
        row.SetInstallProgress(82);
        row.SetInstallProgress(41);

        Assert.Equal(82, row.ProgressPercent);
        Assert.Equal("82%", row.ProgressText);
        Assert.Equal("Installing module... 82%", row.Status);
    }

    [Fact]
    public void RuntimePlatform_ReleaseGeneratedMysqlCatalog_EnablesInstall()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-online-module-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "runtime-catalog.json"), """
[
  {
    "key": "mysql",
    "displayName": "MySQL",
    "version": "8.4.11",
    "architecture": "x64",
    "executableRelativePath": "bin/mysqld.exe",
    "downloadUrl": "https://example.test/mysql.zip",
    "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "archiveRootDirectory": "mysql-8.4.11-winx64",
    "recommended": true
  }
]
""");

            using var service = new RuntimePlatformService(root);
            var status = Assert.Single(service.GetStatuses("mysql"));
            var row = new RuntimeRowViewModel(status, root);

            Assert.Equal("https://example.test/mysql.zip", status.Package.DownloadUrl);
            Assert.Equal(new string('a', 64), status.Package.Sha256);
            Assert.True(row.CanDownload);
            Assert.Equal("Available online", row.Status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddonRow_NotInstalled_UsesInstallLabel()
    {
        var addon = new AddonDefinition(
            "phpmyadmin",
            "phpMyAdmin",
            "Database UI",
            Path.Combine(Path.GetTempPath(), "www", "phpmyadmin"),
            Path.Combine(Path.GetTempPath(), "www", "phpmyadmin", "index.php"),
            "http://phpmyadmin.test",
            ["mysqli"],
            "5.2.3",
            "https://example.test/phpmyadmin.zip",
            new string('b', 64),
            "phpMyAdmin-5.2.3-all-languages");

        var row = new AddonRowViewModel(addon);
        row.ApplyInstallation(installed: false);

        Assert.Equal("Install", row.InstallAction);
        Assert.Equal("Not installed", row.Status);
    }
}
