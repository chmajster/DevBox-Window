using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound16Tests
{
    [Fact]
    public void AddonOwnership_IncompleteMarkerDoesNotClaimDirectory()
    {
        var root = NewRoot();
        try
        {
            var addon = Definition(root);
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "managed?");
            File.WriteAllText(Path.Combine(addon.InstallPath, AddonOwnership.MarkerFileName), "phpmyadmin\n");

            Assert.False(AddonOwnership.IsOwned(root, addon, allowLegacyVhost: false));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void AddonOwnership_ValidOlderVersionMarkerSupportsUpgrade()
    {
        var root = NewRoot();
        try
        {
            var addon = Definition(root);
            Directory.CreateDirectory(addon.InstallPath);
            File.WriteAllText(addon.EntryPointPath, "managed");
            File.WriteAllText(
                Path.Combine(addon.InstallPath, AddonOwnership.MarkerFileName),
                "phpmyadmin\n5.2.2\n");

            Assert.True(AddonOwnership.IsOwned(root, addon, allowLegacyVhost: false));
        }
        finally { Delete(root); }
    }

    [Theory]
    [InlineData("http://bad..test")]
    [InlineData("http://-bad.test")]
    [InlineData("http://bad-.test")]
    [InlineData("http://good.test?redirect=evil")]
    [InlineData("http://user@good.test")]
    public void AddonLocalUrl_RejectsMalformedOrAmbiguousUrls(string localUrl)
    {
        Assert.Throws<InvalidDataException>(() => AddonCatalog.ValidateLocalUrl(localUrl, "test-addon"));
    }

    [Fact]
    public void AddonCatalog_DirectConstructionRejectsReparseConfigRoot()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-addon-config-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var config = Path.Combine(root, "config");
        try
        {
            if (!TryCreateDirectoryLink(config, external))
                return;

            Assert.Throws<InvalidOperationException>(() => new AddonCatalog(root));
        }
        finally
        {
            TryDeleteLink(config);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public async Task AddonInstaller_DirectInstallRejectsReparseTmpRootBeforeDownload()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-addon-tmp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var tmp = Path.Combine(root, "tmp");
        try
        {
            if (!TryCreateDirectoryLink(tmp, external))
                return;

            using var installer = new AddonInstaller(root);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(Definition(root)));
            Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteLink(tmp);
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void InstallerScript_RequiresDedicatedVerifiedInstallRootAndCompleteAddonMarker()
    {
        var repositoryRoot = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(repositoryRoot, "installer", "DevBox.iss"));

        Assert.Contains("IsUnsafeSharedRoot", script, StringComparison.Ordinal);
        Assert.Contains("not HasDevBoxInstallEvidence(WizardDirValue)", script, StringComparison.Ordinal);
        Assert.Contains("ApprovedReinstallDirectory", script, StringComparison.Ordinal);
        Assert.Contains("GetArrayLength(MarkerLines) = 2", script, StringComparison.Ordinal);
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

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DevBox.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "installer", "DevBox.iss")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the DevBox repository root for installer regression validation.");
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch { }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "www"));
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
