using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class PostMergeRegressionTests
{
    [Fact]
    public void DatabaseRuntime_RegisterAcrossInstances_DoesNotLoseRegistrations()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), "[]");
            const int count = 12;
            for (var i = 0; i < count; i++)
            {
                var runtime = Path.Combine(root, "runtime", "mysql", $"8.4.{i}", "bin");
                Directory.CreateDirectory(runtime);
                File.WriteAllBytes(Path.Combine(runtime, "mysqld.exe"), []);
            }

            Parallel.For(0, count, i =>
            {
                using var service = new DatabaseRuntimeService(root);
                _ = service.Register("mysql", $"8.4.{i}", 3500 + i);
            });

            using var verification = new DatabaseRuntimeService(root);
            var registrations = verification.GetInstances("mysql");
            Assert.Equal(count, registrations.Count);
            Assert.Equal(count, registrations.Select(item => item.Port).Distinct().Count());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task EnvironmentLock_PrerequisiteWarning_DoesNotCommitProjectMetadata()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest(
                Name: "app",
                Domain: "app.test",
                Kind: ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");

            var lockFile = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "changed.test",
                Database = new EnvironmentDatabasePin("none", null, null),
                Addons = new[] { "definitely-missing-addon" }
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(lockFile));

            using var service = new EnvironmentLockService(root);
            var result = await service.ApplyLockAsync(project);

            Assert.NotEmpty(result.Warnings);
            var manifest = workspace.LoadManifest(project);
            Assert.NotNull(manifest);
            Assert.Equal("app.test", manifest!.Domain);
            Assert.Equal("app.test", sites.GetSites().Single(item => item.Name == "app").Domain);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void LocalCertificateManager_MismatchedPrivateKey_IsNotHealthy()
    {
        var root = NewRoot();
        try
        {
            var manager = new LocalCertificateManager(root);
            var certificate = manager.Ensure("valid.test");
            Assert.True(manager.IsMaterialValid("valid.test"));

            using var replacement = System.Security.Cryptography.RSA.Create(2048);
            File.WriteAllText(certificate.PrivateKeyPath, replacement.ExportPkcs8PrivateKeyPem());
            Assert.False(manager.IsMaterialValid("valid.test"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void SelfUpdater_UsesArchitectureSpecificInstallerAsset()
    {
        var version = new Version(1, 2, 3);
        Assert.Equal("DevBox-1.2.3-win-x64-setup.exe", ApplicationSelfUpdateService.GetExpectedInstallerAssetName(version, Architecture.X64));
        Assert.Equal("DevBox-1.2.3-win-arm64-setup.exe", ApplicationSelfUpdateService.GetExpectedInstallerAssetName(version, Architecture.Arm64));
    }

    [Fact]
    public void SecureSecretStore_ConcurrentInstances_PreserveAllKeys()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = NewRoot();
        try
        {
            const int count = 24;
            Parallel.For(0, count, i => new SecureSecretStore(root).Set($"test.key.{i}", $"value-{i}"));
            var store = new SecureSecretStore(root);
            Assert.Equal(count, store.ListKeys().Count);
            for (var i = 0; i < count; i++)
                Assert.Equal($"value-{i}", store.Get($"test.key.{i}"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
