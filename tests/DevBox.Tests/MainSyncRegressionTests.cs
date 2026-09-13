using System.Diagnostics;
using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class MainSyncRegressionTests
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
        var value = ValidLock() with { Domain = "bad..test" };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void EnvironmentLock_RejectsNullAndUnsafeServiceKeys()
    {
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(ValidLock() with { Services = new string[] { null! } }));
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(ValidLock() with { Services = ["../redis"] }));
    }

    [Fact]
    public void EnvironmentLock_RejectsDatabaseEngineWithoutVersion()
    {
        var value = ValidLock() with { Database = new EnvironmentDatabasePin("mysql", null, "demo", 3306) };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void RemoteEnvironment_NullActionsAreControlledDataErrors()
    {
        var root = TempRoot();
        try
        {
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
    public async Task TaskCenter_WaitDuringInitialPublishWaitsForExecution()
    {
        var root = TempRoot();
        try
        {
            using var center = new PlatformTaskCenter(root, 1);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PlatformTaskSnapshot>? observedWait = null;
            center.TaskChanged += (_, snapshot) =>
            {
                if (snapshot.State == PlatformTaskState.Queued && observedWait is null)
                    observedWait = center.WaitAsync(snapshot.Id);
            };

            _ = center.Enqueue("wait-race", async (_, _) => await gate.Task.ConfigureAwait(false));
            Assert.NotNull(observedWait);
            Assert.False(observedWait!.IsCompleted);
            gate.TrySetResult();
            var result = await observedWait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlatformTaskState.Completed, result.State);
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task TaskCenter_UnwritableHistoryDoesNotBreakTaskLifecycle()
    {
        var root = TempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "logs"), "blocks history directory creation");
            using var center = new PlatformTaskCenter(root, 1);
            var id = center.Enqueue("history-failure", (_, _) => Task.CompletedTask);
            var result = await center.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PlatformTaskState.Completed, result.State);
            Assert.Equal(100, result.Progress);
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

            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add("ping 127.0.0.1 -n 30 >nul");
            process = Process.Start(info)!;
            Assert.NotNull(process);

            var markerDirectory = Path.Combine(root, "tmp", "services");
            Directory.CreateDirectory(markerDirectory);
            File.WriteAllLines(Path.Combine(markerDirectory, "db-mysql-8-4-11.pid"),
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

    private static EnvironmentLockFile ValidLock() => new()
    {
        ProjectName = "demo",
        Domain = "demo.test",
        Runtimes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        Database = new EnvironmentDatabasePin("none", null, null),
        Addons = Array.Empty<string>(),
        Services = Array.Empty<string>(),
        Actions = Array.Empty<ProjectActionDefinition>()
    };

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "DevBoxTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Delete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
