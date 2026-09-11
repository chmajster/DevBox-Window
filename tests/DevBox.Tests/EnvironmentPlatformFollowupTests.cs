using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class EnvironmentPlatformFollowupTests
{
    [Fact]
    public async Task SnapshotRestore_DoesNotScaffoldPhpInfoIntoStaticProject()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "www", "static-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "index.html"), "<h1>static fixture</h1>");
            File.WriteAllText(
                Path.Combine(source, ProjectWorkspaceService.ManifestFileName),
                JsonSerializer.Serialize(new
                {
                    Name = "static-source",
                    Domain = "static-source.test",
                    DatabaseEngine = "none",
                    Https = false
                }));

            var snapshots = new ProjectSnapshotService(root);
            var snapshot = await snapshots.CreateAsync(source);
            var restored = await snapshots.RestoreAsync(snapshot.SnapshotPath, "static-copy");

            Assert.True(File.Exists(Path.Combine(restored, "index.html")));
            Assert.False(File.Exists(Path.Combine(restored, "index.php")));
            Assert.Equal(
                restored,
                new SiteManager(root).GetSites().Single(site => site.Name == "static-copy").DocumentRoot);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SnapshotRestore_RejectsUnsafeManifestDomainBeforeTlsAccess()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "www", "unsafe-domain");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "index.html"), "fixture");
            File.WriteAllText(
                Path.Combine(source, ProjectWorkspaceService.ManifestFileName),
                JsonSerializer.Serialize(new
                {
                    Name = "unsafe-domain",
                    Domain = "../../outside.test",
                    DatabaseEngine = "none",
                    Https = true
                }));

            var snapshots = new ProjectSnapshotService(root);
            var snapshot = await snapshots.CreateAsync(source);

            await Assert.ThrowsAsync<ArgumentException>(() =>
                snapshots.RestoreAsync(snapshot.SnapshotPath, "unsafe-domain", overwrite: true));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ExportProjectLock_ReportsWhenActionsWereOmitted()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "locked-project");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.html"), "fixture");
            File.WriteAllText(
                Path.Combine(project, ProjectWorkspaceService.ManifestFileName),
                JsonSerializer.Serialize(new
                {
                    Name = "locked-project",
                    Domain = "locked-project.test",
                    DatabaseEngine = "none",
                    Https = false
                }));

            var lockFile = new EnvironmentLockFile
            {
                ProjectName = "locked-project",
                Domain = "locked-project.test",
                Runtimes = new Dictionary<string, string>(),
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false,
                Actions =
                [
                    new ProjectActionDefinition(
                        "fixture-action",
                        "Fixture action",
                        "npm",
                        ["config", "set", "//registry.example.test/:_authToken=not-exported"])
                ]
            };
            File.WriteAllText(
                Path.Combine(project, EnvironmentLockService.LockFileName),
                JsonSerializer.Serialize(lockFile));

            var destination = Path.Combine(root, "lock-share.devbox-env.json");
            var exported = new RemoteEnvironmentService(root).ExportProjectLock(project, destination);
            var json = File.ReadAllText(exported);

            Assert.DoesNotContain("not-exported", json, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(json);
            Assert.Equal("true", document.RootElement.GetProperty("Metadata").GetProperty("actionsOmitted").GetString());
            Assert.Empty(document.RootElement.GetProperty("Profile").GetProperty("Actions").EnumerateArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SiteRegistration_RemovesVhostWhenMetadataPersistenceFails()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "registration-rollback");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.html"), "fixture");

            Directory.CreateDirectory(Path.Combine(root, "config", "sites.json"));

            var sites = new SiteManager(root);
            Assert.ThrowsAny<IOException>(() =>
                sites.RegisterExisting("registration-rollback", "registration-rollback.test", project));

            Assert.False(File.Exists(Path.Combine(
                root,
                "config",
                "nginx",
                "sites-enabled",
                "registration-rollback.test.conf")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SiteRegistration_RestoresPreexistingVhostWhenMetadataPersistenceFails()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "registration-existing-vhost");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.html"), "fixture");

            var vhostPath = Path.Combine(root, "config", "nginx", "sites-enabled", "registration-existing-vhost.test.conf");
            Directory.CreateDirectory(Path.GetDirectoryName(vhostPath)!);
            const string previousContent = "# unmanaged pre-existing vhost";
            File.WriteAllText(vhostPath, previousContent);
            Directory.CreateDirectory(Path.Combine(root, "config", "sites.json"));

            var sites = new SiteManager(root);
            Assert.ThrowsAny<IOException>(() =>
                sites.RegisterExisting("registration-existing-vhost", "registration-existing-vhost.test", project));

            Assert.Equal(previousContent, File.ReadAllText(vhostPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SiteUpdate_RestoresVhostsWhenMetadataPersistenceFails()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "update-rollback");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.html"), "fixture");

            var sites = new SiteManager(root);
            var original = sites.RegisterExisting("update-rollback", "old-update.test", project);
            var oldVhost = sites.GetNginxConfigPath("old-update.test");
            var newVhost = sites.GetNginxConfigPath("new-update.test");
            var oldVhostContent = File.ReadAllText(oldVhost);
            var metadataPath = Path.Combine(root, "config", "sites.json");

            using (var metadataLock = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() =>
                    sites.Update(original with { Domain = "new-update.test" }));
            }

            Assert.True(File.Exists(oldVhost));
            Assert.Equal(oldVhostContent, File.ReadAllText(oldVhost));
            Assert.False(File.Exists(newVhost));
            Assert.Equal("old-update.test", sites.GetSites().Single().Domain);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void SiteUpdate_RejectsPhpRuntimePortCollisionBeforePersistence()
    {
        var root = TemporaryRoot();
        try
        {
            const string firstVersion = "7.0.3";
            const string collidingVersion = "7.1.30";
            Assert.Equal(PhpRuntimePoolManager.GetPort(firstVersion), PhpRuntimePoolManager.GetPort(collidingVersion));

            var firstRoot = Path.Combine(root, "www", "first-php-site");
            var secondRoot = Path.Combine(root, "www", "second-php-site");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);

            var sites = new SiteManager(root);
            var first = sites.RegisterExisting("first-php-site", "first-php-site.test", firstRoot);
            var second = sites.RegisterExisting("second-php-site", "second-php-site.test", secondRoot);
            _ = sites.Update(first with { PhpVersion = firstVersion });

            Assert.Throws<InvalidOperationException>(() =>
                sites.Update(second with { PhpVersion = collidingVersion }));

            var loaded = sites.GetSites();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(firstVersion, loaded.Single(site => site.Name == "first-php-site").PhpVersion);
            Assert.Null(loaded.Single(site => site.Name == "second-php-site").PhpVersion);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void LocalCa_ReplacesCorruptExistingLeafPem()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            var sitesDirectory = Path.Combine(root, "config", "ssl", "sites");
            Directory.CreateDirectory(sitesDirectory);
            var certificatePath = Path.Combine(sitesDirectory, "corrupt-leaf.test.crt.pem");
            var privateKeyPath = Path.Combine(sitesDirectory, "corrupt-leaf.test.key.pem");
            File.WriteAllText(certificatePath, "not-a-certificate");
            File.WriteAllText(privateKeyPath, "not-a-private-key");

            using var authority = new LocalCertificateAuthorityService(root);
            using var certificate = authority.IssueSiteCertificate("corrupt-leaf.test", trustAuthority: false);

            Assert.False(string.IsNullOrWhiteSpace(certificate.Thumbprint));
            Assert.Contains("BEGIN CERTIFICATE", File.ReadAllText(certificatePath));
            Assert.Contains("BEGIN PRIVATE KEY", File.ReadAllText(privateKeyPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void LocalCa_RemoveAuthorityKeepsPasswordAndTrustWhenPfxDeletionFails()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            using var authority = new LocalCertificateAuthorityService(root);
            using (authority.EnsureAuthority(trustCurrentUser: false))
            {
            }

            var simulatedTrusted = true;
            authority.IsTrustedCurrentUserOverride = _ => simulatedTrusted;
            authority.RemoveTrustCurrentUserOverride = _ => simulatedTrusted = false;
            authority.RestoreTrustCurrentUserOverride = _ => simulatedTrusted = true;

            var secrets = new SecureSecretStore(root);
            const string passwordKey = "ssl.local-ca.pfx-password";
            Assert.NotNull(secrets.Get(passwordKey));
            Assert.True(simulatedTrusted);

            var pfxPath = Path.Combine(root, "config", "ssl", "ca", "devbox-local-ca.pfx");
            using (var locked = new FileStream(pfxPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => authority.RemoveAuthority());
                Assert.NotNull(secrets.Get(passwordKey));
                Assert.True(File.Exists(pfxPath));
                Assert.True(simulatedTrusted);
            }

            authority.RemoveAuthority();
            Assert.Null(secrets.Get(passwordKey));
            Assert.False(simulatedTrusted);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void LocalCa_RemoveAuthorityRestoresFilesAfterPartialDeletionFailure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            using var authority = new LocalCertificateAuthorityService(root);
            using (authority.EnsureAuthority(trustCurrentUser: false))
            {
            }

            var simulatedTrusted = true;
            authority.IsTrustedCurrentUserOverride = _ => simulatedTrusted;
            authority.RemoveTrustCurrentUserOverride = _ => simulatedTrusted = false;
            authority.RestoreTrustCurrentUserOverride = _ => simulatedTrusted = true;

            var pfxPath = Path.Combine(root, "config", "ssl", "ca", "devbox-local-ca.pfx");
            var pemPath = authority.CertificatePath;
            var pfxBefore = File.ReadAllBytes(pfxPath);
            var pemBefore = File.ReadAllBytes(pemPath);
            var deleteCount = 0;
            authority.DeleteAuthorityFileOverride = path =>
            {
                deleteCount++;
                if (deleteCount == 1)
                {
                    File.Delete(path);
                    return;
                }
                throw new IOException("simulated second-file deletion failure");
            };

            Assert.Throws<IOException>(() => authority.RemoveAuthority());

            Assert.True(simulatedTrusted);
            Assert.Equal(pfxBefore, File.ReadAllBytes(pfxPath));
            Assert.Equal(pemBefore, File.ReadAllBytes(pemPath));
            Assert.NotNull(new SecureSecretStore(root).Get("ssl.local-ca.pfx-password"));

            authority.DeleteAuthorityFileOverride = null;
            authority.RemoveAuthority();
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void LocalCa_RemoveAuthorityCleansTrustWhenOnlyPemRemains()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            using var authority = new LocalCertificateAuthorityService(root);
            using (authority.EnsureAuthority(trustCurrentUser: false))
            {
            }

            var simulatedTrusted = true;
            authority.IsTrustedCurrentUserOverride = _ => simulatedTrusted;
            authority.RemoveTrustCurrentUserOverride = _ => simulatedTrusted = false;
            authority.RestoreTrustCurrentUserOverride = _ => simulatedTrusted = true;

            var pfxPath = Path.Combine(root, "config", "ssl", "ca", "devbox-local-ca.pfx");
            File.Delete(pfxPath);
            authority.RemoveAuthority();

            Assert.False(simulatedTrusted);
            Assert.False(File.Exists(authority.CertificatePath));
            Assert.Null(new SecureSecretStore(root).Get("ssl.local-ca.pfx-password"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void TlsRollback_PropagatesFailureToDeleteNewlyCreatedCertificate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = TemporaryRoot();
        try
        {
            var rollback = new TlsRollbackStateService(root);
            var state = rollback.Capture("rollback-delete.test");
            var sitesDirectory = Path.Combine(root, "config", "ssl", "sites");
            Directory.CreateDirectory(sitesDirectory);
            var certificatePath = Path.Combine(sitesDirectory, "rollback-delete.test.crt.pem");
            File.WriteAllText(certificatePath, "newly-created-material");

            using var locked = new FileStream(certificatePath, FileMode.Open, FileAccess.Read, FileShare.None);
            var aggregate = Assert.Throws<AggregateException>(() => rollback.Restore(state));
            Assert.Contains(aggregate.InnerExceptions, ex => ex is IOException);
            Assert.True(File.Exists(certificatePath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-followup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
