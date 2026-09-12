using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound8Tests
{
    [Fact]
    public void ProjectActions_RejectDuplicateKeys()
    {
        var root = NewRoot();
        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(root, sites, new PhpExtensionInspector(root), new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest("app", "app.test", ProjectKind.EmptyPhp, Https: false, DatabaseEngine: "none", Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {"Name":"app","Domain":"app.test","Kind":"EmptyPhp","Actions":[
              {"Key":"build","DisplayName":"One","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true},
              {"Key":"BUILD","DisplayName":"Two","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true}
            ]}
            """);
            var service = new ProjectActionService(root, workspace);
            Assert.Throws<InvalidDataException>(() => service.GetActions(project));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void PersistedCatalogs_RejectDuplicateIdentities()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "project-profiles.json"), """
            [
              {"Key":"dup","DisplayName":"One","Kind":"EmptyPhp","PhpVersion":null,"NodeVersion":null,"DatabaseEngine":"none","Https":false,"Addons":[],"Services":[],"Description":"one"},
              {"Key":"DUP","DisplayName":"Two","Kind":"EmptyPhp","PhpVersion":null,"NodeVersion":null,"DatabaseEngine":"none","Https":false,"Addons":[],"Services":[],"Description":"two"}
            ]
            """);
            Assert.Throws<InvalidDataException>(() => new ProjectStackProfileService(root).GetProfiles());

            File.WriteAllText(Path.Combine(root, "config", "runtime-catalog.json"), """
            [
              {"Key":"tool","DisplayName":"One","Version":"1.0","Architecture":"x64","ExecutableRelativePath":"tool.exe"},
              {"Key":"TOOL","DisplayName":"Two","Version":"1.0","Architecture":"X64","ExecutableRelativePath":"other.exe"}
            ]
            """);
            using var runtimes = new RuntimePlatformService(root);
            Assert.Throws<InvalidDataException>(() => runtimes.GetCatalog());

            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [
              {"Key":"dup","DisplayName":"One","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[]},
              {"Key":"DUP","DisplayName":"Two","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            ]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void EnvironmentProfile_RejectsDuplicateActionKeys()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "environment-profiles.json"), """
            [{"Key":"fixture","DisplayName":"Fixture","Kind":"EmptyPhp","Runtimes":{},"Database":{"Engine":"none","Port":3306},"Https":false,"Addons":[],"Services":[],"Actions":[
              {"Key":"build","DisplayName":"One","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true},
              {"Key":"BUILD","DisplayName":"Two","Executable":"php","Arguments":[],"TimeoutSeconds":60,"Enabled":true}
            ]}]
            """);
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).GetProfiles());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void AddonCatalog_RejectsSharedInstallPathAndDomain()
    {
        var root = NewRoot();
        try
        {
            var path = Path.Combine(root, "config", "addons.json");
            File.WriteAllText(path, CatalogJson("www/shared", "one.test", "www/shared", "two.test"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());

            File.WriteAllText(path, CatalogJson("www/one", "same.test", "www/two", "same.test"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void AddonMarketplace_NonStringKey_IsControlledInvalidData()
    {
        var method = typeof(AddonMarketplaceService).GetMethod("MergeCatalogs", BindingFlags.Static | BindingFlags.NonPublic)!;
        var local = new JsonArray();
        var marketplace = JsonNode.Parse("[{\"key\":123}]")!.AsArray();
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [local, marketplace]));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public void TaskCenter_DuplicateHistoryIds_AreQuarantined()
    {
        var root = NewRoot();
        try
        {
            var id = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var items = new[]
            {
                new PlatformTaskSnapshot(id, "one", PlatformTaskState.Completed, 100, "done", now, now, now, null),
                new PlatformTaskSnapshot(id, "two", PlatformTaskState.Completed, 100, "done", now, now, now, null)
            };
            var path = Path.Combine(root, "logs", "task-center-history.json");
            File.WriteAllText(path, JsonSerializer.Serialize(items));
            using var center = new PlatformTaskCenter(root);
            Assert.Empty(center.GetTasks());
            Assert.False(File.Exists(path));
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "task-center-history.json.invalid-*.bak"));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ManagedService_PortReservationReadsDatabasePortCaseInsensitively()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), """
            [{"Engine":"mysql","Version":"8.4.11","PORT":3400}]
            """);
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(1, "fixture", "Fixture", "runtime/fixture/tool.exe", [], ".", 3400, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ManagedService_InvalidDatabaseRegistrationShape_IsNotIgnored()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "database-runtimes.json"), "{}");
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(1, "fixture", "Fixture", "runtime/fixture/tool.exe", [], ".", 3400, "1");
            Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public async Task AddonCatalog_DefaultInitialization_IsSafeUnderConcurrency()
    {
        var root = NewRoot();
        try
        {
            var tasks = Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => new AddonCatalog(root).GetAddons()))
                .ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.All(results, addons => Assert.NotEmpty(addons));
        }
        finally { TryDelete(root); }
    }

    private static string CatalogJson(string installOne, string domainOne, string installTwo, string domainTwo) => $$"""
    [
      {"Key":"one","DisplayName":"One","Description":"one","InstallRelativePath":"{{installOne}}","EntryPointRelativePath":"{{installOne}}/index.php","LocalUrl":"http://{{domainOne}}","RequiredPhpExtensions":[],"Version":"1","DownloadUrl":"https://example.com/one.zip","Sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","ArchiveRootDirectory":"one"},
      {"Key":"two","DisplayName":"Two","Description":"two","InstallRelativePath":"{{installTwo}}","EntryPointRelativePath":"{{installTwo}}/index.php","LocalUrl":"http://{{domainTwo}}","RequiredPhpExtensions":[],"Version":"1","DownloadUrl":"https://example.com/two.zip","Sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","ArchiveRootDirectory":"two"}
    ]
    """;

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-final-sweep8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
