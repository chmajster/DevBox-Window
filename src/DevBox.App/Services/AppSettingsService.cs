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
        var registryState = TryReadAndRepairStartupRegistryState();
        if (registryState.HasValue)
            Current.StartWithWindows = registryState.Value;
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
                File.Replace(tempPath, _settingsPath, null);
            else
                File.Move(tempPath, _settingsPath);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (enabled && string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Unable to determine the DevBox executable path.");

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Unable to open the current-user startup registry key.");
        var previousValue = key.GetValue(RunValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var previousKind = previousValue is null ? (RegistryValueKind?)null : key.GetValueKind(RunValueName);
        var previousState = Current.StartWithWindows;

        try
        {
            if (enabled)
                key.SetValue(RunValueName, BuildStartupCommand(executable!), RegistryValueKind.String);
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);

            Current.StartWithWindows = enabled;
            Save();
        }
        catch (Exception original)
        {
            Current.StartWithWindows = previousState;
            try
            {
                if (previousValue is null)
                    key.DeleteValue(RunValueName, throwOnMissingValue: false);
                else
                    key.SetValue(RunValueName, previousValue, previousKind ?? RegistryValueKind.String);
            }
            catch (Exception rollbackError) when (rollbackError is UnauthorizedAccessException or SecurityException or IOException)
            {
                throw new AggregateException("Updating startup settings failed and the registry rollback was incomplete.", original, rollbackError);
            }
            throw;
        }
    }

    internal static string BuildStartupCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return $"\"{Path.GetFullPath(executable)}\" --startup";
    }

    internal static bool IsStartupCommandForExecutable(string? configuredCommand, string executable)
    {
        if (string.IsNullOrWhiteSpace(configuredCommand))
            return false;

        return string.Equals(
            configuredCommand.Trim(),
            BuildStartupCommand(executable),
            StringComparison.OrdinalIgnoreCase);
    }

    private bool? TryReadAndRepairStartupRegistryState()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            var configuredCommand = key?.GetValue(RunValueName) as string;
            if (string.IsNullOrWhiteSpace(configuredCommand))
                return false;

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return true;
            if (IsStartupCommandForExecutable(configuredCommand, executable))
                return true;

            key?.SetValue(RunValueName, BuildStartupCommand(executable), RegistryValueKind.String);
            return true;
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
                return new AppSettings();
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
