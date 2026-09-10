using System.Text.Json.Serialization;

namespace DevBox.Core.Models;

public sealed record EnvironmentRuntimePin(string Key, string Version);

public sealed record EnvironmentDatabasePin(
    string Engine,
    string? Version,
    string? DatabaseName,
    int? Port = null);

public sealed record ProjectActionDefinition(
    string Key,
    string DisplayName,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    int TimeoutSeconds = 600,
    bool Enabled = true);

public sealed record EnvironmentProfile
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public ProjectKind Kind { get; init; } = ProjectKind.EmptyPhp;
    public IReadOnlyDictionary<string, string> Runtimes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public EnvironmentDatabasePin Database { get; init; } = new("none", null, null);
    public bool Https { get; init; }
    public IReadOnlyList<string> Addons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProjectActionDefinition> Actions { get; init; } = Array.Empty<ProjectActionDefinition>();
    public string Description { get; init; } = string.Empty;
}

public sealed record EnvironmentLockFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ProjectName { get; init; }
    public required string Domain { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyDictionary<string, string> Runtimes { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public EnvironmentDatabasePin Database { get; init; } = new("none", null, null);
    public bool Https { get; init; }
    public IReadOnlyList<string> Addons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ProjectActionDefinition> Actions { get; init; } = Array.Empty<ProjectActionDefinition>();
    public string? SourceProfile { get; init; }
}

public sealed record EnvironmentApplyResult(
    EnvironmentLockFile Lock,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Warnings);

public sealed record ProjectActionResult(
    string Key,
    int ExitCode,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError)
{
    [JsonIgnore]
    public bool Succeeded => ExitCode == 0;
}

public sealed record GitBootstrapRequest(
    string RepositoryUrl,
    string ProjectName,
    string? Branch = null,
    string? ProfileKey = null,
    string? Domain = null,
    bool RunBootstrapActions = true);

public sealed record GitBootstrapResult(
    string ProjectRoot,
    ProjectKind DetectedKind,
    EnvironmentApplyResult? Environment,
    IReadOnlyList<ProjectActionResult> Actions);
