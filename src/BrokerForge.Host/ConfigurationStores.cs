using BrokerForge.Abstractions;

namespace BrokerForge.Host;

/// <summary>Service instances and object maps from appsettings. For local development and contract tests.</summary>
public sealed class OptionsConfigurationStore(BrokerForgeOptions options) : IConfigurationStore
{
    private readonly IReadOnlyDictionary<string, ServiceInstance> _instances =
        options.ServiceInstances.ToDictionary(i => i.Name, i => i.ToModel(), StringComparer.Ordinal);

    private readonly IReadOnlyDictionary<(string, string), ObjectMap> _maps =
        options.ObjectMaps.ToDictionary(m => (m.ServiceInstance, m.Table), m => m.ToModel());

    private readonly IReadOnlyDictionary<(string, string), string> _customApis =
        options.CustomApis.ToDictionary(c => (c.ServiceInstance, c.UniqueName), c => c.Operation);

    public Task<ServiceInstance?> GetServiceInstanceAsync(string name, CancellationToken ct) =>
        Task.FromResult(_instances.TryGetValue(name, out var i) ? i : null);

    public Task<ObjectMap?> GetObjectMapAsync(string serviceInstance, string table, CancellationToken ct) =>
        Task.FromResult(_maps.TryGetValue((serviceInstance, table), out var m) ? m : null);

    public Task<string?> GetCustomApiOperationAsync(string serviceInstance, string customApi, CancellationToken ct) =>
        Task.FromResult(_customApis.TryGetValue((serviceInstance, customApi), out var op) ? op : null);
}

/// <summary>Phase 1: read bf_serviceinstance, bf_objectmap and Custom API bindings through the Dataverse Web API, cached.</summary>
public sealed class DataverseConfigurationStore : IConfigurationStore
{
    public Task<ServiceInstance?> GetServiceInstanceAsync(string name, CancellationToken ct) =>
        throw new NotImplementedException("Phase 1: Dataverse-backed bf_serviceinstance store.");

    public Task<ObjectMap?> GetObjectMapAsync(string serviceInstance, string table, CancellationToken ct) =>
        throw new NotImplementedException("Phase 1: Dataverse-backed bf_objectmap store.");

    public Task<string?> GetCustomApiOperationAsync(string serviceInstance, string customApi, CancellationToken ct) =>
        throw new NotImplementedException("Phase 1: Dataverse-backed Custom API binding store.");
}
