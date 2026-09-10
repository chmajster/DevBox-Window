namespace DevBox.Core.Models;

public sealed record ManagedServiceManifest(
    int SchemaVersion,
    string Key,
    string DisplayName,
    string ExecutableRelativePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectoryRelativePath,
    int Port,
    string Version,
    bool Enabled = true,
    string? StopExecutableRelativePath = null,
    IReadOnlyList<string>? StopArguments = null,
    int GracefulStopTimeoutSeconds = 5,
    string? LogRelativePath = null)
{
    public const int CurrentSchemaVersion = 1;
}
