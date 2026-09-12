using Xunit;
using System.Net;
using System.Net.Sockets;
using DevBox.Core.Models;
using DevBox.Core.Services;

namespace DevBox.Tests;

public sealed class ProcessManagerTests
{
    [Fact]
    public async Task StartAsync_MissingExecutable_ThrowsFileNotFoundException()
    {
        using var manager = new ProcessManager();
        var definition = Definition(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"), 0);

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.StartAsync(definition));
    }

    [Fact]
    public async Task StartAsync_InvalidExecutable_ReportsControlledError()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(root, "broken.exe");
            File.WriteAllText(executable, "not a Windows executable");
            using var manager = new ProcessManager();
            var definition = new ServiceDefinition("broken-start", "Broken service", executable, [], root, 0, "test");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(definition));

            Assert.Contains("Unable to start Broken service", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StartAsync_OccupiedPort_RefusesToStartProcess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var manager = new ProcessManager();
        var executable = Path.Combine(Environment.SystemDirectory, "where.exe");
        var definition = Definition(executable, port, "cmd.exe");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(definition));
        Assert.Contains($"Port {port}", error.Message);
    }

    [Fact]
    public async Task StartAsync_RunningService_DoesNotCreateSecondInstance()
    {
        using var manager = new ProcessManager();
        var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var definition = new ServiceDefinition(
            "test", "Test", executable,
            new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
            Path.GetTempPath(), 0, "test",
            ShutdownTimeout: TimeSpan.FromMilliseconds(100));

        var first = await manager.StartAsync(definition);
        var second = await manager.StartAsync(definition);

        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.Equal(ServiceState.Running, second.State);

        await manager.StopAsync(definition);
    }

    [Fact]
    public async Task StopAsync_BrokenGracefulStopCommand_FallsBackToManagedTermination()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var brokenStop = Path.Combine(root, "broken-stop.exe");
            File.WriteAllText(brokenStop, "not a Windows executable");
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var definition = new ServiceDefinition(
                "broken-stop", "Broken stop", executable,
                new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
                root, 0, "test",
                StopExecutablePath: brokenStop,
                StopArguments: [],
                ShutdownTimeout: TimeSpan.FromMilliseconds(100));

            using var manager = new ProcessManager();
            var started = await manager.StartAsync(definition);
            Assert.Equal(ServiceState.Running, started.State);

            var stopped = await manager.StopAsync(definition);

            Assert.Equal(ServiceState.Stopped, stopped.State);
            Assert.Null(stopped.ProcessId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StartAsync_ProcessThatExitsImmediately_IsNotReportedRunning()
    {
        using var manager = new ProcessManager();
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var definition = new ServiceDefinition(
            "early-exit", "Early exit", executable,
            new[] { "/d", "/c", "exit 7" },
            Path.GetTempPath(), 0, "test");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.StartAsync(definition));

        Assert.Contains("exited during startup", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ServiceState.Stopped, manager.GetStatus(definition).State);
    }

    [Fact]
    public async Task StartAsync_UnwritableLogPath_DoesNotCrashServiceLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var definition = new ServiceDefinition(
                "bad-log", "Bad log", executable,
                new[] { "-NoProfile", "-NonInteractive", "-Command", "Write-Output ok; Start-Sleep -Seconds 5" },
                root, 0, "test",
                ShutdownTimeout: TimeSpan.FromMilliseconds(100),
                LogPath: root);
            using var manager = new ProcessManager();

            var started = await manager.StartAsync(definition);
            Assert.Equal(ServiceState.Running, started.State);
            await manager.StopAsync(definition);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task StopAsync_CancelledGracefulWait_DoesNotKillManagedService()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-process-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var stopExecutable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var definition = new ServiceDefinition(
                "cancel-stop", "Cancellation stop", executable,
                new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
                root, 0, "test",
                StopExecutablePath: stopExecutable,
                StopArguments: new[] { "/d", "/c", "exit 0" },
                ShutdownTimeout: TimeSpan.FromSeconds(5));

            using var manager = new ProcessManager();
            var started = await manager.StartAsync(definition);
            Assert.Equal(ServiceState.Running, started.State);

            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.StopAsync(definition, cancellation.Token));

            Assert.Equal(ServiceState.Running, manager.GetStatus(definition).State);
            await manager.StopAsync(definition);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void RuntimeLayout_CreatesExpectedConfigFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "devbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            RuntimeLayout.EnsureInitialized(root);

            Assert.True(File.Exists(Path.Combine(root, "config", "nginx", "nginx.conf")));
            Assert.True(File.Exists(Path.Combine(root, "config", "php", "php.ini")));
            Assert.True(File.Exists(Path.Combine(root, "config", "mysql", "my.ini")));
            Assert.True(File.Exists(Path.Combine(root, "www", "index.html")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ServiceDefinition Definition(string executable, int port, params string[] arguments) =>
        new("test", "Test", executable, arguments, Path.GetTempPath(), port, "test");
}
