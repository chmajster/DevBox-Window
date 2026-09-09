namespace DevBox.Core.Models;

public sealed record EnvironmentReadinessItem(
    string Key,
    string DisplayName,
    bool Ready,
    string Details,
    bool CanInstallAutomatically);

public sealed record EnvironmentReadiness(
    IReadOnlyList<EnvironmentReadinessItem> Items)
{
    public bool IsReady => Items.All(item => item.Ready);
}
