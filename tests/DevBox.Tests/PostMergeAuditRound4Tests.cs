using System.Net;
using System.Net.Sockets;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class PostMergeAuditRound4Tests
{
    [Fact]
    public void PhpMyAdminConfig_RejectsStaleDatabasePort()
    {
        const string config = "<?php\n$cfg['blowfish_secret'] = 'x';\n$cfg['Servers'][$i]['auth_type'] = 'cookie';\n$cfg['Servers'][$i]['host'] = '127.0.0.1';\n$cfg['Servers'][$i]['port'] = '3306';\n$cfg['Servers'][$i]['AllowNoPassword'] = true;\n$cfg['TempDir'] = 'tmp';\n";
        Assert.True(AddonInstaller.IsPhpMyAdminConfigUsable(config, 3306));
        Assert.False(AddonInstaller.IsPhpMyAdminConfigUsable(config, 3316));
    }

    [Fact]
    public void ManagedServiceCatalog_RejectsHttpsAndDatabasePorts()
    {
        var root = NewRoot();
        try
        {
            var catalog = new ManagedServiceCatalog(root);
            foreach (var port in new[] { 443, 3316, 5432 })
            {
                var manifest = new ManagedServiceManifest(
                    ManagedServiceManifest.CurrentSchemaVersion,
                    $"custom-{port}",
                    "Custom",
                    "runtime/custom/tool.exe",
                    Array.Empty<string>(),
                    ".",
                    port,
                    "1");
                Assert.Throws<InvalidDataException>(() => catalog.Upsert(manifest));
            }
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void DatabaseRuntime_RegisterRejectsExplicitPortUsedByAnotherProcess()
    {
        var root = NewRoot();
        TcpListener? listener = null;
        try
        {
            var executable = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin", "mysqld.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");

            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            using var databases = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidOperationException>(() => databases.Register("mysql", "8.4.11", port));
        }
        finally
        {
            listener?.Stop();
            TryDelete(root);
        }
    }

    [Fact]
    public async Task DatabaseRuntime_UnrecognizedNonEmptyDataDirectory_IsPreserved()
    {
        var root = NewRoot();
        try
        {
            var executable = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin", "mysqld.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture");
            var data = Path.Combine(root, "data", "mysql", "8.4.11");
            Directory.CreateDirectory(data);
            var sentinel = Path.Combine(data, "user-data.bin");
            File.WriteAllText(sentinel, "preserve-me");

            using var databases = new DatabaseRuntimeService(root);
            await Assert.ThrowsAsync<InvalidOperationException>(() => databases.EnsureInitializedAsync("mysql", "8.4.11", 3406));

            Assert.True(File.Exists(sentinel));
            Assert.Equal("preserve-me", File.ReadAllText(sentinel));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void DatabaseRuntime_DuplicateEngineVersionRegistration_IsRejected()
    {
        var root = NewRoot();
        try
        {
            var config = Path.Combine(root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "database-runtimes.json"), """
[
  { "engine": "mysql", "version": "8.4.11", "port": 3406 },
  { "engine": "MYSQL", "version": "8.4.11", "port": 3407 }
]
""");

            using var databases = new DatabaseRuntimeService(root);
            Assert.Throws<InvalidDataException>(() => databases.GetInstances());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void EnvironmentProfiles_ConcurrentWritersDoNotLoseUpdates()
    {
        var root = NewRoot();
        try
        {
            var first = Profile("round4-first");
            var second = Profile("round4-second");
            Parallel.Invoke(
                () => new EnvironmentProfileService(root).SaveCustomProfile(first),
                () => new EnvironmentProfileService(root).SaveCustomProfile(second));

            var keys = new EnvironmentProfileService(root).GetProfiles().Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains(first.Key, keys);
            Assert.Contains(second.Key, keys);
        }
        finally { TryDelete(root); }
    }

    private static EnvironmentProfile Profile(string key) => new()
    {
        Key = key,
        DisplayName = key,
        Kind = ProjectKind.EmptyPhp,
        Database = new EnvironmentDatabasePin("none", null, null),
        Description = "round 4 regression fixture"
    };

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-round4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
