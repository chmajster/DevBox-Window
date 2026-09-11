using System.Runtime.InteropServices;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound3Tests
{
    [Fact]
    public void RuntimeArchitecture_AllowsX64FallbackOnWindowsArm64()
    {
        Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("arm64", Architecture.Arm64));
        Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("any", Architecture.Arm64));
        if (OperatingSystem.IsWindows())
            Assert.True(RuntimePlatformService.IsPackageArchitectureCompatible("x64", Architecture.Arm64));
        Assert.False(RuntimePlatformService.IsPackageArchitectureCompatible("arm64", Architecture.X64));
    }

    [Fact]
    public void SiteManager_ConcurrentWritersDoNotLoseSites()
    {
        var root = TestRoot();
        try
        {
            var first = new SiteManager(root);
            var second = new SiteManager(root);
            Parallel.Invoke(
                () => first.Create("alpha"),
                () => second.Create("beta"));

            var names = new SiteManager(root).GetSites().Select(site => site.Name).OrderBy(value => value).ToArray();
            Assert.Equal(new[] { "alpha", "beta" }, names);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ApplyProfile_DoesNotWriteLockWhenPrerequisiteFails()
    {
        var root = TestRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest(
                "demo",
                null,
                ProjectKind.EmptyPhp,
                null,
                false,
                "none",
                null,
                null,
                Array.Empty<string>(),
                Array.Empty<string>()));

            new EnvironmentProfileService(root).SaveCustomProfile(new EnvironmentProfile
            {
                Key = "missing-runtime",
                DisplayName = "Missing runtime",
                Kind = ProjectKind.EmptyPhp,
                Runtimes = new Dictionary<string, string> { ["not-installed"] = "1.0" },
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false
            });

            var project = Path.Combine(root, "www", "demo");
            using var service = new EnvironmentLockService(root);
            var result = await service.ApplyProfileAsync(project, "missing-runtime");

            Assert.NotEmpty(result.Warnings);
            Assert.False(File.Exists(Path.Combine(project, EnvironmentLockService.LockFileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ConfigurationRestore_RejectsBackupFromAnotherConfigurationKind()
    {
        var root = TestRoot();
        try
        {
            var backupRoot = Path.Combine(root, "backups", "configuration");
            Directory.CreateDirectory(backupRoot);
            var wrong = Path.Combine(backupRoot, "nginx-20260911.conf.bak");
            File.WriteAllText(wrong, "events {}\nhttp {}");
            var service = new ConfigurationFileService(root);
            Assert.Throws<InvalidOperationException>(() => service.RestoreBackup("mysql", wrong));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void PhpMyAdminPort_PrefersRunningRegisteredDatabase()
    {
        var instances = new[]
        {
            new DatabaseRuntimeInstance(DatabaseEngineKind.MySql, "8.4", 3307, "", "", "", true, ServiceState.Stopped, null),
            new DatabaseRuntimeInstance(DatabaseEngineKind.MariaDb, "11", 3316, "", "", "", true, ServiceState.Running, 42)
        };
        Assert.Equal(3316, AddonInstaller.SelectPhpMyAdminPort(instances));
        Assert.Equal(3306, AddonInstaller.SelectPhpMyAdminPort(Array.Empty<DatabaseRuntimeInstance>()));
    }

    private static string TestRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
