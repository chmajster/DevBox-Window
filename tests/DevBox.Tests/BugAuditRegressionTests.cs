using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class BugAuditRegressionTests
{
    [Fact]
    public async Task ProjectExport_DestinationInsideProject_DoesNotIncludeItself()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "self-export");
            File.WriteAllText(Path.Combine(project, "index.php"), "<?php echo 'ok';");
            var destination = Path.Combine(project, "export.devbox-project.zip");
            var service = new ProjectTransferService(root);

            var result = await service.ExportAsync(project, destinationPath: destination);

            Assert.Equal(destination, result.ArchivePath);
            using var archive = ZipFile.OpenRead(destination);
            Assert.Contains(archive.Entries, entry => entry.FullName == "project/index.php");
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("export.devbox-project.zip", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectExport_PreCancelledOperation_PreservesExistingDestination()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "cancel-export");
            var destination = Path.Combine(project, "existing.devbox-project.zip");
            await File.WriteAllTextAsync(destination, "existing-good-file");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var service = new ProjectTransferService(root);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.ExportAsync(project, destinationPath: destination, cancellationToken: cancellation.Token));

            Assert.Equal("existing-good-file", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectExport_DuplicateDatabaseBackupNames_AreRejected()
    {
        var root = TemporaryRoot();
        try
        {
            var project = CreateProject(root, "duplicate-db-export");
            var firstDirectory = Path.Combine(root, "db-a");
            var secondDirectory = Path.Combine(root, "db-b");
            Directory.CreateDirectory(firstDirectory);
            Directory.CreateDirectory(secondDirectory);
            var first = Path.Combine(firstDirectory, "backup.sql");
            var second = Path.Combine(secondDirectory, "backup.sql");
            await File.WriteAllTextAsync(first, "-- first");
            await File.WriteAllTextAsync(second, "-- second");
            var destination = Path.Combine(root, "duplicate.devbox-project.zip");
            var service = new ProjectTransferService(root);

            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.ExportAsync(
                project,
                new ProjectSnapshotOptions(IncludeDatabase: true),
                [first, second],
                destination));

            Assert.Contains("duplicate file name", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProjectImport_RejectsOversizedTransferMetadata()
    {
        var root = TemporaryRoot();
        try
        {
            var archivePath = Path.Combine(root, "oversized.devbox-project.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                var transfer = archive.CreateEntry("transfer.json", CompressionLevel.NoCompression);
                await using (var stream = transfer.Open())
                await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: false))
                    await writer.WriteAsync(new string(' ', 1024 * 1024 + 1));

                var projectManifest = archive.CreateEntry("project/devbox.json");
                await using var manifestStream = projectManifest.Open();
                await using var manifestWriter = new StreamWriter(manifestStream);
                await manifestWriter.WriteAsync("{\"Name\":\"x\",\"Domain\":\"x.test\",\"DatabaseEngine\":\"none\",\"Https\":false}");
            }

            var service = new ProjectTransferService(root);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportAsync(archivePath));
            Assert.Contains("metadata limit", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplicationUpdate_MalformedJson_IsReportedAsInvalidData()
    {
        using var http = new HttpClient(new StaticHandler("{"u8.ToArray()));
        using var service = new ApplicationUpdateService(new Version(1, 0, 0), http);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CheckAsync());
    }

    [Fact]
    public async Task SelfUpdate_MalformedJson_IsReportedAsInvalidData()
    {
        var root = TemporaryRoot();
        try
        {
            using var http = new HttpClient(new StaticHandler("{"u8.ToArray()));
            using var service = new ApplicationSelfUpdateService(root, http);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadLatestInstallerAsync(new Version(0, 0, 0)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SelfUpdate_RejectsOversizedReleaseMetadataBeforeReadingBody()
    {
        var root = TemporaryRoot();
        try
        {
            using var http = new HttpClient(new StaticHandler("{}"u8.ToArray(), declaredLength: 10L * 1024 * 1024));
            using var service = new ApplicationSelfUpdateService(root, http);

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadLatestInstallerAsync(new Version(0, 0, 0)));
            Assert.Contains("too large", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ProcessWait_CancellationIsNotTreatedAsTimeout()
    {
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
        }) ?? throw new InvalidOperationException("Unable to start cancellation fixture process.");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ProcessManager.WaitForExitAsync(process, TimeSpan.FromSeconds(30), cancellation.Token));
            Assert.False(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
    }

    private static string CreateProject(string root, string name)
    {
        var project = Path.Combine(root, "www", name);
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, ProjectWorkspaceService.ManifestFileName),
            JsonSerializer.Serialize(new { Name = name, Domain = $"{name}.test", DatabaseEngine = "none", Https = false }));
        return project;
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-bug-audit-tests", Guid.NewGuid().ToString("N"));
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
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StaticHandler(byte[] payload, long? declaredLength = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(payload);
            if (declaredLength.HasValue)
                content.Headers.ContentLength = declaredLength.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
