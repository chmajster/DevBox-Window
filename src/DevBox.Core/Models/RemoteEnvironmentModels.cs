namespace DevBox.Core.Models;

public sealed record EnvironmentShareBundle
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Name { get; init; }
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required EnvironmentProfile Profile { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}

public sealed record EnvironmentShareImportResult(
    string ProfileKey,
    string DisplayName,
    bool ReplacedExisting,
    DateTimeOffset ImportedAtUtc);
