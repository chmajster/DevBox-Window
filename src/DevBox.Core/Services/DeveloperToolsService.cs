using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class DeveloperToolsService : IDisposable
{
    private readonly string _rootPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public DeveloperToolsService(string rootPath, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DevBox-Windows", "0.2.0"));
        }
    }

    public async Task<IReadOnlyList<DeveloperToolStatus>> GetStatusesAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var nodeDirectory = Path.Combine(programFiles, "nodejs");
        var npmGlobalDirectory = Path.Combine(roamingAppData, "npm");

        var composer = ResolveCommand([
            Path.Combine(_rootPath, "tools", "composer", "composer.cmd"),
            "composer.cmd",
            "composer.bat",
            "composer.exe"
        ]);
        var node = ResolveCommand([
            Path.Combine(nodeDirectory, "node.exe"),
            "node.exe"
        ]);
        var npm = ResolveCommand([
            Path.Combine(nodeDirectory, "npm.cmd"),
            Path.Combine(nodeDirectory, "npm.exe"),
            "npm.cmd",
            "npm.exe"
        ]);
        var pnpm = ResolveCommand([
            Path.Combine(npmGlobalDirectory, "pnpm.cmd"),
            Path.Combine(npmGlobalDirectory, "pnpm.exe"),
            "pnpm.cmd",
            "pnpm.exe"
        ]);

        return
        [
            await StatusAsync("composer", "Composer", composer, ["--version"], "Verified Composer installer", cancellationToken).ConfigureAwait(false),
            await StatusAsync("node", "Node.js", node, ["--version"], "winget OpenJS.NodeJS.LTS", cancellationToken).ConfigureAwait(false),
            await StatusAsync("npm", "npm", npm, ["--version"], "Installed with Node.js", cancellationToken).ConfigureAwait(false),
            await StatusAsync("pnpm", "pnpm", pnpm, ["--version"], "npm --global pnpm", cancellationToken).ConfigureAwait(false)
        ];
    }

    public async Task InstallNodeLtsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var winget = ResolveCommand(["winget.exe"])
            ?? throw new FileNotFoundException("Windows Package Manager (winget) is not available.");

        await RunCheckedAsync(
            winget,
            ["install", "--id", "OpenJS.NodeJS.LTS", "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements"],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallPnpmAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var nodeDirectory = Path.Combine(programFiles, "nodejs");
        var npm = ResolveCommand([
            Path.Combine(nodeDirectory, "npm.cmd"),
            Path.Combine(nodeDirectory, "npm.exe"),
            "npm.cmd",
            "npm.exe"
        ]) ?? throw new FileNotFoundException("npm is not available. Install Node.js first.");

        await RunCheckedAsync(npm, ["install", "--global", "pnpm"], cancellationToken).ConfigureAwait(false);
    }

    public async Task InstallComposerAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var php = Path.Combine(_rootPath, "runtime", "php", "current", "php.exe");
        if (!File.Exists(php))
        {
            throw new FileNotFoundException("Active PHP runtime with php.exe is required to install Composer.", php);
        }

        const string installerUrl = "https://getcomposer.org/installer";
        const string signatureUrl = "https://composer.github.io/installer.sig";
        var tempRoot = Path.Combine(_rootPath, "tmp", "composer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var installerPath = Path.Combine(tempRoot, "composer-setup.php");

        try
        {
            var expectedSignature = (await _httpClient.GetStringAsync(signatureUrl, cancellationToken).ConfigureAwait(false)).Trim();
            if (expectedSignature.Length != 96 || expectedSignature.Any(ch => !Uri.IsHexDigit(ch)))
            {
                throw new InvalidDataException("Composer installer signature has an invalid format.");
            }

            using (var response = await _httpClient.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            await using (var stream = File.OpenRead(installerPath))
            {
                var actual = SHA384.HashData(stream);
                var expected = Convert.FromHexString(expectedSignature);
                if (!CryptographicOperations.FixedTimeEquals(actual, expected))
                {
                    throw new InvalidDataException("Composer installer SHA-384 verification failed.");
                }
            }

            var installDir = Path.Combine(_rootPath, "tools", "composer");
            Directory.CreateDirectory(installDir);
            await RunCheckedAsync(
                php,
                [installerPath, $"--install-dir={installDir}", "--filename=composer.phar", "--quiet"],
                cancellationToken).ConfigureAwait(false);

            var wrapper = Path.Combine(installDir, "composer.cmd");
            File.WriteAllText(wrapper, "@echo off\r\n\"%~dp0..\\..\\runtime\\php\\current\\php.exe\" \"%~dp0composer.phar\" %*\r\n");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    internal static string? ResolveCommand(IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var candidate in candidates.Where(candidate => !Path.IsPathFullyQualified(candidate)))
        {
            foreach (var directory in pathEntries)
            {
                var path = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(path))
                {
                    return Path.GetFullPath(path);
                }
            }
        }
        return null;
    }

    private static async Task<DeveloperToolStatus> StatusAsync(
        string key,
        string displayName,
        string? executable,
        IReadOnlyList<string> versionArguments,
        string installMethod,
        CancellationToken cancellationToken)
    {
        if (executable is null)
        {
            return new DeveloperToolStatus(key, displayName, false, null, null, installMethod);
        }

        try
        {
            var version = (await RunCaptureAsync(executable, versionArguments, cancellationToken).ConfigureAwait(false)).Trim();
            return new DeveloperToolStatus(key, displayName, true, version, executable, installMethod);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new DeveloperToolStatus(key, displayName, true, "Version check failed", executable, installMethod);
        }
    }

    private static async Task RunCheckedAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _ = await RunCaptureAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunCaptureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(executable, arguments);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
        }

        var outputTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardOutput, cancellationToken: cancellationToken);
        var errorTask = ProcessOutputCapture.ReadBoundedAsync(process.StandardError, cancellationToken: cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"{Path.GetFileName(executable)} exited with code {process.ExitCode}."
                : error.Trim());
        }
        return string.IsNullOrWhiteSpace(output) ? error : output;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(executable);
        var isCommandScript = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                              extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo(isCommandScript ? ResolveCommandProcessor() : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (isCommandScript)
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(BuildCmdCommand(executable, arguments));
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        return startInfo;
    }

    private static string ResolveCommandProcessor()
    {
        var comSpec = Environment.GetEnvironmentVariable("ComSpec");
        return string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
    }

    private static string BuildCmdCommand(string executable, IReadOnlyList<string> arguments)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return string.Join(' ', new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
