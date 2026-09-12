using System.Net;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalAuditRegressionTests
{
    [Theory]
    [InlineData("mariadb", "mysql")]
    [InlineData("mariadb", "information_schema")]
    [InlineData("postgresql", "postgres")]
    [InlineData("postgresql", "template0")]
    [InlineData("postgresql", "template1")]
    public async Task ProjectDatabaseProvisioner_RejectsSystemDatabaseMutationsBeforeClientLookup(string engine, string database)
    {
        var root = TemporaryRoot();
        try
        {
            var provisioner = new ProjectDatabaseProvisioner(root, new DatabaseManager(root));

            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.EnsureDatabaseAsync(engine, database));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provisioner.DropDatabaseAsync(engine, database));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EnvironmentProfile_RejectsExecutableOutsideActionAllowList()
    {
        var root = TemporaryRoot();
        try
        {
            var profile = BaseProfile() with
            {
                Actions = [new ProjectActionDefinition("unsafe", "Unsafe", "powershell.exe", ["-Command", "whoami"], null, 30)]
            };

            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("C:\\outside")]
    [InlineData("../outside")]
    [InlineData("sub/../../outside")]
    public void EnvironmentProfile_RejectsUnsafeActionWorkingDirectory(string workingDirectory)
    {
        var root = TemporaryRoot();
        try
        {
            var profile = BaseProfile() with
            {
                Actions = [new ProjectActionDefinition("composer-install", "Composer", "composer", ["install"], workingDirectory, 30)]
            };

            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ProjectActionManifest_RejectsParentTraversalBeforeExecution()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "app");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {
              "SchemaVersion": 1,
              "Name": "app",
              "Domain": "app.test",
              "Kind": "EmptyPhp",
              "DatabaseEngine": "none",
              "Https": false,
              "Addons": [],
              "Actions": [
                {
                  "Key": "escape",
                  "DisplayName": "Escape",
                  "Executable": "composer",
                  "Arguments": ["install"],
                  "WorkingDirectory": "../outside",
                  "TimeoutSeconds": 30,
                  "Enabled": true
                }
              ]
            }
            """);

            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            var actions = new ProjectActionService(root, workspace);

            Assert.Throws<InvalidDataException>(() => actions.GetActions(project));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ComposerInstaller_RejectsOversizedSignatureBeforeInstallerDownload()
    {
        var root = TemporaryRoot();
        try
        {
            var php = Path.Combine(root, "runtime", "php", "current", "php.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(php)!);
            File.WriteAllBytes(php, []);

            var handler = new OversizedSignatureHandler();
            using var http = new HttpClient(handler);
            using var tools = new DeveloperToolsService(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() => tools.InstallComposerAsync());
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void AddonCatalog_DoesNotReportOwnedAddonThroughReparsePoint()
    {
        var root = TemporaryRoot();
        var outside = Path.Combine(Path.GetTempPath(), "devbox-addon-outside-tests", Guid.NewGuid().ToString("N"));
        string? link = null;
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var catalog = new AddonCatalog(root);
            var addon = Assert.Single(catalog.GetAddons());
            link = addon.InstallPath;

            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "index.php"), "<?php echo 'outside';");
            File.WriteAllText(Path.Combine(outside, AddonOwnership.MarkerFileName), $"{addon.Key}{Environment.NewLine}{addon.Version}{Environment.NewLine}");

            if (!TryCreateDirectoryLink(link, outside))
                return;

            Assert.False(catalog.IsInstalled(addon));
        }
        finally
        {
            TryDeleteLink(link);
            Cleanup(root);
            Cleanup(outside);
        }
    }

    private static EnvironmentProfile BaseProfile() => new()
    {
        Key = "final-audit",
        DisplayName = "Final audit",
        Kind = ProjectKind.EmptyPhp,
        Database = new EnvironmentDatabasePin("none", null, null)
    };

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-audit-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private static void TryDeleteLink(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch
        {
        }
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class OversizedSignatureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var content = new ByteArrayContent(new byte[5000]);
            content.Headers.ContentLength = 5000;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }
}
