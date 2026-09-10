using DevBox.Core.Abstractions;
using DevBox.Core.Models;

namespace DevBox.Core.Services;

public sealed class OptionalRuntimeInstaller
{
    private readonly IRuntimeManager _runtimeManager;
    private readonly OptionalRuntimeCatalog _runtimeCatalog;
    private readonly ManagedServiceCatalog _services;

    public OptionalRuntimeInstaller(
        IRuntimeManager runtimeManager,
        OptionalRuntimeCatalog runtimeCatalog,
        ManagedServiceCatalog services)
    {
        _runtimeManager = runtimeManager ?? throw new ArgumentNullException(nameof(runtimeManager));
        _runtimeCatalog = runtimeCatalog ?? throw new ArgumentNullException(nameof(runtimeCatalog));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public async Task<ManagedServiceManifest> InstallAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var definition = _runtimeCatalog.Get(key);
        await _runtimeManager.InstallAsync(definition, cancellationToken).ConfigureAwait(false);

        var manifest = definition.Key switch
        {
            "mailpit" => ManagedServiceCatalog.MailpitTemplate(definition.Version),
            "redis" => ManagedServiceCatalog.RedisTemplate(definition.Version),
            _ => throw new InvalidOperationException($"Runtime '{definition.Key}' does not map to a managed service.")
        };

        _services.Upsert(manifest with { Enabled = true });
        return manifest;
    }

    public IReadOnlyList<RuntimeInstallation> GetInstalled(string key)
    {
        var definition = _runtimeCatalog.Get(key);
        return _runtimeManager.GetInstalled(definition.Key, definition.ExecutableRelativePath);
    }
}
