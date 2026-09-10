namespace DevBox.Core.Models;

public sealed record PhpExtensionCheckResult(
    bool RuntimeAvailable,
    IReadOnlyList<string> LoadedExtensions,
    IReadOnlyList<string> MissingExtensions,
    string? Error)
{
    public IReadOnlyList<string> Missing => MissingExtensions;
    public bool Success => RuntimeAvailable && MissingExtensions.Count == 0 && string.IsNullOrWhiteSpace(Error);
}
