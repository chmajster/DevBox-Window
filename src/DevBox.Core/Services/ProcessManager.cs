using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ProcessManager : IProcessManager
{
    private static readonly TimeSpan ProcessIdentityTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProcessOnlyStartupDelay = TimeSpan.FromSeconds(1);
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
            using var processLock = await CrossProcessFileLock.AcquireAsync(ProcessLockPath(definition), cancellationToken).ConfigureAwait(false);
            if (_processes.TryGetValue(definition.Key, out var existing) && !existing.Process.HasExited)
                return Snapshot(existing, ServiceState.Running);

            RemoveStale(definition.Key);
            var adopted = TryAdopt(definition);
            if (adopted is not null)
            {
                _processes[definition.Key] = adopted;
                return Snapshot(adopted, ServiceState.Running);
            }

            if (!File.Exists(definition.ExecutablePath))
                throw new FileNotFoundException($"Runtime for {definition.DisplayName} was not found.", definition.ExecutablePath);

            if (definition.Port > 0 && !IsPortAvailable(definition.Port))
                throw new InvalidOperationException($"Port {definition.Port} required by {definition.DisplayName} is already in use.");

            Directory.CreateDirectory(definition.WorkingDirectory);
            EnsureLogDirectory(definition.LogPath);
            await PrepareServiceAsync(definition, cancellationToken).ConfigureAwait(false);

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

            try
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException($"Windows refused to start {definition.DisplayName}.");
                }
            }
            catch (Win32Exception ex)
            {
                process.Dispose();
                throw new InvalidOperationException($"Unable to start {definition.DisplayName}: {ex.Message}", ex);
            }

            managed.StartedAt = TryGetStartTime(process) ?? DateTimeOffset.UtcNow;
            try
            {
                WritePidMarker(managed);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                _processes[definition.Key] = managed;
                AppendLog(managed, "APP", $"Started PID {process.Id}; validating startup.");
                await VerifyStartupAsync(managed, cancellationToken).ConfigureAwait(false);
                AppendLog(managed, "APP", definition.Port > 0
                    ? $"Startup validated on port {definition.Port}."
                    : "Startup validated.");
            }
            catch
            {
                _processes.TryRemove(definition.Key, out _);
                var processId = TryGetProcessId(process);
                if (processId.HasValue)
                    DeletePidMarker(definition, processId.Value);
                TryTerminateStartedProcess(process);
                process.Dispose();
                throw;
            }

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
            using var processLock = await CrossProcessFileLock.AcquireAsync(ProcessLockPath(definition), cancellationToken).ConfigureAwait(false);
            ManagedProcess? managed = null;
            if (_processes.TryGetValue(definition.Key, out var existing) && !existing.Process.HasExited)
            {
                managed = existing;
            }
            else
            {
                RemoveStale(definition.Key);
                managed = TryAdopt(definition);
                if (managed is not null)
                    _processes[definition.Key] = managed;
            }

            if (managed is null)
                return Stopped(definition);

            var timeout = definition.ShutdownTimeout ?? TimeSpan.FromSeconds(5);
                var gracefulRequested = false;
                if (!string.IsNullOrWhiteSpace(definition.StopExecutablePath) && File.Exists(definition.StopExecutablePath))
                {
                    gracefulRequested = await ExecuteStopCommandAsync(definition, timeout, cancellationToken).ConfigureAwait(false);
                    if (!gracefulRequested)
                        AppendLog(managed, "APP", "Graceful stop command failed; falling back to managed process termination.");
                }
                else
                {
                    gracefulRequested = managed.Process.CloseMainWindow();
                }

                if (gracefulRequested && !managed.Process.HasExited)
                    await WaitForExitAsync(managed.Process, timeout, cancellationToken).ConfigureAwait(false);

                if (!managed.Process.HasExited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendLog(managed, "APP", "Graceful shutdown was unavailable or timed out; killing managed process tree.");
                    managed.Process.Kill(entireProcessTree: true);
                    await managed.Process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            AppendLog(managed, "APP", "Stopped.");
            CleanupStoppedProcess(definition, managed);
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
            var adopted = TryAdopt(definition);
            if (adopted is null)
                return Stopped(definition);

            managed = _processes.GetOrAdd(definition.Key, adopted);
            if (!ReferenceEquals(managed, adopted))
                adopted.Process.Dispose();
        }

        if (managed.Process.HasExited)
        {
            var exitCode = managed.Process.ExitCode;
            var processId = managed.Process.Id;
            var error = managed.LastError ?? (exitCode == 0 ? null : $"Process exited with code {exitCode}.");
            TimeSpan? uptime = managed.StartedAt is null ? null : DateTimeOffset.UtcNow - managed.StartedAt.Value;
            _processes.TryRemove(definition.Key, out _);
            DeletePidMarker(definition, processId);
            managed.Process.Dispose();
            return new ServiceSnapshot(
                definition.Key, definition.DisplayName,
                exitCode == 0 ? ServiceState.Stopped : ServiceState.Error,
                null, definition.Port, definition.Version,
                uptime,
                error);
        }

        return Snapshot(managed, ServiceState.Running);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var managed in _processes.Values)
            managed.Process.Dispose();
        foreach (var gate in _locks.Values)
            gate.Dispose();
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
            info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<bool> ExecuteStopCommandAsync(ServiceDefinition definition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopInfo = BuildStartInfo(
            definition.StopExecutablePath!,
            definition.StopArguments ?? Array.Empty<string>(),
            definition.WorkingDirectory);

        using var stopProcess = new Process { StartInfo = stopInfo };
        try
        {
            if (!stopProcess.Start())
                return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        var outputTask = stopProcess.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = stopProcess.StandardError.ReadToEndAsync(cancellationToken);
        bool exited;
        try
        {
            exited = await WaitForExitAsync(stopProcess, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateStartedProcess(stopProcess);
            throw;
        }
        if (!exited)
        {
            TryTerminateStartedProcess(stopProcess);
            _ = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return false;
        }

        _ = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        return stopProcess.ExitCode == 0;
    }

    internal static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (process.HasExited)
            return true;

        var exitTask = process.WaitForExitAsync(cancellationToken);
        var delayTask = Task.Delay(timeout, CancellationToken.None);
        var completed = await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (completed != exitTask)
            return false;

        await exitTask.ConfigureAwait(false);
        return process.HasExited;
    }

    private static async Task VerifyStartupAsync(ManagedProcess managed, CancellationToken cancellationToken)
    {
        if (managed.Definition.Port <= 0)
        {
            await Task.Delay(ProcessOnlyStartupDelay, cancellationToken).ConfigureAwait(false);
            ThrowIfExitedDuringStartup(managed);
            return;
        }

        var deadline = DateTimeOffset.UtcNow + StartupProbeTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfExitedDuringStartup(managed);
            if (!IsPortAvailable(managed.Definition.Port))
                return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfExitedDuringStartup(managed);
        throw new InvalidOperationException(
            $"{managed.Definition.DisplayName} started but did not begin listening on port {managed.Definition.Port} within {StartupProbeTimeout.TotalSeconds:0} seconds.");
    }

    private static void ThrowIfExitedDuringStartup(ManagedProcess managed)
    {
        if (!managed.Process.HasExited)
            return;
        var exitCode = managed.Process.ExitCode;
        throw new InvalidOperationException(
            $"{managed.Definition.DisplayName} exited during startup with code {exitCode}. Check {managed.Definition.LogPath ?? "the service log"} for details.");
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
        TimeSpan? uptime = managed.StartedAt is null
            ? null
            : DateTimeOffset.UtcNow - managed.StartedAt.Value;

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

    private void CleanupStoppedProcess(ServiceDefinition definition, ManagedProcess managed)
    {
        _processes.TryRemove(definition.Key, out _);
        var processId = TryGetProcessId(managed.Process);
        if (processId.HasValue)
            DeletePidMarker(definition, processId.Value);
        managed.Process.Dispose();
    }

    private void RemoveStale(string key)
    {
        if (_processes.TryRemove(key, out var stale))
        {
            TryDeletePidMarkerForProcess(stale);
            stale.Process.Dispose();
        }
    }

    private ManagedProcess? TryAdopt(ServiceDefinition definition)
    {
        var marker = ReadPidMarker(definition);
        if (marker is null)
            return null;

        Process? process = null;
        try
        {
            process = Process.GetProcessById(marker.Value.ProcessId);
            if (process.HasExited)
            {
                process.Dispose();
                DeletePidMarker(definition, marker.Value.ProcessId);
                return null;
            }

            string? actualExecutable;
            try
            {
                actualExecutable = process.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                process.Dispose();
                DeletePidMarker(definition, marker.Value.ProcessId);
                return null;
            }

            var actualStartTime = TryGetStartTime(process);
            if (string.IsNullOrWhiteSpace(actualExecutable) ||
                actualStartTime is null ||
                !Path.GetFullPath(actualExecutable).Equals(marker.Value.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(actualExecutable).Equals(Path.GetFullPath(definition.ExecutablePath), StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(actualStartTime.Value.UtcDateTime.Ticks - marker.Value.StartTimeUtcTicks) > ProcessIdentityTolerance.Ticks)
            {
                process.Dispose();
                DeletePidMarker(definition, marker.Value.ProcessId);
                return null;
            }

            process.EnableRaisingEvents = true;
            var managed = new ManagedProcess(process, definition)
            {
                StartedAt = actualStartTime
            };
            process.Exited += (_, _) => OnExited(managed);
            AppendLog(managed, "APP", $"Adopted existing PID {process.Id} from a previous DevBox session.");
            return managed;
        }
        catch (ArgumentException)
        {
            process?.Dispose();
            DeletePidMarker(definition, marker.Value.ProcessId);
            return null;
        }
        catch (InvalidOperationException)
        {
            process?.Dispose();
            DeletePidMarker(definition, marker.Value.ProcessId);
            return null;
        }
    }

    private static DateTimeOffset? TryGetStartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string PidMarkerPath(ServiceDefinition definition) =>
        Path.Combine(definition.WorkingDirectory, "tmp", "services", $"{definition.Key}.pid");

    private static string ProcessLockPath(ServiceDefinition definition) =>
        Path.Combine(definition.WorkingDirectory, "tmp", "services", $"{definition.Key}.lock");

    private static void WritePidMarker(ManagedProcess managed)
    {
        if (managed.StartedAt is null)
            throw new InvalidOperationException("Managed process start time is unavailable.");

        var path = PidMarkerPath(managed.Definition);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var executable = Path.GetFullPath(managed.Definition.ExecutablePath);
        var tempPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllLines(tempPath,
            [
                managed.Process.Id.ToString(CultureInfo.InvariantCulture),
                executable,
                managed.StartedAt.Value.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture)
            ]);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static (int ProcessId, string ExecutablePath, long StartTimeUtcTicks)? ReadPidMarker(ServiceDefinition definition)
    {
        var path = PidMarkerPath(definition);
        if (!File.Exists(path))
            return null;

        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length < 3 ||
                !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var processId) ||
                processId <= 0 ||
                !long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var startTimeUtcTicks) ||
                startTimeUtcTicks <= 0)
            {
                File.Delete(path);
                return null;
            }

            var executable = Path.GetFullPath(lines[1].Trim());
            return (processId, executable, startTimeUtcTicks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void DeletePidMarker(ServiceDefinition definition, int expectedProcessId)
    {
        var path = PidMarkerPath(definition);
        if (!File.Exists(path))
            return;

        try
        {
            var marker = ReadPidMarker(definition);
            if (marker is null || marker.Value.ProcessId == expectedProcessId)
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeletePidMarkerForProcess(ManagedProcess managed)
    {
        try
        {
            DeletePidMarker(managed.Definition, managed.Process.Id);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryTerminateStartedProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static async Task PrepareServiceAsync(ServiceDefinition definition, CancellationToken cancellationToken)
    {
        if (!definition.Key.Equals("mysql", StringComparison.OrdinalIgnoreCase))
            return;

        var dataDirectory = Path.Combine(definition.WorkingDirectory, "data", "mysql");
        Directory.CreateDirectory(dataDirectory);
        if (IsMySqlDataDirectoryInitialized(dataDirectory))
            return;

        if (Directory.EnumerateFileSystemEntries(dataDirectory).Any())
        {
            throw new InvalidOperationException(
                $"MySQL data directory '{dataDirectory}' is non-empty but does not contain initialized system tables. " +
                "Move or repair the partial data directory before starting MySQL.");
        }

        var initializationArguments = definition.Arguments.Concat(["--initialize-insecure"]).ToArray();
        var info = BuildStartInfo(definition.ExecutablePath, initializationArguments, definition.WorkingDirectory);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Unable to start MySQL initialization.");
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Unable to start MySQL initialization: {ex.Message}", ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminateStartedProcess(process);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(error) ? output : error;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details)
                ? $"MySQL initialization failed with exit code {process.ExitCode}."
                : $"MySQL initialization failed: {details.Trim()}");
        }

        if (!IsMySqlDataDirectoryInitialized(dataDirectory))
            throw new InvalidOperationException("MySQL initialization completed without creating the expected system database.");
    }

    internal static bool IsMySqlDataDirectoryInitialized(string dataDirectory) =>
        Directory.Exists(Path.Combine(dataDirectory, "mysql"));

    private static void EnsureLogDirectory(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        var directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void AppendLog(ManagedProcess managed, string stream, string? message)
    {
        if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(managed.Definition.LogPath))
            return;

        try
        {
            lock (managed.Gate)
            {
                File.AppendAllText(
                    managed.Definition.LogPath,
                    $"{DateTimeOffset.Now:O} [{stream}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
        }
    }

    private static void OnExited(ManagedProcess managed)
    {
        try
        {
            var exitCode = managed.Process.ExitCode;
            if (exitCode != 0)
                managed.LastError = $"Process exited with code {exitCode}.";
            AppendLog(managed, "APP", $"Exited with code {exitCode}.");
            DeletePidMarker(managed.Definition, managed.Process.Id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
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
