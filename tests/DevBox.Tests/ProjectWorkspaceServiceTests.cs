using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProjectWorkspaceServiceTests
{
    [Fact]
    public void Detect_RecognizesLaravelAndComposerExtensions()
    {
        var root = TemporaryRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "artisan"), string.Empty);
            File.WriteAllText(Path.Combine(root, "composer.json"), """
                {
                  "require": {
                    "laravel/framework": "^12.0",
                    "ext-mbstring": "*",
                    "ext-pdo": "*"
                  },
                  "require-dev": {
                    "ext-xdebug": "*"
                  }
                }
                """);

            var service = CreateService(root);
            var result = service.Detect(root);

            Assert.Equal(ProjectKind.Laravel, result.Kind);
            Assert.Equal(new[] { "mbstring", "pdo", "xdebug" }, result.RequiredPhpExtensions);
            Assert.Contains(result.Evidence, value => value.Contains("Laravel", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Detect_RecognizesWordPress()
    {
        var root = TemporaryRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "wp-includes"));
            File.WriteAllText(Path.Combine(root, "wp-includes", "version.php"), "<?php");

            var service = CreateService(root);
            var result = service.Detect(root);

            Assert.Equal(ProjectKind.WordPress, result.Kind);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Create_LaravelProjectUsesPublicDocumentRootAndWritesManifest()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = CreateService(root);

            var site = service.Create(new ProjectCreateRequest(
                "demo-app",
                Kind: ProjectKind.Laravel,
                DatabaseName: "demo_db"));

            Assert.Equal(Path.Combine(root, "www", "demo-app", "public"), site.DocumentRoot);
            Assert.True(File.Exists(Path.Combine(root, "www", "demo-app", ProjectWorkspaceService.ManifestFileName)));
            Assert.True(File.Exists(Path.Combine(root, "www", "demo-app", ".devbox-scaffold-pending")));

            var manifest = service.LoadManifest(Path.Combine(root, "www", "demo-app"));
            Assert.NotNull(manifest);
            Assert.Equal(ProjectKind.Laravel, manifest!.Kind);
            Assert.Equal("demo_db", manifest.DatabaseName);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Import_CopiesExternalProjectAndDetectsFramework()
    {
        var root = TemporaryRoot();
        var source = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            File.WriteAllText(Path.Combine(source, "wp-config.php"), "<?php");
            File.WriteAllText(Path.Combine(source, "index.php"), "<?php echo 'wp';");
            var service = CreateService(root);

            var site = service.Import(new ProjectImportRequest(source, "wordpress-demo"));

            var copiedRoot = Path.Combine(root, "www", "wordpress-demo");
            Assert.Equal(copiedRoot, site.DocumentRoot);
            Assert.True(File.Exists(Path.Combine(copiedRoot, "wp-config.php")));
            var manifest = service.LoadManifest(copiedRoot);
            Assert.NotNull(manifest);
            Assert.Equal(ProjectKind.WordPress, manifest!.Kind);
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(source);
        }
    }

    [Fact]
    public void Import_InPlaceOutsideWwwIsRejected()
    {
        var root = TemporaryRoot();
        var source = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = CreateService(root);

            Assert.Throws<InvalidOperationException>(() => service.Import(
                new ProjectImportRequest(source, "external", CopyIntoDevBox: false)));
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(source);
        }
    }

    [Fact]
    public async Task CheckHealthAndRepair_RecreatesMissingVhostAndManifest()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var phpRoot = Path.Combine(root, "runtime", "php", "current");
            Directory.CreateDirectory(phpRoot);
            File.WriteAllText(Path.Combine(phpRoot, "php-cgi.exe"), "runtime");

            var siteManager = new SiteManager(root);
            var service = new ProjectWorkspaceService(
                root,
                siteManager,
                new PhpExtensionInspector(root),
                new LocalCertificateManager(root));
            var site = siteManager.Create("health-demo");
            var projectRoot = Path.Combine(root, "www", "health-demo");
            var vhost = siteManager.GetNginxConfigPath(site.Domain);
            File.Delete(vhost);

            var before = await service.CheckHealthAsync(site);
            Assert.Contains(before.Checks, check => check.Key == "nginx-vhost" && check.State == ProjectHealthState.Error);
            Assert.Contains(before.Checks, check => check.Key == "manifest" && check.State == ProjectHealthState.Warning);

            var repaired = await service.RepairAsync(site);

            Assert.True(File.Exists(vhost));
            Assert.True(File.Exists(Path.Combine(projectRoot, ProjectWorkspaceService.ManifestFileName)));
            Assert.Contains(repaired.Repaired, value => value.Contains("Nginx", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static ProjectWorkspaceService CreateService(string root)
    {
        RuntimeLayout.EnsureInitialized(root);
        return new ProjectWorkspaceService(
            root,
            new SiteManager(root),
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
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
