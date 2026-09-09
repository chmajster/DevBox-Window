namespace DevBox.Core.Models;

public sealed record ServiceDefinition(
    string Key,
    string DisplayName,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    int Port,
    string Version,
    string? StopExecutablePath = null,
    IReadOnlyList<string>? StopArguments = null,
    TimeSpan? ShutdownTimeout = null,
    string? LogPath = null);
