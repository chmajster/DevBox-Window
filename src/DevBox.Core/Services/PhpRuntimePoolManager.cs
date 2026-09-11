using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed partial class PhpRuntimePoolManager : IDisposable
{
    private const string ServiceKeyPrefix = "php-pool-";
    private readonly string _rootPath;
    private readonly ProcessManager _processes = new();
    private readonly object _sync = new();
    private readonly HashSet<string> _knownVersions = new(StringComparer.OrdinalIgnoreCase);
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
        var definition = BuildServiceDefinition(normalized, generateIni: true);
        var snapshot = await _processes.StartAsync(definition, cancellationToken).ConfigureAwait(false);
        if (snapshot.State != ServiceState.Running)
            throw new InvalidOperationException($"PHP {normalized} FastCGI did not reach the running state.");

        lock (_sync)
            _knownVersions.Add(normalized);
        return definition.Port;
    }

    public async Task RestartAsync(string version, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeVersion(version);
        var definition = BuildServiceDefinition(normalized, generateIni: true);
        var snapshot = await _processes.RestartAsync(definition, cancellationToken).ConfigureAwait(false);
        if (snapshot.State != ServiceState.Running)
            throw new InvalidOperationException($"PHP {normalized} FastCGI did not reach the running state after restart.");

        lock (_sync)
            _knownVersions.Add(normalized);
    }

    public async Task StopAsync(string version, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalized = NormalizeVersion(version);
        await StopVersionAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var version in GetKnownVersions())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await StopVersionAsync(version, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                RemoveKnownVersion(version);
            }
        }
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
            throw new InvalidOperationException("PHP runtime directory does not match the requested version.");

        var sharedIni = Path.Combine(_rootPath, "config", "php", "php.ini");
        if (!File.Exists(sharedIni))
            throw new FileNotFoundException("php.ini is missing.", sharedIni);

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

    private async Task StopVersionAsync(string normalized, CancellationToken cancellationToken)
    {
        var definition = BuildServiceDefinition(normalized, generateIni: false);
        await _processes.StopAsync(definition, cancellationToken).ConfigureAwait(false);
        RemoveKnownVersion(normalized);
    }

    private ServiceDefinition BuildServiceDefinition(string normalized, bool generateIni)
    {
        var runtimeDirectory = Path.Combine(_rootPath, "runtime", "php", normalized);
        var executable = Path.Combine(runtimeDirectory, "php-cgi.exe");
        if (generateIni && !File.Exists(executable))
            throw new FileNotFoundException($"PHP runtime {normalized} is not installed or does not contain php-cgi.exe.", executable);

        var phpIni = generateIni
            ? BuildVersionIni(normalized, runtimeDirectory)
            : Path.Combine(_rootPath, "config", "php", "versions", normalized, "php.ini");
        var port = GetPort(normalized);
        return new ServiceDefinition(
            ServiceKey(normalized),
            $"PHP {normalized} FastCGI",
            executable,
            ["-b", $"127.0.0.1:{port}", "-c", phpIni],
            _rootPath,
            port,
            normalized,
            ShutdownTimeout: TimeSpan.FromSeconds(3),
            LogPath: Path.Combine(_rootPath, "logs", "php", $"php-{normalized}-cgi.log"));
    }

    private IReadOnlyList<string> GetKnownVersions()
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
            versions.UnionWith(_knownVersions);

        var markerDirectory = Path.Combine(_rootPath, "tmp", "services");
        if (Directory.Exists(markerDirectory))
        {
            foreach (var marker in Directory.EnumerateFiles(markerDirectory, $"{ServiceKeyPrefix}*.pid", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(marker);
                if (!name.StartsWith(ServiceKeyPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var candidate = name[ServiceKeyPrefix.Length..];
                try
                {
                    versions.Add(NormalizeVersion(candidate));
                }
                catch (ArgumentException)
                {
                }
            }
        }

        return versions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void RemoveKnownVersion(string version)
    {
        lock (_sync)
            _knownVersions.Remove(version);
    }

    private static string ServiceKey(string normalizedVersion) => ServiceKeyPrefix + normalizedVersion;

    private static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content);
            if (File.Exists(path))
                File.Replace(tempPath, path, null);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static string NormalizeVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var normalized = version.Trim();
        if (!VersionRegex().IsMatch(normalized))
            throw new ArgumentException("PHP version must use MAJOR.MINOR.PATCH format.", nameof(version));
        return normalized;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try
        {
            StopAllAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
        }
        _disposed = true;
        _processes.Dispose();
    }

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("(?im)^\\s*extension_dir\\s*=.*$")]
    private static partial Regex ExtensionDirRegex();
}
