namespace DevBox.Core.Models;

public sealed record AddonDefinition(
    string Key,
    string DisplayName,
    string Description,
    string InstallPath,
    string EntryPointPath,
    string LocalUrl,
    IReadOnlyList<string> RequiredPhpExtensions,
    string Version,
    string DownloadUrl,
    string Sha256,
    string ArchiveRootDirectory);
