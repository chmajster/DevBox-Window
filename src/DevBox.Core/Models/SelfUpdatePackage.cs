namespace DevBox.Core.Models;

public sealed record SelfUpdatePackage(
    Version Version,
    string InstallerPath,
    string InstallerFileName,
    string Sha256,
    string ReleaseUrl);
