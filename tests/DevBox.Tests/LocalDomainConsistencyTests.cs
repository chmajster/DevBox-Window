using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class LocalDomainConsistencyTests
{
    [Fact]
    public void SiteManager_CreateWithUnderscoreNameGeneratesValidDefaultDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var site = new SiteManager(root).Create("project_with_underscore");

            Assert.Equal("project-with-underscore.test", site.Domain);
            Assert.True(File.Exists(Path.Combine(root, "config", "nginx", "sites-enabled", "project-with-underscore.test.conf")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ProjectWorkspace_CreateSupportsEightyCharacterProjectNameWithSafeDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var longName = new string('a', 79) + "_";
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));

            var site = workspace.Create(new ProjectCreateRequest(
                longName,
                null,
                ProjectKind.EmptyPhp,
                null,
                false,
                "none",
                null,
                null,
                Array.Empty<string>(),
                Array.Empty<string>()));

            Assert.Equal(longName, site.Name);
            Assert.EndsWith(".test", site.Domain, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(site.Domain.Split('.')[0].Length, 1, 63);
            Assert.DoesNotContain('_', site.Domain);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectTransfer_RenameWithUnderscoreUsesSafeDefaultDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var source = Path.Combine(root, "www", "transfer-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, ProjectWorkspaceService.ManifestFileName), """
            {"Name":"transfer-source","Domain":"transfer-source.test","DatabaseEngine":"none","Https":false}
            """);
            File.WriteAllText(Path.Combine(source, "index.html"), "fixture");

            var transfer = new ProjectTransferService(root);
            var exported = await transfer.ExportAsync(source);
            var imported = await transfer.ImportAsync(exported.ArchivePath, "renamed_project");

            Assert.True(Directory.Exists(imported));
            var site = new SiteManager(root).GetSites().Single(value => value.Name == "renamed_project");
            Assert.Equal("renamed-project.test", site.Domain);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-local-domain-tests", Guid.NewGuid().ToString("N"));
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
