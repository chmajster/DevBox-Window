namespace DevBox.Core.Models;

public sealed record ProjectCommandPreset(
    string Key,
    string DisplayName,
    string Tool,
    IReadOnlyList<string> Arguments,
    string Description);

public sealed record ProjectCommandResult(
    string PresetKey,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration)
{
    public bool Success => ExitCode == 0;
}
