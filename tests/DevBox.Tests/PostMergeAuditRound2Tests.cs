using System.Security.Cryptography;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound2Tests
{
    [Fact]
    public async Task CrossProcessFileLock_RejectsSecondOwnerUntilReleased()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "resource.lock");
            using var first = CrossProcessFileLock.Acquire(path, TimeSpan.FromSeconds(1));
            await Assert.ThrowsAsync<TimeoutException>(() =>
                CrossProcessFileLock.AcquireAsync(path, CancellationToken.None, TimeSpan.FromMilliseconds(100)));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task EnvironmentLock_SiteCollision_RollsBackManifestActionsAndSite()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            _ = workspace.Create(new ProjectCreateRequest("other", "other.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            var actions = new ProjectActionService(root, workspace);
            actions.SetActions(project, new[] { new ProjectActionDefinition("before", "Before", "php", new[] { "-v" }) });

            var desired = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "other.test",
                Database = new EnvironmentDatabasePin("none", null, null),
                Actions = new[] { new ProjectActionDefinition("after", "After", "php", new[] { "-m" }) }
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(desired));

            using var service = new EnvironmentLockService(root);
            await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyLockAsync(project));

            Assert.Equal("app.test", workspace.LoadManifest(project)!.Domain);
            Assert.Equal("app.test", sites.GetSites().Single(item => item.Name == "app").Domain);
            Assert.Equal("before", actions.GetActions(project).Single().Key);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void LocalCertificateManager_Ensure_ReplacesWrongDomainMaterial()
    {
        var root = NewRoot();
        try
        {
            var manager = new LocalCertificateManager(root);
            var foo = manager.Ensure("foo.test");
            var sites = Path.Combine(root, "config", "ssl", "sites");
            File.Copy(foo.CertificatePath, Path.Combine(sites, "bar.test.crt.pem"));
            File.Copy(foo.PrivateKeyPath, Path.Combine(sites, "bar.test.key.pem"));

            Assert.False(manager.IsMaterialValid("bar.test"));
            var bar = manager.Ensure("bar.test");
            Assert.True(manager.IsMaterialValid("bar.test"));
            Assert.NotEqual(foo.Thumbprint, bar.Thumbprint);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void EnvironmentLock_DriftDetectsExistingButInvalidTlsMaterial()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var certs = new LocalCertificateManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), certs);
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.Php, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            _ = sites.SetHttps("app", true);
            var certificate = certs.Ensure("app.test");
            using var wrongKey = RSA.Create(2048);
            File.WriteAllText(certificate.PrivateKeyPath, wrongKey.ExportPkcs8PrivateKeyPem());

            var project = Path.Combine(root, "www", "app");
            var desired = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "app.test",
                Https = true,
                Database = new EnvironmentDatabasePin("none", null, null)
            };
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), JsonSerializer.Serialize(desired));
            using var service = new EnvironmentLockService(root);
            Assert.Contains(service.GetDrift(project), item => item.Contains("certificate material", StringComparison.OrdinalIgnoreCase));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task ProjectProvisioning_FailureAfterCreate_RollsBackSiteAndPreservesEmptyRoot()
    {
        var root = NewRoot();
        try
        {
            var projectRoot = Path.Combine(root, "www", "app");
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(Path.Combine(root, "config", "services.json"));
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            var service = new ProjectProvisioningService(root, workspace, new DatabaseManager(root), new ManagedServiceCatalog(root));
            var request = new ProjectCreateRequest(
                "app", "app.test", ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>(),
                Services: new[] { "mailpit" });

            await Assert.ThrowsAnyAsync<Exception>(() => service.ProvisionAsync(request));
            Assert.DoesNotContain(sites.GetSites(), item => item.Name == "app");
            Assert.True(Directory.Exists(projectRoot));
            Assert.Empty(Directory.EnumerateFileSystemEntries(projectRoot));
        }
        finally { TryDelete(root); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-audit2-" + Guid.NewGuid().ToString("N"));
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
