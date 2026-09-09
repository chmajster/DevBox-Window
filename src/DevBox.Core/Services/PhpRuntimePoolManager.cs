using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevBox.Core.Services;

public sealed partial class PhpRuntimePoolManager : IDisposable
{
    private readonly string _rootPath;
    private readonly object _sync = new();
    private readonly Dictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public PhpRuntimePoolManager(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
    }

    public async Task<int> EnsureRunningAsync(string version, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeVersion(version);
        var port = GetPort(normalized);

        lock (_sync)
        {
            if (_processes.TryGetValue(normalized, out var existing))
            {
                if (!existing.HasExited)
                {
                    return port;
                }
                existing.Dispose();
                _processes.Remove(normalized);
            }
        }

        var runtimeDirectory = Path.Combine(_rootPath, "runtime", "php", normalized);
        var executable = Path.Combine(runtimeDirectory, "php-cgi.exe");
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException($"PHP runtime {normalized} is not installed or does not contain php-cgi.exe.", executable);
        }

        var phpIni = Path.Combine(_rootPath, "config", "php", "php.ini");
        if (!File.Exists(phpIni))
        {
            throw new FileNotFoundException("php.ini is missing.", phpIni);
        }

        if (!IsPortAvailable(port))
        {
            throw new InvalidOperationException($"FastCGI port {port} for PHP {normalized} is already in use.");
        }

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _rootPath
        };
        startInfo.ArgumentList.Add("-b");
        startInfo.ArgumentList.Add($"127.0.0.1:{port}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(phpIni);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Unable to start PHP {normalized} FastCGI.");
        }

        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            process.Dispose();
            throw new InvalidOperationException($"PHP {normalized} FastCGI exited during startup with code {exitCode}.");
        }

        lock (_sync)
        {
            if (_disposed)
            {
                TryStop(process);
                process.Dispose();
                throw new ObjectDisposedException(nameof(PhpRuntimePoolManager));
            }
            _processes[normalized] = process;
        }
        return port;
    }

    public Task StopAsync(string version, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeVersion(version);
        Process? process = null;
        lock (_sync)
        {
            if (_processes.Remove(normalized, out var found))
            {
                process = found;
            }
        }
        if (process is not null)
        {
            TryStop(process);
            process.Dispose();
        }
        return Task.CompletedTask;
    }

    public Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process[] processes;
        lock (_sync)
        {
            processes = _processes.Values.ToArray();
            _processes.Clear();
        }
        foreach (var process in processes)
        {
            TryStop(process);
            process.Dispose();
        }
        return Task.CompletedTask;
    }

    public static int GetPort(string version)
    {
        var normalized = NormalizeVersion(version);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        var value = BinaryPrimitives.ReadUInt32LittleEndian(hash);
        return 20000 + (int)(value % 30000);
    }

    private static string NormalizeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var normalized = version.Trim();
        if (!VersionRegex().IsMatch(normalized))
        {
            throw new ArgumentException("PHP version must use MAJOR.MINOR.PATCH format.", nameof(version));
        }
        return normalized;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _ = StopAllAsync();
    }

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
}
