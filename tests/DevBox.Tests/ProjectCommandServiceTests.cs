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
        var siteManager = new SiteManager(root);
        var workspace = new ProjectWorkspaceService(
            root,
            siteManager,
            new PhpExtensionInspector(root),
            new LocalCertificateManager(root));
        return new ProjectCommandService(root, workspace);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
