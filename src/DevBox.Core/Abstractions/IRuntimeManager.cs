using DevBox.Core.Models;

namespace DevBox.Core.Abstractions;

public interface IRuntimeManager
{
    IReadOnlyList<RuntimeInstallation> GetInstalled(string runtimeKey, string executableRelativePath);
    Task InstallAsync(RuntimeDefinition definition, CancellationToken cancellationToken = default);
    Task ActivateAsync(string runtimeKey, string version, string executableRelativePath, CancellationToken cancellationToken = default);
    Task RemoveAsync(string runtimeKey, string version, CancellationToken cancellationToken = default);
}
