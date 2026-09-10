using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class NodeRuntimeService
{
    private readonly IRuntimeManager _runtimeManager;
    private readonly NodeRuntimeCatalog _catalog;

    public NodeRuntimeService(IRuntimeManager runtimeManager, NodeRuntimeCatalog catalog)
    {
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public Task InstallRecommendedAsync(CancellationToken cancellationToken = default) =>
        _runtimeManager.InstallAsync(_catalog.GetRecommended(), cancellationToken);

    public IReadOnlyList<RuntimeInstallation> GetInstalled() =>
        _runtimeManager.GetInstalled("node", "node.exe");
}
