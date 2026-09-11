using DevBox.App.ViewModels;
using DevBox.Core.Models;

namespace DevBox.Tests;

public sealed class OnlineModuleDeliveryTests
{
    [Fact]
    public void RuntimeRow_RemotePhpPackage_ExposesDownloadAction()
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
    public void AddonRow_NotInstalled_UsesDownloadLabel()
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

        Assert.Equal("Download", row.InstallAction);
        Assert.Equal("Not installed", row.Status);
    }
}
