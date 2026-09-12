using System.ComponentModel;
using System.Diagnostics;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class ConfigurationFileService
{
    private readonly string _rootPath;
    private readonly string _backupRoot;

    public ConfigurationFileService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _backupRoot = Path.Combine(_rootPath, "backups", "configuration");
    }

    public IReadOnlyList<string> GetKnownConfigurations() => ["nginx", "php", "mysql"];

    public string GetPath(string key) => NormalizeKey(key) switch
    {
        "nginx" => Path.Combine(_rootPath, "config", "nginx", "nginx.conf"),
        "php" => Path.Combine(_rootPath, "config", "php", "php.ini"),
        "mysql" => Path.Combine(_rootPath, "config", "mysql", "my.ini"),
        _ => throw new KeyNotFoundException($"Configuration '{key}' is not supported.")
    };

    public string Read(string key)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Configuration '{key}' does not exist.", path);
        return File.ReadAllText(path);
    }

    public async Task<ConfigurationValidationResult> ValidateAsync(
        string key,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalized = NormalizeKey(key);
        if (content.Length > 2 * 1024 * 1024)
            return new ConfigurationValidationResult(false, normalized, null, "Configuration exceeds the 2 MiB safety limit.");
        if (content.Contains('\0'))
            return new ConfigurationValidationResult(false, normalized, null, "Configuration contains a NUL character.");

        var tempRoot = Path.Combine(_rootPath, "tmp", "config-validation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var extension = normalized == "php" ? ".ini" : ".conf";
            var tempPath = Path.Combine(tempRoot, normalized + extension);
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            (bool Valid, string Message) validation = normalized switch
            {
                "nginx" => await ValidateNginxAsync(tempPath, cancellationToken).ConfigureAwait(false),
                "php" => await ValidatePhpAsync(tempPath, cancellationToken).ConfigureAwait(false),
                "mysql" => await ValidateMySqlAsync(tempPath, cancellationToken).ConfigureAwait(false),
                _ => (false, "Unsupported configuration.")
            };
            return new ConfigurationValidationResult(validation.Valid, normalized, null, validation.Message);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    public async Task<ConfigurationValidationResult> SaveValidatedAsync(
        string key,
        string content,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(key);
        var validation = await ValidateAsync(normalized, content, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
            return validation;

        var path = GetPath(normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string? backupPath = null;
        if (File.Exists(path))
        {
            Directory.CreateDirectory(_backupRoot);
            backupPath = Path.Combine(_backupRoot, $"{normalized}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{Path.GetExtension(path)}.bak");
            File.Copy(path, backupPath, overwrite: false);
        }

        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temp, content, cancellationToken).ConfigureAwait(false);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
        catch
        {
            if (backupPath is not null && File.Exists(backupPath))
                File.Copy(backupPath, path, overwrite: true);
            throw;
        }
        finally
        {
            TryDeleteFile(temp);
        }

        return validation with { BackupPath = backupPath, Message = backupPath is null ? "Configuration validated and saved." : $"Configuration validated and saved. Backup: {backupPath}" };
    }

    public void RestoreBackup(string key, string backupPath)
    {
        var normalized = NormalizeKey(key);
        var source = PathSafety.EnsureUnderRootWithoutReparsePoints(
            _backupRoot,
            backupPath,
            "Configuration backups can only be restored from the DevBox backup directory and cannot traverse a reparse point.");
        if (!File.Exists(source))
            throw new FileNotFoundException("Configuration backup does not exist.", source);
        var fileName = Path.GetFileName(source);
        if (!fileName.StartsWith(normalized + "-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Backup '{fileName}' does not belong to configuration '{normalized}'.");
        var content = File.ReadAllText(source);
        var validation = ValidateAsync(normalized, content).ConfigureAwait(false).GetAwaiter().GetResult();
        if (!validation.IsValid)
            throw new InvalidDataException($"Configuration backup failed validation: {validation.Message}");
        var destination = GetPath(normalized);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + $".{Guid.NewGuid():N}.restore.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(destination))
                File.Replace(temp, destination, null);
            else
                File.Move(temp, destination);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private async Task<(bool Valid, string Message)> ValidateNginxAsync(string configPath, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_rootPath, "runtime", "nginx", "current", "nginx.exe");
        if (!File.Exists(executable))
            return BasicNginxValidation(configPath);
        return await RunValidatorAsync(
            executable,
            ["-t", "-p", _rootPath + Path.DirectorySeparatorChar, "-c", configPath],
            _rootPath,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Valid, string Message)> ValidatePhpAsync(string configPath, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_rootPath, "runtime", "php", "current", "php.exe");
        if (!File.Exists(executable))
            return BasicPhpValidation(configPath);
        return await RunValidatorAsync(executable, ["-c", configPath, "-r", "echo 'ok';"], _rootPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Valid, string Message)> ValidateMySqlAsync(string configPath, CancellationToken cancellationToken)
    {
        var executable = Path.Combine(_rootPath, "runtime", "mysql", "current", "bin", "mysqld.exe");
        if (!File.Exists(executable))
            return BasicMySqlValidation(configPath);
        return await RunValidatorAsync(executable, [$"--defaults-file={configPath}", "--validate-config"], _rootPath, cancellationToken).ConfigureAwait(false);
    }

    private static (bool Valid, string Message) BasicNginxValidation(string path)
    {
        var content = File.ReadAllText(path);
        var balance = 0;
        foreach (var ch in content)
        {
            if (ch == '{') balance++;
            else if (ch == '}') balance--;
            if (balance < 0)
                return (false, "Nginx configuration has an unmatched closing brace.");
        }
        return balance == 0
            ? (true, "Nginx executable is unavailable; structural brace validation passed.")
            : (false, "Nginx configuration has unmatched braces.");
    }

    private static (bool Valid, string Message) BasicPhpValidation(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('['))
                continue;
            if (!line.Contains('='))
                return (false, $"PHP INI contains a non-section directive without '=': {line}");
        }
        return (true, "PHP executable is unavailable; structural INI validation passed.");
    }

    private static (bool Valid, string Message) BasicMySqlValidation(string path)
    {
        var hasSection = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                hasSection = true;
                continue;
            }
            if (!hasSection)
                return (false, "MySQL configuration contains directives before the first section header.");
        }
        return (true, "MySQL executable is unavailable; structural INI validation passed.");
    }

    private static async Task<(bool Valid, string Message)> RunValidatorAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return (false, "Configuration validator could not be started.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (false, $"Configuration validator failed to start: {ex.Message}");
        }

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return (false, "Configuration validator exceeded the 15 second timeout.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        var output = (await stdout.ConfigureAwait(false) + Environment.NewLine + await stderr.ConfigureAwait(false)).Trim();
        if (output.Length > 4000)
            output = output[..4000];
        return process.ExitCode == 0
            ? (true, string.IsNullOrWhiteSpace(output) ? "Configuration validation passed." : output)
            : (false, string.IsNullOrWhiteSpace(output) ? $"Configuration validator exited with code {process.ExitCode}." : output);
    }

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var normalized = key.Trim().ToLowerInvariant();
        return normalized is "nginx" or "php" or "mysql"
            ? normalized
            : throw new KeyNotFoundException($"Configuration '{key}' is not supported.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
