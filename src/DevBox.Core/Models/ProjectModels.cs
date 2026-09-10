namespace DevBox.Core.Models;

public enum ProjectKind
{
    Unknown,
    EmptyPhp,
    Php,
    Laravel,
    Symfony,
    WordPress,
    Node
}

public enum ProjectHealthState
{
    Healthy,
    Warning,
    Error
}

public sealed record ProjectDetectionResult(
    ProjectKind Kind,
    string RootPath,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> RequiredPhpExtensions);

public sealed record DevBoxProjectManifest(
    int SchemaVersion,
    string Name,
    string Domain,
    ProjectKind Kind,
    string? PhpVersion,
    string? NodeVersion,
    string DatabaseEngine,
    string? DatabaseName,
    bool Https,
    IReadOnlyList<string> Addons,
    IReadOnlyList<string> Services)
{
    public const int CurrentSchemaVersion = 1;

    public DevBoxProjectManifest(
        int schemaVersion,
        string name,
        string domain,
        ProjectKind kind,
        string? phpVersion,
        string? nodeVersion,
        string databaseEngine,
        string? databaseName,
        bool https,
        IReadOnlyList<string> addons)
        : this(schemaVersion, name, domain, kind, phpVersion, nodeVersion, databaseEngine, databaseName, https, addons, Array.Empty<string>())
    {
    }
}

public sealed record ProjectCreateRequest(
    string Name,
    string? Domain = null,
    ProjectKind Kind = ProjectKind.EmptyPhp,
    string? PhpVersion = null,
    bool Https = false,
    string DatabaseEngine = "mysql",
    string? DatabaseName = null,
    string? NodeVersion = null,
    IReadOnlyList<string>? Addons = null,
    IReadOnlyList<string>? Services = null);

public sealed record ProjectImportRequest(
    string SourcePath,
    string Name,
    string? Domain = null,
    string? PhpVersion = null,
    bool Https = false,
    bool CopyIntoDevBox = true);

public sealed record ProjectHealthCheck(
    string Key,
    string DisplayName,
    ProjectHealthState State,
    string Details,
    bool CanRepair = false);

public sealed record ProjectHealthReport(
    SiteDefinition Site,
    ProjectKind Kind,
    IReadOnlyList<ProjectHealthCheck> Checks,
    IReadOnlyList<string> RequiredPhpExtensions)
{
    public bool IsHealthy => Checks.All(check => check.State != ProjectHealthState.Error);
}

public sealed record ProjectRepairResult(
    IReadOnlyList<string> Repaired,
    IReadOnlyList<string> RemainingProblems);
