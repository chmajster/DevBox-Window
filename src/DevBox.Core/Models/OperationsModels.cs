namespace DevBox.Core.Models;

public enum PlatformTaskState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record PlatformTaskSnapshot(
    Guid Id,
    string Name,
    PlatformTaskState State,
    double Progress,
    string? Message,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    string? Error);

public enum DiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record AdvancedDiagnosticFinding(
    string Key,
    string Area,
    DiagnosticSeverity Severity,
    string Summary,
    string Details,
    string? SuggestedAction = null);

public sealed record AdvancedDiagnosticReport(
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<AdvancedDiagnosticFinding> Findings)
{
    public bool HasErrors => Findings.Any(item => item.Severity == DiagnosticSeverity.Error);
    public bool HasWarnings => Findings.Any(item => item.Severity == DiagnosticSeverity.Warning);
}

public sealed record ProjectSnapshotOptions(
    bool IncludeDatabase = true,
    bool IncludeVendor = false,
    bool IncludeNodeModules = false,
    bool IncludeGitDirectory = false);

public sealed record ProjectSnapshotResult(
    string SnapshotPath,
    string ProjectName,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<string> IncludedFiles);

public sealed record ProjectTransferManifest
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string ProjectName { get; init; }
    public required string Domain { get; init; }
    public DateTimeOffset ExportedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string ProjectDirectory { get; init; }
    public string? EnvironmentLockFile { get; init; }
    public IReadOnlyList<string> DatabaseBackups { get; init; } = Array.Empty<string>();
}

public sealed record ProjectTransferResult(
    string ArchivePath,
    ProjectTransferManifest Manifest,
    long SizeBytes);

public sealed record ConfigurationValidationResult(
    bool IsValid,
    string ConfigurationKey,
    string? BackupPath,
    string Message);
