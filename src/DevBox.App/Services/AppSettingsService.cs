using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace DevBox.App.Services;

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool StartServicesAutomatically { get; set; }
}

public interface IAppSettingsService
{
    AppSettings Current { get; }
    void Save();
    void SetStartWithWindows(bool enabled);
}

public sealed class AppSettingsService : IAppSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "DevBoxWindows";
    private readonly string _settingsPath;

    public AppSettingsService(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _settingsPath = Path.Combine(Path.GetFullPath(rootPath), "config", "appsettings.json");
        Current = Load();
        var registryState = TryReadStartupRegistryState();
        if (registryState.HasValue)
        {
            Current.StartWithWindows = registryState.Value;
        }
    }

    public AppSettings Current { get; }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var tempPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(Current, JsonOptions));
            if (File.Exists(_settingsPath))
            {
                File.Replace(tempPath, _settingsPath, null);
            }
            else
            {
                File.Move(tempPath, _settingsPath);
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

    public void SetStartWithWindows(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (enabled && string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("Unable to determine the DevBox executable path.");
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current-user startup registry key.");
        if (enabled)
        {
            key.SetValue(RunValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }

        Current.StartWithWindows = enabled;
        Save();
    }

    private bool? TryReadStartupRegistryState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var configuredCommand = key?.GetValue(RunValueName) as string;
            if (string.IsNullOrWhiteSpace(configuredCommand))
            {
                return false;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return true;
            }

            return configuredCommand.Contains(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
