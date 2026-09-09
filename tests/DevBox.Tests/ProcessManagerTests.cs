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
