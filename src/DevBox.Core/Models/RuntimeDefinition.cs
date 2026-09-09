namespace DevBox.Core.Models;

public sealed record RuntimeDefinition(
    string Key,
    string DisplayName,
    string Version,
    string DownloadUrl,
    string Sha256,
    string ExecutableRelativePath,
    string? ArchiveRootDirectory = null);

public sealed record RuntimeInstallation(
    string Key,
    string Version,
    string InstallPath,
    bool IsActive,
    bool IsValid);
