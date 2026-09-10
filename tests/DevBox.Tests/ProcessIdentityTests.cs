using DevBox.Core.Models;
using DevBox.Core.Services;
using Xunit;

namespace DevBox.Tests;

public sealed class ProcessIdentityTests
{
    [Fact]
    public async Task StartAsync_PersistsProcessStartTimeInPidMarker()
    {
        var root = TemporaryRoot();
        try
        {
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var definition = new ServiceDefinition(
                "identity", "Identity test", executable,
                ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"],
                root, 0, "test",
                ShutdownTimeout: TimeSpan.FromMilliseconds(100));
            using var manager = new ProcessManager();

            var started = await manager.StartAsync(definition);
            var marker = Path.Combine(root, "tmp", "services", "identity.pid");
            var lines = File.ReadAllLines(marker);

            Assert.Equal(3, lines.Length);
            Assert.Equal(started.ProcessId?.ToString(), lines[0]);
            Assert.Equal(Path.GetFullPath(executable), lines[1]);
            Assert.True(long.TryParse(lines[2], out var startTimeTicks));
            Assert.True(startTimeTicks > 0);

            await manager.StopAsync(definition);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public void GetStatus_RejectsMarkerForReusedPidWithWrongStartTime()
    {
        var root = TemporaryRoot();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        try
        {
            var executable = process.MainModule?.FileName ?? throw new InvalidOperationException("Test process path unavailable.");
            var definition = new ServiceDefinition("reused", "Reused PID", executable, [], root, 0, "test");
            var markerDirectory = Path.Combine(root, "tmp", "services");
            Directory.CreateDirectory(markerDirectory);
            var marker = Path.Combine(markerDirectory, "reused.pid");
            File.WriteAllLines(marker,
            [
                process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Path.GetFullPath(executable),
                DateTime.UtcNow.AddHours(-1).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ]);
            using var manager = new ProcessManager();

            var status = manager.GetStatus(definition);

            Assert.Equal(ServiceState.Stopped, status.State);
            Assert.False(File.Exists(marker));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-process-identity-tests", Guid.NewGuid().ToString("N"));
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
