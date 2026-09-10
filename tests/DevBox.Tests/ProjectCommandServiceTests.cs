using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProjectCommandServiceTests
{
    [Fact]
    public void GetPresets_LaravelExposesOnlyKnownCommands()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "artisan"), string.Empty);
            File.WriteAllText(Path.Combine(project, "composer.json"), "{\"require\":{\"laravel/framework\":\"^12.0\"}}");
            File.WriteAllText(Path.Combine(project, "package.json"), "{}");
            var service = CreateService(root);

            var presets = service.GetPresets(project);

            Assert.Contains(presets, preset => preset.Key == "composer-install");
            Assert.Contains(presets, preset => preset.Key == "npm-build");
            Assert.Contains(presets, preset => preset.Key == "laravel-migrate");
            Assert.Contains(presets, preset => preset.Key == "laravel-optimize-clear");
            Assert.DoesNotContain(presets, preset => preset.Arguments.Any(argument => argument.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ResolveTool_UsesNodeVersionPinnedByProjectManifest()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "package.json"), "{}");
            var runtime = Path.Combine(root, "runtime", "node", NodeRuntimeCatalog.RecommendedVersion);
            Directory.CreateDirectory(runtime);
            var npm = Path.Combine(runtime, "npm.cmd");
            File.WriteAllText(npm, "@echo off");

            var workspace = CreateWorkspace(root);
            workspace.SaveManifest(project, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                "demo",
                "demo.test",
                ProjectKind.Php,
                null,
                NodeRuntimeCatalog.RecommendedVersion,
                "none",
                null,
                false,
                Array.Empty<string>(),
                Array.Empty<string>()));
            var service = new ProjectCommandService(root, workspace);

            Assert.Equal(npm, service.ResolveTool("npm", project));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ResolveTool_DoesNotFallBackWhenPinnedNodeIsMissing()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "package.json"), "{}");
            var workspace = CreateWorkspace(root);
            workspace.SaveManifest(project, new DevBoxProjectManifest(
                DevBoxProjectManifest.CurrentSchemaVersion,
                "demo",
                "demo.test",
                ProjectKind.Php,
                null,
                "99.0.0",
                "none",
                null,
                false,
                Array.Empty<string>(),
                Array.Empty<string>()));
            var service = new ProjectCommandService(root, workspace);

            Assert.Throws<FileNotFoundException>(() => service.ResolveTool("npm", project));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsUnknownPresetBeforeProcessStart()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "index.php"), "<?php");
            var service = CreateService(root);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RunAsync(project, "arbitrary-shell-command"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void GetPresets_RejectsProjectOutsideDevBoxWww()
    {
        var root = TemporaryRoot();
        var external = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = CreateService(root);

            Assert.Throws<InvalidOperationException>(() => service.GetPresets(external));
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(external);
        }
    }

    private static ProjectCommandService CreateService(string root)
    {
        var workspace = CreateWorkspace(root);
        return new ProjectCommandService(root, workspace);
    }

    private static ProjectWorkspaceService CreateWorkspace(string root)
    {
        var siteManager = new SiteManager(root);
        return new ProjectWorkspaceService(
            root,
            siteManager,
            new PhpExtensionInspector(root),
            new LocalCertificateManager(root));
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
