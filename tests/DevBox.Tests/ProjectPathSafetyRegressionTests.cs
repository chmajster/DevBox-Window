using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProjectPathSafetyRegressionTests
{
    [Fact]
    public async Task ProjectOperations_RejectProjectRootReparsePoint()
    {
        var root = TemporaryRoot();
        var www = Path.Combine(root, "www");
        var outside = Path.Combine(root, "outside-project");
        var link = Path.Combine(www, "linked-project");
        Directory.CreateDirectory(www);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "index.php"), "<?php echo 'outside';");

        if (!TryCreateDirectoryLink(link, outside))
        {
            Cleanup(root, link);
            return;
        }

        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(
                root,
                sites,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));

            var commands = new ProjectCommandService(root, workspace);
            Assert.Throws<InvalidOperationException>(() => commands.GetPresets(link));

            var actions = new ProjectActionService(root, workspace);
            Assert.Throws<InvalidOperationException>(() => actions.GetActions(link));

            var snapshots = new ProjectSnapshotService(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.CreateAsync(link));

            var transfers = new ProjectTransferService(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => transfers.ExportAsync(link));

            using var environment = new EnvironmentLockService(root);
            Assert.Throws<InvalidOperationException>(() => environment.Generate(link));
        }
        finally
        {
            Cleanup(root, link);
        }
    }

    [Fact]
    public void Import_CopyIntoDevBox_RejectsNestedReparsePointBeforeFollowingIt()
    {
        var root = TemporaryRoot();
        var www = Path.Combine(root, "www");
        var source = Path.Combine(root, "import-source");
        var outside = Path.Combine(root, "outside-import");
        var nestedLink = Path.Combine(source, "linked-content");
        Directory.CreateDirectory(www);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(source, "index.php"), "<?php echo 'source';");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "must-not-be-copied");

        if (!TryCreateDirectoryLink(nestedLink, outside))
        {
            Cleanup(root, nestedLink);
            return;
        }

        try
        {
            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(
                root,
                sites,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));

            var error = Assert.Throws<InvalidDataException>(() => workspace.Import(new ProjectImportRequest(
                source,
                "imported",
                "imported.test",
                null,
                false,
                CopyIntoDevBox: true)));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(www, "imported", "linked-content", "secret.txt")));
        }
        finally
        {
            Cleanup(root, nestedLink);
        }
    }

    [Fact]
    public void ProjectAction_WorkingDirectoryDot_RemainsValid()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "app");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "devbox.json"), """
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
                  "Key": "dot-root",
                  "DisplayName": "Dot root",
                  "Executable": "php",
                  "Arguments": ["-v"],
                  "WorkingDirectory": ".",
                  "TimeoutSeconds": 30,
                  "Enabled": true
                }
              ]
            }
            """);

            var sites = new SiteManager(root);
            var workspace = new ProjectWorkspaceService(
                root,
                sites,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));
            var actions = new ProjectActionService(root, workspace);

            var configured = Assert.Single(actions.GetActions(project));
            Assert.Equal(".", configured.WorkingDirectory);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ConfigurationRestore_RejectsBackupThroughReparsePoint()
    {
        var root = TemporaryRoot();
        var backupsRoot = Path.Combine(root, "backups");
        var outside = Path.Combine(root, "outside-backups");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = backupsRoot;
        if (!TryCreateDirectoryLink(link, outside))
        {
            Cleanup(root, link);
            return;
        }

        try
        {
            var configuration = Path.Combine(outside, "configuration");
            Directory.CreateDirectory(configuration);
            var backup = Path.Combine(configuration, "php-test.ini.bak");
            File.WriteAllText(backup, "memory_limit=256M");
            var service = new ConfigurationFileService(root);
            Assert.Throws<InvalidOperationException>(() => service.RestoreBackup("php", backup));
        }
        finally
        {
            Cleanup(root, link);
        }
    }

    [Fact]
    public void ManagedService_RejectsExecutableThroughReparsePoint()
    {
        var root = TemporaryRoot();
        var outside = Path.Combine(root, "outside-runtime");
        var runtime = Path.Combine(root, "runtime");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllBytes(Path.Combine(outside, "tool.exe"), []);
        if (!TryCreateDirectoryLink(runtime, outside))
        {
            Cleanup(root, runtime);
            return;
        }

        try
        {
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(
                ManagedServiceManifest.CurrentSchemaVersion,
                "custom-tool",
                "Custom Tool",
                "runtime/tool.exe",
                Array.Empty<string>(),
                ".",
                19001,
                "1.0");

            Assert.Throws<InvalidDataException>(() => catalog.Save([manifest]));
        }
        finally
        {
            Cleanup(root, runtime);
        }
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), "devbox-project-path-safety-tests", Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root, string? linkPath = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(linkPath) && Directory.Exists(linkPath) &&
                (File.GetAttributes(linkPath) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(linkPath);
            }
        }
        catch
        {
        }

        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }
}
