namespace DevBox.Core.Models;

public sealed record ProjectStackProfile(
    string Key,
    string DisplayName,
    ProjectKind Kind,
    string? PhpVersion,
    string? NodeVersion,
    string DatabaseEngine,
    bool Https,
    IReadOnlyList<string> Addons,
    string Description);
