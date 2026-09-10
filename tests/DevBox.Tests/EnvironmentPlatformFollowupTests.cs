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

            // A directory at the metadata path makes the final atomic move fail after
            // the vhost has already been staged, exercising registration rollback.
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
