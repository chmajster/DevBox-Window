namespace DevBox.Core.Models;

public sealed record ServiceSnapshot(
    string Key,
    string DisplayName,
    ServiceState State,
    int? ProcessId,
    int Port,
    string Version,
    TimeSpan? Uptime,
    string? LastError);
