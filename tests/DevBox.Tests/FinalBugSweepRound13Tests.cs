using System.IO.Compression;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound13Tests
{
    [Fact]
    public void PathSafety_PreservesFilesystemRootWhenCheckingChild()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("Temporary directory does not have a filesystem root.");
        var candidate = Path.Combine(filesystemRoot, "devbox-root-safety-" + Guid.NewGuid().ToString("N"));

        var result = PathSafety.EnsureUnderRootWithoutReparsePoints(
            filesystemRoot,
            candidate,
            "candidate must remain under the filesystem root");

        Assert.True(string.Equals(Path.GetFullPath(candidate), result, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PathSafety_AllowsFilesystemRootWhenExplicitlyRequested()
    {
        var filesystemRoot = Path.GetPathRoot(Path.GetTempPath())
            ?? throw new InvalidOperationException("Temporary directory does not have a filesystem root.");

        var result = PathSafety.EnsureUnderRootWithoutReparsePoints(
            filesystemRoot,
            filesystemRoot,
            "root should be allowed",
            allowRoot: true);

        Assert.True(string.Equals(Path.GetFullPath(filesystemRoot), result, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("payload/file.txt.")]
    [InlineData("payload/file.txt ")]
    [InlineData("payload/NUL.txt")]
    [InlineData("payload/COM1.log")]
    [InlineData("payload/../file.txt")]
    public void ArchiveSafety_RejectsWindowsAmbiguousOrReservedEntryNames(string entryName)
    {
        var root = NewRoot();
        try
        {
            var archivePath = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(entryName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("blocked");
            }

            Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(
                archivePath,
                Path.Combine(root, "extract"),
                1024 * 1024,
                100,
                "fixture"));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task ConfigurationValidation_UsesUtf8ByteLimitInsteadOfCharacterCount()
    {
        var root = NewRoot();
        try
        {
            var service = new ConfigurationFileService(root);
            var content = new string('ą', 1_100_000);

            var result = await service.ValidateAsync("nginx", content);

            Assert.False(result.IsValid);
            Assert.Contains("2 MiB", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task ProjectProvisioning_PreCanceledOperation_DoesNotCreateProjectOrSite()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "www"));
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(
                root,
                sites,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));
            var provisioning = new ProjectProvisioningService(
                root,
                workspace,
                new DatabaseManager(root),
                new ManagedServiceCatalog(root));
            var request = new ProjectCreateRequest(
                Name: "cancelled-project",
                Domain: "cancelled-project.test",
                Kind: ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                provisioning.ProvisionAsync(request, cancellationToken: cancellation.Token));

            Assert.False(Directory.Exists(Path.Combine(root, "www", "cancelled-project")));
            Assert.DoesNotContain(sites.GetSites(), item => item.Name.Equals("cancelled-project", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task EnvironmentLock_PreCanceledApply_DoesNotMutateManifestOrSite()
    {
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "www"));
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(
                root,
                sites,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));
            _ = workspace.Create(new ProjectCreateRequest(
                Name: "app",
                Domain: "app.test",
                Kind: ProjectKind.Php,
                Https: false,
                DatabaseEngine: "none",
                Addons: Array.Empty<string>()));
            var project = Path.Combine(root, "www", "app");
            var desired = new EnvironmentLockFile
            {
                ProjectName = "app",
                Domain = "changed.test",
                Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Database = new EnvironmentDatabasePin("none", null, null),
                Https = false,
                Addons = [],
                Services = [],
                Actions = []
            };
            File.WriteAllText(
                Path.Combine(project, EnvironmentLockService.LockFileName),
                JsonSerializer.Serialize(desired));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            using var service = new EnvironmentLockService(root);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.ApplyLockAsync(project, cancellation.Token));

            Assert.Equal("app.test", workspace.LoadManifest(project)!.Domain);
            Assert.Equal("app.test", sites.GetSites().Single(item => item.Name == "app").Domain);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task DatabaseRuntime_PreCanceledInitialization_DoesNotPersistRegistration()
    {
        var root = NewRoot();
        try
        {
            var bin = Path.Combine(root, "runtime", "mysql", "8.4.99", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "mysqld.exe"), []);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            using var service = new DatabaseRuntimeService(root);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                service.EnsureInitializedAsync("mysql", "8.4.99", 34077, cancellation.Token));

            Assert.False(File.Exists(Path.Combine(root, "config", "database-runtimes.json")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task DatabaseManager_PreCanceledCommand_DoesNotStartNativeClient()
    {
        var root = NewRoot();
        try
        {
            var bin = Path.Combine(root, "runtime", "mysql", "current", "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "mysql.exe"), []);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var manager = new DatabaseManager(root);
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                manager.ListDatabasesAsync(new DatabaseConnectionOptions(), cancellation.Token));
        }
        finally
        {
            Delete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round13-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
