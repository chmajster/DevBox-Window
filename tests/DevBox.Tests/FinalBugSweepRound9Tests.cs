using System.Reflection;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound9Tests
{
    [Theory]
    [InlineData("https://addon.test")]
    [InlineData("http://addon.test:8080")]
    public void AddonCatalog_RejectsLocalUrlsThatCannotMatchGeneratedVhost(string localUrl)
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "addons.json"), Catalog(localUrl, "addon"));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void AddonCatalog_RejectsOverlongKey()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "addons.json"), Catalog("http://addon.test", new string('a', 65)));
            Assert.Throws<InvalidDataException>(() => new AddonCatalog(root).GetAddons());
        }
        finally { Delete(root); }
    }

    [Fact]
    public void PathSafety_RejectsCandidateOutsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureUnderRootWithoutReparsePoints(root, outside, "unsafe"));
        }
        finally { Delete(root); Delete(outside); }
    }

    [Fact]
    public void PathSafety_RejectsReparsePointWhenSupportedByRunner()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        var outside = root + "-outside";
        var link = Path.Combine(root, "linked");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
            Assert.Throws<InvalidOperationException>(() => PathSafety.EnsureUnderRootWithoutReparsePoints(root, Path.Combine(link, "child"), "unsafe"));
        }
        finally { Delete(root); Delete(outside); }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round9", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "www"));
        return root;
    }

    private static string Catalog(string localUrl, string key) => $$"""
    [{"Key":"{{key}}","DisplayName":"Addon","Description":"fixture","InstallRelativePath":"www/addon","EntryPointRelativePath":"www/addon/index.php","LocalUrl":"{{localUrl}}","RequiredPhpExtensions":[],"Version":"1.0","DownloadUrl":"https://example.test/addon.zip","Sha256":"{{new string('a',64)}}","ArchiveRootDirectory":"package"}]
    """;

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
