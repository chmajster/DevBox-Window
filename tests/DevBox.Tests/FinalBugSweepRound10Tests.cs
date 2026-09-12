using System.IO.Compression;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound10Tests
{
    [Fact]
    public void ArchiveSafety_RejectsCaseInsensitiveDuplicateOutputPath()
    {
        var root = NewRoot();
        try
        {
            var archivePath = Path.Combine(root, "duplicate.zip");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "payload/tool.txt", "one");
                WriteEntry(archive, "PAYLOAD/TOOL.TXT", "two");
            }

            Assert.Throws<InvalidDataException>(() => ArchiveSafety.ExtractZipSafely(
                archivePath, Path.Combine(root, "out"), 1024 * 1024, 100, "fixture"));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void PathSafety_RejectsProtectedRootThatIsAReparsePoint_WhenSupported()
    {
        var parent = NewRoot();
        var target = Path.Combine(parent, "target");
        var link = Path.Combine(parent, "linked-root");
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            var child = Path.Combine(link, "child");
            Directory.CreateDirectory(child);
            Assert.Throws<InvalidOperationException>(() =>
                PathSafety.EnsureUnderRootWithoutReparsePoints(link, child, "unsafe"));
        }
        finally { Delete(parent); }
    }

    [Fact]
    public void ManagedServiceCatalog_RejectsExecutableThroughReparsePoint_WhenSupported()
    {
        var root = NewRoot();
        var external = Path.Combine(Path.GetTempPath(), "devbox-round10-external", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "tool.exe"), "fixture");
        var link = Path.Combine(root, "linked");
        try
        {
            try { Directory.CreateSymbolicLink(link, external); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            var manifest = new ManagedServiceManifest(
                ManagedServiceManifest.CurrentSchemaVersion, "fixture", "Fixture",
                "linked/tool.exe", Array.Empty<string>(), ".", 8123, "1.0");
            Assert.Throws<InvalidDataException>(() => new ManagedServiceCatalog(root).Save([manifest]));
        }
        finally { Delete(root); Delete(external); }
    }

    [Fact]
    public void EnvironmentProfile_RejectsActionUnsupportedByProjectActionPolicy()
    {
        var root = NewRoot();
        try
        {
            var profile = new EnvironmentProfile
            {
                Key = "bad-action",
                DisplayName = "Bad action",
                Kind = ProjectKind.Php,
                Actions = [new ProjectActionDefinition("run", "Run", "powershell", ["-NoProfile"])]
            };
            Assert.Throws<InvalidDataException>(() => new EnvironmentProfileService(root).SaveCustomProfile(profile));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task GitBootstrap_RejectsMalformedTestDomainBeforeResolvingGit()
    {
        var root = NewRoot();
        try
        {
            var service = new GitProjectBootstrapService(root);
            await Assert.ThrowsAsync<ArgumentException>(() => service.BootstrapAsync(new GitBootstrapRequest(
                "https://example.com/repository.git", "demo", Domain: "bad..test")));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void ConfigurationRestore_RejectsOversizedBackupBeforeReadingIt()
    {
        var root = NewRoot();
        try
        {
            var backupRoot = Path.Combine(root, "backups", "configuration");
            Directory.CreateDirectory(backupRoot);
            var backup = Path.Combine(backupRoot, "nginx-oversized.conf.bak");
            using (var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write))
                stream.SetLength(2L * 1024 * 1024 + 1);
            Assert.Throws<InvalidDataException>(() => new ConfigurationFileService(root).RestoreBackup("nginx", backup));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task ProcessOutputCapture_DrainsButBoundsRetainedText()
    {
        var input = new string('x', 100_000);
        var output = await ProcessOutputCapture.ReadBoundedAsync(new StringReader(input), 1024);
        Assert.StartsWith(new string('x', 1024), output, StringComparison.Ordinal);
        Assert.Contains("[output truncated by DevBox]", output, StringComparison.Ordinal);
        Assert.True(output.Length < 1200);
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round10", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "www"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
