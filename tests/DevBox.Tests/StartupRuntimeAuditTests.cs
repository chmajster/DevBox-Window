using DevBox.App.Services;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class StartupRuntimeAuditTests
{
    [Fact]
    public void SingleInstanceGuard_RejectsSecondOwnerForSameRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-single-instance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = SingleInstanceGuard.TryAcquire(root);
            var second = SingleInstanceGuard.TryAcquire(root);

            Assert.NotNull(first);
            Assert.Null(second);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void SingleInstanceGuard_MutexNameIsCaseInsensitiveAndRootSpecific()
    {
        var root = Path.Combine(Path.GetTempPath(), "DevBox", "Root");
        var sameRootDifferentCase = root.ToUpperInvariant();
        var otherRoot = root + "-other";

        Assert.Equal(SingleInstanceGuard.BuildMutexName(root), SingleInstanceGuard.BuildMutexName(sameRootDifferentCase));
        Assert.NotEqual(SingleInstanceGuard.BuildMutexName(root), SingleInstanceGuard.BuildMutexName(otherRoot));
    }

    [Theory]
    [InlineData("..", "1.0.0", "runtime.exe")]
    [InlineData("php", "..", "runtime.exe")]
    [InlineData("php", "1.0.0", "../runtime.exe")]
    public void RuntimeCatalog_RejectsPathTraversal(string key, string version, string executable)
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new RuntimePlatformService(root);
            var package = new RuntimePackageEntry
            {
                Key = key,
                DisplayName = "Test",
                Version = version,
                Architecture = "x64",
                ExecutableRelativePath = executable
            };

            Assert.Throws<InvalidDataException>(() => service.SaveCatalogEntry(package));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RuntimeCatalog_RejectsArchiveRootTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-runtime-catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new RuntimePlatformService(root);
            var package = new RuntimePackageEntry
            {
                Key = "php",
                DisplayName = "Test",
                Version = "1.0.0",
                Architecture = "x64",
                ExecutableRelativePath = "runtime.exe",
                ArchiveRootDirectory = "../package"
            };

            Assert.Throws<InvalidDataException>(() => service.SaveCatalogEntry(package));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
