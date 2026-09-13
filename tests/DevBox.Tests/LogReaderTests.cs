using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class LogReaderTests
{
    [Fact]
    public void ReadTail_ReturnsRequestedLastLines()
    {
        var root = TemporaryRoot();
        try
        {
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            File.WriteAllLines(Path.Combine(logs, "app.log"), ["one", "two", "three"]);
            var reader = new LogReader(root);

            var lines = reader.ReadTail("app.log", 2);

            Assert.Equal(["two", "three"], lines);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void ReadTail_RejectsTraversal()
    {
        var root = TemporaryRoot();
        try
        {
            var reader = new LogReader(root);
            Assert.Throws<ArgumentException>(() => reader.ReadTail("../secret.log"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Clear_RejectsSymbolicLinkAndPreservesTarget()
    {
        var root = TemporaryRoot();
        var externalRoot = TemporaryRoot();
        var logs = Path.Combine(root, "logs");
        var target = Path.Combine(externalRoot, "important.txt");
        var link = Path.Combine(logs, "app.log");
        Directory.CreateDirectory(logs);
        File.WriteAllText(target, "do-not-truncate");

        if (!TryCreateFileLink(link, target))
        {
            DeleteRoot(root);
            DeleteRoot(externalRoot);
            return;
        }

        try
        {
            var reader = new LogReader(root);
            Assert.Throws<InvalidOperationException>(() => reader.Clear("app.log"));
            Assert.Equal("do-not-truncate", File.ReadAllText(target));
            Assert.DoesNotContain("app.log", reader.GetAvailableLogs());
        }
        finally
        {
            TryDeleteLink(link);
            DeleteRoot(root);
            DeleteRoot(externalRoot);
        }
    }

    [Fact]
    public void ReadTail_RejectsReparsePointLogsDirectory()
    {
        var root = TemporaryRoot();
        var externalRoot = TemporaryRoot();
        var logs = Path.Combine(root, "logs");
        File.WriteAllText(Path.Combine(externalRoot, "app.log"), "external");

        if (!TryCreateDirectoryLink(logs, externalRoot))
        {
            DeleteRoot(root);
            DeleteRoot(externalRoot);
            return;
        }

        try
        {
            var reader = new LogReader(root);
            Assert.Throws<InvalidOperationException>(() => reader.ReadTail("app.log"));
        }
        finally
        {
            TryDeleteLink(logs);
            DeleteRoot(root);
            DeleteRoot(externalRoot);
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

    private static void TryDeleteLink(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            else if (Directory.Exists(path))
                Directory.Delete(path);
        }
        catch
        {
        }
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
            Directory.Delete(root, recursive: true);
    }
}
