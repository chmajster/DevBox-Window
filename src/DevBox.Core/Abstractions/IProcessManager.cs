using DevBox.Core.Models;

namespace DevBox.Core.Abstractions;

public interface IProcessManager : IDisposable
{
    Task<ServiceSnapshot> StartAsync(ServiceDefinition definition, CancellationToken cancellationToken = default);
    Task<ServiceSnapshot> StopAsync(ServiceDefinition definition, CancellationToken cancellationToken = default);
    Task<ServiceSnapshot> RestartAsync(ServiceDefinition definition, CancellationToken cancellationToken = default);
    ServiceSnapshot GetStatus(ServiceDefinition definition);
}
