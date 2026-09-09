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
