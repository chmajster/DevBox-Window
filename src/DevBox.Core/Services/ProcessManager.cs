using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProcessManager : IProcessManager
{
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public async Task<ServiceSnapshot> StartAsync(ServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _locks.GetOrAdd(definition.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_processes.TryGetValue(definition.Key, out var existing) && !existing.Process.HasExited)
            {
                return Snapshot(existing, ServiceState.Running);
            }

            RemoveStale(definition.Key);

            if (!File.Exists(definition.ExecutablePath))
            {
                throw new FileNotFoundException($"Runtime for {definition.DisplayName} was not found.", definition.ExecutablePath);
            }

            if (definition.Port > 0 && !IsPortAvailable(definition.Port))
            {
                throw new InvalidOperationException($"Port {definition.Port} required by {definition.DisplayName} is already in use.");
            }

            Directory.CreateDirectory(definition.WorkingDirectory);
            EnsureLogDirectory(definition.LogPath);

            var startInfo = BuildStartInfo(definition.ExecutablePath, definition.Arguments, definition.WorkingDirectory);
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            var managed = new ManagedProcess(process, definition);
            process.OutputDataReceived += (_, e) => AppendLog(managed, "OUT", e.Data);
            process.ErrorDataReceived += (_, e) => AppendLog(managed, "ERR", e.Data);
            process.Exited += (_, _) => OnExited(managed);

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"Windows refused to start {definition.DisplayName}.");
            }

            managed.StartedAt = DateTimeOffset.UtcNow;
            _processes[definition.Key] = managed;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            AppendLog(managed, "APP", $"Started PID {process.Id}.");

            return Snapshot(managed, ServiceState.Running);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ServiceSnapshot> StopAsync(ServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var gate = _locks.GetOrAdd(definition.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_processes.TryGetValue(definition.Key, out var managed) || managed.Process.HasExited)
            {
                RemoveStale(definition.Key);
                return Stopped(definition);
            }

            var timeout = definition.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
            var gracefulRequested = false;
            if (!string.IsNullOrWhiteSpace(definition.StopExecutablePath) &&
                File.Exists(definition.StopExecutablePath))
            {
                await ExecuteStopCommandAsync(definition, timeout, cancellationToken).ConfigureAwait(false);
                gracefulRequested = true;
            }
            else
            {
                gracefulRequested = managed.Process.CloseMainWindow();
            }

            if (gracefulRequested && !managed.Process.HasExited)
            {
                await WaitForExitAsync(managed.Process, timeout, cancellationToken).ConfigureAwait(false);
            }

            if (!managed.Process.HasExited)
            {
                AppendLog(managed, "APP", "Graceful shutdown was unavailable or timed out; killing managed process tree.");
                managed.Process.Kill(entireProcessTree: true);
                await managed.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            AppendLog(managed, "APP", "Stopped.");
            _processes.TryRemove(definition.Key, out _);
            managed.Process.Dispose();
            return Stopped(definition);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ServiceSnapshot> RestartAsync(ServiceDefinition definition, CancellationToken cancellationToken = default)
    {
        await StopAsync(definition, cancellationToken).ConfigureAwait(false);
        return await StartAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    public ServiceSnapshot GetStatus(ServiceDefinition definition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_processes.TryGetValue(definition.Key, out var managed))
        {
            return Stopped(definition);
        }

        if (managed.Process.HasExited)
        {
            var exitCode = managed.Process.ExitCode;
            var error = managed.LastError ?? (exitCode == 0 ? null : $"Process exited with code {exitCode}.");
            return new ServiceSnapshot(
                definition.Key, definition.DisplayName,
                exitCode == 0 ? ServiceState.Stopped : ServiceState.Error,
                managed.Process.Id, definition.Port, definition.Version,
                managed.StartedAt is null ? null : DateTimeOffset.UtcNow - managed.StartedAt.Value,
                error);
        }

        return Snapshot(managed, ServiceState.Running);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var managed in _processes.Values)
        {
            managed.Process.Dispose();
        }
        foreach (var gate in _locks.Values)
        {
            gate.Dispose();
        }
        _processes.Clear();
        _locks.Clear();
    }

    private static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return info;
    }

    private static async Task ExecuteStopCommandAsync(ServiceDefinition definition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopInfo = BuildStartInfo(
            definition.StopExecutablePath!,
            definition.StopArguments ?? Array.Empty<string>(),
            definition.WorkingDirectory);

        stopInfo.RedirectStandardOutput = false;
        stopInfo.RedirectStandardError = false;
        using var stopProcess = new Process { StartInfo = stopInfo };
        if (!stopProcess.Start())
        {
            return;
        }

        await WaitForExitAsync(stopProcess, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        return completed == exitTask && process.HasExited;
    }

    private static bool IsPortAvailable(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static ServiceSnapshot Snapshot(ManagedProcess managed, ServiceState state)
    {
        var uptime = managed.StartedAt is null ? null : DateTimeOffset.UtcNow - managed.StartedAt.Value;
        return new ServiceSnapshot(
            managed.Definition.Key,
            managed.Definition.DisplayName,
            state,
            managed.Process.HasExited ? null : managed.Process.Id,
            managed.Definition.Port,
            managed.Definition.Version,
            uptime,
            managed.LastError);
    }

    private static ServiceSnapshot Stopped(ServiceDefinition definition) =>
        new(definition.Key, definition.DisplayName, ServiceState.Stopped, null, definition.Port, definition.Version, null, null);

    private void RemoveStale(string key)
    {
        if (_processes.TryRemove(key, out var stale))
        {
            stale.Process.Dispose();
        }
    }

    private static void EnsureLogDirectory(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void AppendLog(ManagedProcess managed, string stream, string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(managed.Definition.LogPath))
        {
            return;
        }

        lock (managed.Gate)
        {
            File.AppendAllText(
                managed.Definition.LogPath,
                $"{DateTimeOffset.Now:O} [{stream}] {message}{Environment.NewLine}");
        }
    }

    private static void OnExited(ManagedProcess managed)
    {
        try
        {
            var exitCode = managed.Process.ExitCode;
            if (exitCode != 0)
            {
                managed.LastError = $"Process exited with code {exitCode}.";
            }
            AppendLog(managed, "APP", $"Exited with code {exitCode}.");
        }
        catch (InvalidOperationException)
        {
            managed.LastError = "Process exited unexpectedly.";
        }
    }

    private sealed class ManagedProcess
    {
        public ManagedProcess(Process process, ServiceDefinition definition)
        {
            Process = process;
            Definition = definition;
        }

        public Process Process { get; }
        public ServiceDefinition Definition { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public string? LastError { get; set; }
        public object Gate { get; } = new();
    }
}
