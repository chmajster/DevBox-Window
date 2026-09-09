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

        var phpIni = BuildVersionIni(normalized, runtimeDirectory);

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

        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryStop(process);
            process.Dispose();
            throw;
        }

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

    public async Task RestartAsync(string version, CancellationToken cancellationToken = default)
    {
        await StopAsync(version, cancellationToken).ConfigureAwait(false);
        _ = await EnsureRunningAsync(version, cancellationToken).ConfigureAwait(false);
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

    internal string BuildVersionIni(string version, string runtimeDirectory)
    {
        var normalized = NormalizeVersion(version);
        var fullRuntimeDirectory = Path.GetFullPath(runtimeDirectory);
        var expectedRuntimeDirectory = Path.GetFullPath(Path.Combine(_rootPath, "runtime", "php", normalized));
        if (!fullRuntimeDirectory.Equals(expectedRuntimeDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("PHP runtime directory does not match the requested version.");
        }

        var sharedIni = Path.Combine(_rootPath, "config", "php", "php.ini");
        if (!File.Exists(sharedIni))
        {
            throw new FileNotFoundException("php.ini is missing.", sharedIni);
        }

        var content = File.ReadAllText(sharedIni);
        var extensionDirectory = Path.Combine(fullRuntimeDirectory, "ext").Replace('\\', '/');
        var replacement = $"extension_dir=\"{extensionDirectory}\"";
        var rewritten = ExtensionDirRegex().IsMatch(content)
            ? ExtensionDirRegex().Replace(content, replacement, 1)
            : $"{replacement}{Environment.NewLine}{content}";

        var versionConfigDirectory = Path.Combine(_rootPath, "config", "php", "versions", normalized);
        Directory.CreateDirectory(versionConfigDirectory);
        var versionIni = Path.Combine(versionConfigDirectory, "php.ini");
        AtomicWrite(versionIni, rewritten);
        return versionIni;
    }

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
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

    [GeneratedRegex("(?im)^\\s*extension_dir\\s*=.*$")]
    private static partial Regex ExtensionDirRegex();
}
