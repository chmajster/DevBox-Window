using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class SnapshotHardeningTests
{
    [Fact]
    public async Task CreateAsync_CancelledOperationDoesNotLeavePartialSnapshot()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "cancelled-snapshot");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                new ProjectSnapshotService(root).CreateAsync(project, cancellationToken: cancellation.Token));

            var snapshotRoot = Path.Combine(root, "backups", "projects");
            Assert.True(Directory.Exists(snapshotRoot));
            Assert.Empty(Directory.GetFiles(snapshotRoot));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task CreateAsync_WithoutDatabaseDoesNotAdvertiseDatabaseBackups()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "no-database-payload");
            var databaseBackup = Path.Combine(root, "database.sql");
            await File.WriteAllTextAsync(databaseBackup, "-- fixture");

            var result = await new ProjectSnapshotService(root).CreateAsync(
                project,
                new ProjectSnapshotOptions(IncludeDatabase: false),
                [databaseBackup]);

            using var archive = ZipFile.OpenRead(result.SnapshotPath);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.StartsWith("database/", StringComparison.Ordinal));
            var metadata = archive.GetEntry("snapshot.json")!;
            using var reader = new StreamReader(metadata.Open());
            using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
            Assert.Empty(document.RootElement.GetProperty("DatabaseBackups").EnumerateArray());
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsArchiveWithoutSnapshotMetadata()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "not-a-snapshot.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "project/devbox.json", Manifest("missing-metadata"));
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectSnapshotService(root).RestoreAsync(archivePath, "missing-metadata"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsOversizedProjectMetadata()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "oversized-metadata.devbox-snapshot.zip");
            var oversizedManifest = $$"""
            {"Name":"oversized","Domain":"oversized.test","DatabaseEngine":"none","Https":false,"Padding":"{{new string('x', 1024 * 1024 + 256)}}"}
            """;
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "snapshot.json", SnapshotMetadata("oversized"));
                WriteEntry(archive, "project/devbox.json", oversizedManifest);
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectSnapshotService(root).RestoreAsync(archivePath, "oversized"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsSymbolicLinkEntry()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "symlink.devbox-snapshot.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "snapshot.json", SnapshotMetadata("symlink-source"));
                WriteEntry(archive, "project/devbox.json", Manifest("symlink-source"));
                var link = archive.CreateEntry("project/linked-file");
                link.ExternalAttributes = (0xA000 | 0x1FF) << 16;
                using var writer = new StreamWriter(link.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write("target.txt");
            }

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new ProjectSnapshotService(root).RestoreAsync(archivePath, "symlink-copy"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task RestoreAsync_LongProjectNameGeneratesValidTestDomain()
    {
        var root = TemporaryRoot();
        try
        {
            var source = CreateProject(root, "short-source");
            var service = new ProjectSnapshotService(root);
            var snapshot = await service.CreateAsync(source);
            var longName = new string('a', 80);

            var restored = await service.RestoreAsync(snapshot.SnapshotPath, longName);

            Assert.True(Directory.Exists(restored));
            var site = new SiteManager(root).GetSites().Single(value => value.Name == longName);
            Assert.EndsWith(".test", site.Domain, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(site.Domain.Split('.')[0].Length, 1, 63);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string CreateProject(string root, string name)
    {
        var path = Path.Combine(root, "www", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, ProjectWorkspaceService.ManifestFileName), Manifest(name));
        File.WriteAllText(Path.Combine(path, "index.html"), "fixture");
        return path;
    }

    private static string Manifest(string name) => JsonSerializer.Serialize(new
    {
        Name = name,
        Domain = $"{name}.test",
        DatabaseEngine = "none",
        Https = false
    });

    private static string SnapshotMetadata(string projectName) => JsonSerializer.Serialize(new
    {
        SchemaVersion = 1,
        ProjectName = projectName,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Options = new ProjectSnapshotOptions(),
        DatabaseBackups = Array.Empty<string>()
    });

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
        writer.Write(content);
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-snapshot-hardening", Guid.NewGuid().ToString("N"));
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
}
