using System.Reflection;
using DevBox.Core.Models;
using DevBox.Core.Services;

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
    public void ProjectAction_WorkingDirectoryDot_ResolvesToProjectRoot()
    {
        var root = TemporaryRoot();
        try
        {
            var project = Path.Combine(root, "www", "app");
            Directory.CreateDirectory(project);
            var method = typeof(ProjectActionService).GetMethod(
                "ResolveWorkingDirectory",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(method);
            var resolved = Assert.IsType<string>(method!.Invoke(null, [project, "."]));
            Assert.Equal(
                Path.GetFullPath(project).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(resolved).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                ignoreCase: OperatingSystem.IsWindows());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ConfigurationRestore_RejectsBackupPathThroughReparsePoint()
    {
        var root = TemporaryRoot();
        var backupRoot = Path.Combine(root, "backups", "configuration");
        var outside = Path.Combine(root, "outside-backups");
        var link = Path.Combine(backupRoot, "linked");
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(outside);
        var externalBackup = Path.Combine(outside, "nginx-evil.conf.bak");
        File.WriteAllText(externalBackup, "events { } http { }");

        if (!TryCreateDirectoryLink(link, outside))
        {
            Cleanup(root, link);
            return;
        }

        try
        {
            var service = new ConfigurationFileService(root);
            var error = Assert.Throws<InvalidOperationException>(() =>
                service.RestoreBackup("nginx", Path.Combine(link, Path.GetFileName(externalBackup))));
            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(service.GetPath("nginx")));
        }
        finally
        {
            Cleanup(root, link);
        }
    }

    [Fact]
    public void ManagedService_RejectsExecutablePathThroughReparsePoint()
    {
        var root = TemporaryRoot();
        var externalRoot = TemporaryRoot();
        var runtimeRoot = Path.Combine(root, "runtime");
        var link = Path.Combine(runtimeRoot, "escape");
        Directory.CreateDirectory(runtimeRoot);
        Directory.CreateDirectory(externalRoot);
        File.WriteAllText(Path.Combine(externalRoot, "evil.exe"), "fixture");

        if (!TryCreateDirectoryLink(link, externalRoot))
        {
            Cleanup(root, link);
            Cleanup(externalRoot);
            return;
        }

        try
        {
            var catalog = new ManagedServiceCatalog(root);
            var manifest = new ManagedServiceManifest(
                ManagedServiceManifest.CurrentSchemaVersion,
                "escape",
                "Escape service",
                "runtime/escape/evil.exe",
                Array.Empty<string>(),
                ".",
                18080,
                "1.0");

            var error = Assert.Throws<InvalidDataException>(() => catalog.GetDefinition(manifest));
            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root, link);
            Cleanup(externalRoot);
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
