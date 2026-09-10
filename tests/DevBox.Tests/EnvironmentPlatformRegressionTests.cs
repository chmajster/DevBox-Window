using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class EnvironmentPlatformRegressionTests
{
    [Fact]
    public async Task RuntimeImport_AcceptsFlatArchiveWithoutArchiveRoot()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "flat.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("tool.exe");
                await using var output = entry.Open();
                await output.WriteAsync(Encoding.UTF8.GetBytes("fixture"));
            }

            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(archivePath)));
            var package = new RuntimePackageEntry
            {
                Key = "fixture",
                DisplayName = "Fixture Runtime",
                Version = "1.0.0",
                Architecture = "any",
                ExecutableRelativePath = "tool.exe"
            };

            using var service = new RuntimePlatformService(root);
            await service.ImportLocalArchiveAsync(package, archivePath, sha, activate: false);

            Assert.True(File.Exists(Path.Combine(root, "runtime", "fixture", "1.0.0", "tool.exe")));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ProjectActions_PreserveManifestDeclarationOrder()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "actions");
            var workspace = Workspace(root);
            var service = new ProjectActionService(root, workspace);
            service.SetActions(project,
            [
                new ProjectActionDefinition("z-last-alphabetically", "First declared", "php", ["-v"]),
                new ProjectActionDefinition("a-first-alphabetically", "Second declared", "php", ["-v"])
            ]);

            Assert.Equal(
                ["z-last-alphabetically", "a-first-alphabetically"],
                service.GetActions(project).Select(action => action.Key).ToArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task TaskCenter_LateProgressCannotReopenCompletedTask()
    {
        var root = TemporaryRoot();
        try
        {
            using var center = new PlatformTaskCenter(root, 1);
            IProgress<(double Progress, string? Message)>? captured = null;
            var id = center.Enqueue("terminal-state", (progress, _) =>
            {
                captured = progress;
                return Task.CompletedTask;
            });

            var completed = await center.WaitAsync(id);
            Assert.Equal(PlatformTaskState.Completed, completed.State);

            captured!.Report((5, "late progress"));
            await Task.Delay(150);

            Assert.Equal(PlatformTaskState.Completed, center.GetTask(id)!.State);
            Assert.Equal(100, center.GetTask(id)!.Progress);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void RemoteEnvironment_ExportsAndImportsItsOwnSecretMetadataSafely()
    {
        var root = TemporaryRoot();
        try
        {
            var path = Path.Combine(root, "portable.devbox-env.json");
            var service = new RemoteEnvironmentService(root);
            var exported = service.ExportProfile("php-minimal", path);

            Assert.Equal(path, exported);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("false", document.RootElement.GetProperty("metadata").GetProperty("containsSecrets").GetString());

            var imported = service.Import(path, replaceExisting: true);
            Assert.Equal("php-minimal", imported.ProfileKey);
            Assert.True(imported.ReplacedExisting);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SnapshotRestore_RewritesIdentityAndRestoresDatabasePayload()
    {
        var root = TemporaryRoot();
        try
        {
            var source = CreateProject(root, "source", "source.test");
            var lockFile = new EnvironmentLockFile
            {
                ProjectName = "source",
                Domain = "source.test",
                Runtimes = new Dictionary<string, string>(),
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false
            };
            File.WriteAllText(
                Path.Combine(source, EnvironmentLockService.LockFileName),
                JsonSerializer.Serialize(lockFile));

            var databaseBackup = Path.Combine(root, "source.sql");
            await File.WriteAllTextAsync(databaseBackup, "-- fixture database backup");
            var service = new ProjectSnapshotService(root);
            var snapshot = await service.CreateAsync(
                source,
                new ProjectSnapshotOptions(IncludeDatabase: true),
                [databaseBackup]);

            var restored = await service.RestoreAsync(snapshot.SnapshotPath, "copy");

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(restored, ProjectWorkspaceService.ManifestFileName)));
            Assert.Equal("copy", manifest.RootElement.GetProperty("Name").GetString());
            Assert.Equal("copy.test", manifest.RootElement.GetProperty("Domain").GetString());

            var restoredLock = JsonSerializer.Deserialize<EnvironmentLockFile>(File.ReadAllText(Path.Combine(restored, EnvironmentLockService.LockFileName)))!;
            Assert.Equal("copy", restoredLock.ProjectName);
            Assert.Equal("copy.test", restoredLock.Domain);

            var backupRoot = Path.Combine(root, "backups", "snapshot-restores", "copy");
            Assert.Single(Directory.GetFiles(backupRoot, "source.sql", SearchOption.AllDirectories));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplyLock_ClearsUnlockedProjectStateAndSynchronizesSite()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "locked", "locked.test");
            var sites = new SiteManager(root);
            sites.Create("locked", "locked.test", project);
            _ = sites.SetPhpVersion("locked", "8.0.0");

            var workspace = Workspace(root);
            var actions = new ProjectActionService(root, workspace);
            actions.SetActions(project, [new ProjectActionDefinition("stale", "Stale", "php", ["-v"])]);

            File.WriteAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName), """
            {
              "Name": "locked",
              "Domain": "locked.test",
              "PhpVersion": "8.0.0",
              "DatabaseEngine": "none",
              "Https": false,
              "Addons": ["stale-addon"],
              "Services": ["mailpit"],
              "Actions": [{"Key":"stale","DisplayName":"Stale","Executable":"php","Arguments":["-v"]}]
            }
            """);

            var desired = new EnvironmentLockFile
            {
                ProjectName = "locked",
                Domain = "locked.test",
                Runtimes = new Dictionary<string, string>(),
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false,
                Addons = Array.Empty<string>(),
                Services = Array.Empty<string>(),
                Actions = Array.Empty<ProjectActionDefinition>()
            };
            File.WriteAllText(
                Path.Combine(project, EnvironmentLockService.LockFileName),
                JsonSerializer.Serialize(desired));

            using var service = new EnvironmentLockService(root);
            _ = await service.ApplyLockAsync(project);

            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, ProjectWorkspaceService.ManifestFileName)));
            Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("PhpVersion").ValueKind);
            Assert.Empty(manifest.RootElement.GetProperty("Addons").EnumerateArray());
            Assert.Empty(manifest.RootElement.GetProperty("Services").EnumerateArray());
            Assert.Empty(actions.GetActions(project));
            Assert.Null(sites.GetSites().Single(site => site.Name == "locked").PhpVersion);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task MarketplaceSync_RemovesWithdrawnRemoteEntries()
    {
        var root = TemporaryRoot();
        try
        {
            using var rsa = RSA.Create(2048);
            var handler = new SignedMarketplaceHandler(rsa);
            using var client = new HttpClient(handler);
            using var marketplace = new AddonMarketplaceService(root, client);
            marketplace.Configure(
                "https://marketplace.test/catalog.json",
                "https://marketplace.test/catalog.sig",
                rsa.ExportSubjectPublicKeyInfoPem());

            handler.Catalog = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new AddonDefinition(
                    "remote-fixture",
                    "Remote Fixture",
                    "Regression fixture",
                    "www/addons/remote-fixture",
                    "index.php",
                    "http://remote-fixture.test",
                    Array.Empty<string>(),
                    "1.0.0",
                    "https://marketplace.test/remote-fixture.zip",
                    new string('a', 64),
                    string.Empty)
            }, WebJson);

            var first = await marketplace.SyncAsync();
            Assert.Contains(first, addon => addon.Key == "remote-fixture");

            handler.Catalog = "[]"u8.ToArray();
            var second = await marketplace.SyncAsync();
            Assert.DoesNotContain(second, addon => addon.Key == "remote-fixture");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ProjectWorkspaceService Workspace(string root) => new(
        root,
        new SiteManager(root),
        new PhpExtensionInspector(root),
        new LocalCertificateManager(root));

    private static string CreateProject(string root, string name, string? domain = null)
    {
        var project = Path.Combine(root, "www", name);
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, ProjectWorkspaceService.ManifestFileName),
            JsonSerializer.Serialize(new { Name = name, Domain = domain ?? $"{name}.test", DatabaseEngine = "none", Https = false }));
        return project;
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-platform-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class SignedMarketplaceHandler(RSA rsa) : HttpMessageHandler
    {
        public byte[] Catalog { get; set; } = "[]"u8.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content;
            if (request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal))
            {
                var signature = rsa.SignData(Catalog, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                content = new StringContent(Convert.ToBase64String(signature), Encoding.UTF8, "text/plain");
            }
            else
            {
                content = new ByteArrayContent(Catalog);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
}
