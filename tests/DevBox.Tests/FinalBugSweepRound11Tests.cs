using System.Diagnostics;
using System.Text.Json;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound11Tests
{
    [Fact]
    public void EnvironmentLock_NullCollectionsAreControlledDataErrors()
    {
        var root = TempRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), """
            {"SchemaVersion":1,"ProjectName":"demo","Domain":"demo.test","Runtimes":null,"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            """);

            using var service = new EnvironmentLockService(root);
            Assert.Throws<InvalidDataException>(() => service.Load(project));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void EnvironmentLock_RejectsMalformedTestDomain()
    {
        var root = TempRoot();
        try
        {
            var project = Path.Combine(root, "www", "demo");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, EnvironmentLockService.LockFileName), """
            {"SchemaVersion":1,"ProjectName":"demo","Domain":"bad..test","Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":[]}
            """);

            using var service = new EnvironmentLockService(root);
            Assert.Throws<InvalidDataException>(() => service.Load(project));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RemoteEnvironment_NullActionsAreControlledDataErrors()
    {
        var root = TempRoot();
        try
        {
            Directory.CreateDirectory(root);
            var share = Path.Combine(root, "bad.devbox-env.json");
            File.WriteAllText(share, """
            {"SchemaVersion":1,"Name":"bad","Profile":{"Key":"bad","DisplayName":"Bad","Kind":1,"Runtimes":{},"Database":{"Engine":"none","Version":null,"DatabaseName":null,"Port":null},"Https":false,"Addons":[],"Services":[],"Actions":null,"Description":""},"Metadata":{}}
            """);

            var service = new RemoteEnvironmentService(root);
            Assert.Throws<InvalidDataException>(() => service.Import(share));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void DatabaseRuntime_RunningInstanceRejectsPortMutation()
    {
        var root = TempRoot();
        Process? process = null;
        try
        {
            var runtimeBin = Path.Combine(root, "runtime", "mysql", "8.4.11", "bin");
            Directory.CreateDirectory(runtimeBin);
            var executable = Path.Combine(runtimeBin, "mysqld.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), executable);

            using var service = new DatabaseRuntimeService(root);
            _ = service.Register("mysql", "8.4.11", 3406);

            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping 127.0.0.1 -n 30 >nul");
            process = Process.Start(info)!;
            Assert.NotNull(process);

            var markerDirectory = Path.Combine(root, "tmp", "services");
            Directory.CreateDirectory(markerDirectory);
            var marker = Path.Combine(markerDirectory, "db-mysql-8-4-11.pid");
            File.WriteAllLines(marker,
            [
                process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(executable),
                process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ]);

            var error = Assert.Throws<InvalidOperationException>(() => service.Register("mysql", "8.4.11", 3407));
            Assert.Contains("Stop MySQL 8.4.11", error.Message, StringComparison.Ordinal);
            Assert.Equal(3406, service.GetInstances("mysql").Single().Port);
        }
        finally
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch { }
            process?.Dispose();
            Delete(root);
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
