using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class RuntimeCatalogMalformedInputTests
{
    [Fact]
    public void UserCatalog_NullEntry_IsReportedAsInvalidData()
    {
        var root = NewRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "config", "runtime-catalog.json"), "[null]");
            using var service = new RuntimePlatformService(root);

            var error = Assert.Throws<InvalidDataException>(() => service.GetCatalog());

            Assert.Contains("null entry", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("runtime-catalog.json", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void ReleaseCatalog_DuplicateIdentity_IsReportedAsInvalidData()
    {
        var root = NewRoot();
        try
        {
            var entry = """
{
  "key": "mysql",
  "displayName": "MySQL",
  "version": "8.4.11",
  "architecture": "x64",
  "executableRelativePath": "bin/mysqld.exe",
  "downloadUrl": "https://example.test/mysql.zip",
  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "archiveRootDirectory": "mysql-8.4.11-winx64",
  "recommended": true
}
""";
            File.WriteAllText(
                Path.Combine(root, "config", "runtime-catalog.release.json"),
                $"[{entry},{entry}]");
            using var service = new RuntimePlatformService(root);

            var error = Assert.Throws<InvalidDataException>(() => service.GetCatalog());

            Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("runtime-catalog.release.json", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-malformed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        return root;
    }

    private static void TryDelete(string path)
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
