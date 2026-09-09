using System.Diagnostics;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class PhpExtensionInspector
{
    private readonly string _rootPath;
    private readonly string _phpExecutable;
    private readonly string _phpIniPath;

    public PhpExtensionInspector(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _phpExecutable = Path.Combine(_rootPath, "runtime", "php", "current", "php.exe");
        _phpIniPath = Path.Combine(_rootPath, "config", "php", "php.ini");
    }

    public async Task<PhpExtensionCheckResult> CheckAsync(
        IReadOnlyCollection<string> requiredExtensions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredExtensions);
        var required = requiredExtensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (!File.Exists(_phpExecutable))
        {
            return new PhpExtensionCheckResult(false, Array.Empty<string>(), required, "PHP CLI runtime is not installed.");
        }

        var startInfo = new ProcessStartInfo(_phpExecutable)
        {
            WorkingDirectory = _rootPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(_phpIniPath);
        startInfo.ArgumentList.Add("-m");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return new PhpExtensionCheckResult(true, Array.Empty<string>(), required, "Unable to start PHP CLI.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new PhpExtensionCheckResult(true, Array.Empty<string>(), required, "PHP extension check timed out.");
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var loaded = ParseModules(output);
        var missing = required.Where(extension => !loaded.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToArray();

        return process.ExitCode == 0
            ? new PhpExtensionCheckResult(true, loaded, missing, null)
            : new PhpExtensionCheckResult(true, loaded, missing, string.IsNullOrWhiteSpace(error) ? $"PHP exited with code {process.ExitCode}." : error.Trim());
    }

    public bool EnsureConfigured(IReadOnlyCollection<string> requiredExtensions)
    {
        ArgumentNullException.ThrowIfNull(requiredExtensions);
        if (!File.Exists(_phpIniPath))
        {
            throw new FileNotFoundException("php.ini was not found.", _phpIniPath);
        }

        var lines = File.ReadAllLines(_phpIniPath).ToList();
        var configured = lines
            .Select(TryParseConfiguredExtension)
            .Where(value => value is not null)
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var extension in requiredExtensions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (extension.Equals("json", StringComparison.OrdinalIgnoreCase) || configured.Contains(extension))
            {
                continue;
            }

            lines.Add($"extension={extension}");
            configured.Add(extension);
            changed = true;
        }

        if (changed)
        {
            AtomicWrite(_phpIniPath, lines);
        }

        return changed;
    }

    internal static IReadOnlyList<string> ParseModules(string output) =>
        output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('[') && !line.EndsWith(']'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string? TryParseConfiguredExtension(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith(';') || !trimmed.StartsWith("extension", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var equalsIndex = trimmed.IndexOf('=');
        if (equalsIndex < 0)
        {
            return null;
        }

        var value = trimmed[(equalsIndex + 1)..].Trim().Trim('"', '\'');
        if (value.StartsWith("php_", StringComparison.OrdinalIgnoreCase))
        {
            value = value[4..];
        }
        if (value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        return value;
    }

    private static void AtomicWrite(string path, IReadOnlyCollection<string> lines)
    {
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllLines(tempPath, lines);
            File.Replace(tempPath, path, null);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
