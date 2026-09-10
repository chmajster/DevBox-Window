namespace DevBox.Core.Models;

public enum RuntimeSupportState
{
    Current,
    Maintenance,
    EndOfLife,
    Unknown
}

public sealed record RuntimePackageEntry
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required string Version { get; init; }
    public required string Architecture { get; init; }
    public required string ExecutableRelativePath { get; init; }
    public string? DownloadUrl { get; init; }
    public string? Sha256 { get; init; }
    public string? ArchiveRootDirectory { get; init; }
    public DateOnly? EndOfLifeDate { get; init; }
    public bool Recommended { get; init; }
    public string Channel { get; init; } = "stable";

    public RuntimeDefinition ToRuntimeDefinition() => new(
        Key,
        DisplayName,
        Version,
        DownloadUrl,
        Sha256,
        ExecutableRelativePath,
        ArchiveRootDirectory);
}

public sealed record RuntimeVersionStatus(
    RuntimePackageEntry Package,
    bool Installed,
    bool Active,
    bool Valid,
    RuntimeSupportState SupportState);

public enum DatabaseEngineKind
{
    MySql,
    MariaDb,
    PostgreSql
}

public sealed record DatabaseRuntimeInstance(
    DatabaseEngineKind Engine,
    string Version,
    int Port,
    string RuntimePath,
    string DataPath,
    string LogPath,
    bool Initialized,
    ServiceState State,
    int? ProcessId);

public sealed record DatabaseBackupResult(
    string Engine,
    string DatabaseName,
    string BackupPath,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);
