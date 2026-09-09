using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using DevBox.Core.Services;

namespace DevBox.App.Services;

public interface IDialogService
{
    void Info(string title, string message);
    void Warning(string title, string message);
    void Error(string title, string message);
    bool Confirm(string title, string message);
}

public sealed class DialogService : IDialogService
{
    public void Info(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warning(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
}

public interface IShellService
{
    void Open(string target);
}

public sealed class ShellService : IShellService
{
    public void Open(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}

public interface IHostMappingService
{
    bool Has(string domain);
    Task<bool> EnsureAsync(string domain);
    Task<bool> RemoveAsync(string domain);
}

public sealed class HostMappingService : IHostMappingService
{
    private const string IpAddress = "127.0.0.1";
    private readonly HostsFileManager _hostsFileManager;

    public HostMappingService(HostsFileManager hostsFileManager)
    {
        _hostsFileManager = hostsFileManager ?? throw new ArgumentNullException(nameof(hostsFileManager));
    }

    public bool Has(string domain)
    {
        try
        {
            return _hostsFileManager.HasMapping(IpAddress, domain);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<bool> EnsureAsync(string domain)
    {
        ValidateTestDomain(domain);
        try
        {
            if (_hostsFileManager.HasMapping(IpAddress, domain))
            {
                return true;
            }
            _hostsFileManager.EnsureMapping(IpAddress, domain);
            return _hostsFileManager.HasMapping(IpAddress, domain);
        }
        catch (UnauthorizedAccessException)
        {
            return await RunElevatedAsync("--hosts-ensure", domain, IpAddress) && Has(domain);
        }
    }

    public async Task<bool> RemoveAsync(string domain)
    {
        ValidateTestDomain(domain);
        try
        {
            _hostsFileManager.RemoveMapping(domain);
            return !Has(domain);
        }
        catch (UnauthorizedAccessException)
        {
            return await RunElevatedAsync("--hosts-remove", domain) && !Has(domain);
        }
    }

    private static async Task<bool> RunElevatedAsync(string command, params string[] arguments)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas"
        };
        info.ArgumentList.Add(command);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
    }

    private static void ValidateTestDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        if (!domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Privileged hosts operations are limited to .test domains.", nameof(domain));
        }
    }
}
