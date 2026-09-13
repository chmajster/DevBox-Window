using System.IO.Compression;
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
