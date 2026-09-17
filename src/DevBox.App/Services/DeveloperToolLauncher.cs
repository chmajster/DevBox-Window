using System.ComponentModel;
using System.Diagnostics;

namespace DevBox.App.Services;

public sealed class DeveloperToolLauncher
{
    public void OpenVsCode(string projectRoot)
    {
        var executable = FindFirstExisting(
            "code.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"));

        Start(executable ?? "code", projectRoot, projectRoot);
    }

    public void OpenPhpStorm(string projectRoot)
    {
        var executable = FindPhpStorm() ?? "phpstorm64.exe";
        Start(executable, projectRoot, projectRoot);
    }

    public void OpenTerminal(string projectRoot)
    {
        try
        {
            Start("wt.exe", projectRoot, "-d", projectRoot);
        }
        catch (Win32Exception)
        {
            Start("powershell.exe", projectRoot);
        }
    }

    private static void Start(string executable, string workingDirectory, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException($"Project directory does not exist: {workingDirectory}");

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        _ = Process.Start(info) ?? throw new InvalidOperationException($"Unable to start {Path.GetFileName(executable)}.");
    }

    private static string? FindFirstExisting(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string? FindPhpStorm()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrWhiteSpace(programFiles))
            return null;

        var root = Path.Combine(programFiles, "JetBrains");
        if (!Directory.Exists(root))
            return null;

        try
        {
            return Directory.EnumerateDirectories(root, "PhpStorm *", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => Path.Combine(path, "bin", "phpstorm64.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}