using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound17Tests
{
    [Fact]
    public void RuntimeCatalog_RejectsOversizedCustomCatalogBeforeParsing()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "runtime-catalog.json"), new string(' ', 2 * 1024 * 1024 + 1));

            using var service = new RuntimePlatformService(root);
            var error = Assert.Throws<InvalidDataException>(() => service.GetCatalog());
            Assert.Contains("2 MiB", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RuntimeCatalog_RevalidatesConfigRootAfterServiceConstruction()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            using var service = new RuntimePlatformService(root);
            var config = Path.Combine(root, "config");
            if (Directory.Exists(config))
                Directory.Delete(config, recursive: true);
            if (!TryCreateDirectoryLink(config, external))
                return;

            Assert.Throws<InvalidOperationException>(() => service.GetCatalog());
            TryDeleteLink(config);
        }
        finally
        {
            TryDeleteLink(Path.Combine(root, "config"));
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void ProjectManifest_DirectReadAndWriteRejectOutsideWww()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-project-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            var service = Workspace(root);
            var manifest = ValidManifest();

            Assert.Throws<InvalidOperationException>(() => service.SaveManifest(external, manifest));
            Assert.Throws<InvalidOperationException>(() => service.LoadManifest(external));
            Assert.False(File.Exists(Path.Combine(external, ProjectWorkspaceService.ManifestFileName)));
        }
        finally
        {
            Delete(root);
            Delete(external);
        }
    }

    [Fact]
    public void ProjectManifest_RejectsOversizedManagedManifestBeforeParsing()
    {
        var root = NewRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(
                Path.Combine(project, ProjectWorkspaceService.ManifestFileName),
                new string(' ', 2 * 1024 * 1024 + 1));

            var service = Workspace(root);
            var error = Assert.Throws<InvalidDataException>(() => service.LoadManifest(project));
            Assert.Contains("2 MiB", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ProjectDetection_RejectsOversizedComposerBeforeParsing()
    {
        var root = NewRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "composer.json"), new string(' ', 2 * 1024 * 1024 + 1));

            var service = Workspace(root);
            var error = Assert.Throws<InvalidDataException>(() => service.Detect(project));
            Assert.Contains("2 MiB", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ProjectDetection_RejectsComposerReparseFile()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-composer-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            var target = Path.Combine(external, "composer.json");
            File.WriteAllText(target, "{\"require\":{}}");
            var link = Path.Combine(project, "composer.json");
            if (!TryCreateFileLink(link, target))
                return;

            var service = Workspace(root);
            Assert.Throws<InvalidDataException>(() => service.Detect(project));
        }
        finally
        {
            TryDeleteFileLink(Path.Combine(root, "www", "demo", "composer.json"));
            Delete(root);
            Delete(external);
        }
    }

    private static ProjectWorkspaceService Workspace(string root) =>
        new(
            root,
            new SiteManager(root),
            new PhpExtensionInspector(root),
            new LocalCertificateManager(root));

    private static DevBoxProjectManifest ValidManifest() =>
        new(
            DevBoxProjectManifest.CurrentSchemaVersion,
            "demo",
            "demo.test",
            ProjectKind.Php,
            null,
            null,
            "mysql",
            "demo",
            false,
            Array.Empty<string>());

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

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteFileLink(string path)
    {
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                File.Delete(path);
        }
        catch { }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round17-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "www"));
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
