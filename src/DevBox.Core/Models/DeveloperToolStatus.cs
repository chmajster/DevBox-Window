namespace DevBox.Core.Models;

public sealed record DeveloperToolStatus(
    string Key,
    string DisplayName,
    bool Installed,
    string? Version,
    string? ExecutablePath,
    string InstallMethod);
