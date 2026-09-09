using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class EnvironmentReadinessTests
{
    [Fact]
    public void Check_MarksVerifiedMissingRuntimesAsAutomaticallyInstallable()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var service = new EnvironmentReadinessService(root, new RuntimeCatalog());

            var readiness = service.Check();

            Assert.False(readiness.IsReady);
            Assert.True(readiness.Items.Single(item => item.Key == "php").CanInstallAutomatically);
            Assert.True(readiness.Items.Single(item => item.Key == "nginx").CanInstallAutomatically);
            Assert.False(readiness.Items.Single(item => item.Key == "mysql").CanInstallAutomatically);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void Check_MarksBundledMySqlAsAutomaticallyInstallableBeforeActivation()
    {
        var root = TemporaryRoot();
        try
        {
            RuntimeLayout.EnsureInitialized(root);
            var bundledBin = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(bundledBin);
            File.WriteAllText(Path.Combine(bundledBin, "mysqld.exe"), "runtime");

            var service = new EnvironmentReadinessService(root, new RuntimeCatalog());
            var readiness = service.Check();
            var mysql = readiness.Items.Single(item => item.Key == "mysql");

            Assert.False(mysql.Ready);
            Assert.True(mysql.CanInstallAutomatically);
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
