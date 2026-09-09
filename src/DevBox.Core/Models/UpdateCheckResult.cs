namespace DevBox.Core.Models;

public sealed record UpdateCheckResult(
    Version CurrentVersion,
    Version LatestVersion,
    bool UpdateAvailable,
    string ReleaseUrl);
