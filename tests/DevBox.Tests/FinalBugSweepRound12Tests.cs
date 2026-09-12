using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class FinalBugSweepRound12Tests
{
    [Fact]
    public async Task TaskCenter_WaitAsyncDuringInitialPublishWaitsForExecution()
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
    public void EnvironmentLock_RejectsNullServiceKey()
    {
        var value = ValidLock() with { Services = new string[] { null! } };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void EnvironmentLock_RejectsDatabaseEngineWithoutVersion()
    {
        var value = ValidLock() with { Database = new EnvironmentDatabasePin("mysql", null, "demo", 3306) };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
    }

    [Fact]
    public void EnvironmentLock_RejectsUnsafeServiceKey()
    {
        var value = ValidLock() with { Services = ["../redis"] };
        Assert.Throws<InvalidDataException>(() => EnvironmentLockService.ValidateLockData(value));
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
