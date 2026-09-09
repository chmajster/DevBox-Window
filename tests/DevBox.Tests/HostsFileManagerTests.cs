using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class HostsFileManagerTests
{
    [Fact]
    public void EnsureMapping_AddsMappingWithoutDuplicatingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-hosts-tests", Guid.NewGuid().ToString("N"));
        var hosts = Path.Combine(root, "hosts");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(hosts, "127.0.0.1 localhost\n");
            var manager = new HostsFileManager(hosts);

            manager.EnsureMapping("127.0.0.1", "phpmyadmin.test");
            manager.EnsureMapping("127.0.0.1", "phpmyadmin.test");

            Assert.True(manager.HasMapping("127.0.0.1", "phpmyadmin.test"));
            Assert.Single(File.ReadAllLines(hosts), line => line.Contains("phpmyadmin.test", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RemoveMapping_PreservesOtherHostnamesOnSameLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-hosts-tests", Guid.NewGuid().ToString("N"));
        var hosts = Path.Combine(root, "hosts");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(hosts, "127.0.0.1 phpmyadmin.test other.test # local\n");
            var manager = new HostsFileManager(hosts);

            manager.RemoveMapping("phpmyadmin.test");

            var content = File.ReadAllText(hosts);
            Assert.DoesNotContain("phpmyadmin.test", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("other.test", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
