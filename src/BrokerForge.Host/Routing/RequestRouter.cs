using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Host.Config;
using BrokerForge.Host.Execution;
using BrokerForge.Keys;

namespace BrokerForge.Host.Routing;

/// <summary>
/// Resolves a canonical request to its object map, approved descriptor, key strategy and authenticated
/// channel (HLD 4 step 3, 5). A table, message or instance that does not line up is refused.
/// </summary>
public sealed class RequestRouter(HostConfigStore config, IEnumerable<IAuthProvider> providers, IEnumerable<IBroker> brokers)
{
    private readonly Dictionary<AuthMode, IAuthProvider> _providers = providers.ToDictionary(p => p.Mode);
    private readonly Dictionary<string, IBroker> _brokers = brokers.ToDictionary(b => b.BrokerType);

    public bool HasOperation(string? table, string message) =>
        table != null && config.Map(table)?.Map.Operations.ContainsKey(message) == true;

    public async Task<(OperationContext Op, IBroker Broker)> ResolveAsync(CanonicalRequest request, CancellationToken ct)
    {
        var correlationId = request.CorrelationId!;
        UnsupportedQueryException Reject(string why) => new(why, correlationId);

        var resolved = config.Map(request.Table ?? "") ?? throw Reject($"Table {request.Table} is not mapped to a BrokerForge source.");
        if (resolved.Map.ServiceInstance != request.ServiceInstance)
            throw Reject($"Table {request.Table} belongs to service instance {resolved.Map.ServiceInstance}, not {request.ServiceInstance}.");
        if (!resolved.Map.Operations.TryGetValue(request.Message ?? "", out var operation))
            throw Reject($"Table {request.Table} has no {request.Message} operation.");
        var instance = config.Instance(resolved.Map.ServiceInstance)!;
        var descriptor = config.Descriptor(instance.Name, operation)!;

        var broker = _brokers.GetValueOrDefault(instance.BrokerType)
            ?? throw new InvalidOperationException($"No adapter is registered for broker type {instance.BrokerType}.");
        var provider = _providers.GetValueOrDefault(instance.Auth.Mode)
            ?? throw new InvalidOperationException($"No authentication provider is registered for mode {instance.Auth.Mode}.");
        var caller = request.Caller ?? new CallerContext();
        var channel = await provider.AuthenticateAsync(instance, caller, correlationId, ct);

        var record = new Dictionary<string, Cell>(StringComparer.Ordinal);
        foreach (var cell in request.Record ?? new List<Cell>())
            if (cell.Col == null || !record.TryAdd(cell.Col, cell))
                throw new MappedSourceException(SourceErrorKinds.Validation, $"The record names column {cell.Col ?? "(none)"} twice or not at all.", correlationId);

        var binding = new BindingContext(
            instance,
            correlationId,
            request.EnvironmentId!,
            caller.UserObjectId,
            caller.Async,
            record,
            request.Message == Messages.Update ? record.Keys.ToHashSet(StringComparer.Ordinal) : null,
            DecodeId(request, resolved.Keys),
            null,
            null);
        return (new OperationContext(instance, resolved.Map, descriptor, resolved.Keys, channel, binding), broker);
    }

    private static ExternalKey? DecodeId(CanonicalRequest request, IKeyStrategy keys)
    {
        if (request.Message is not (Messages.Retrieve or Messages.Update or Messages.Delete))
            return null;
        if (!Guid.TryParse(request.Id, out var id))
            throw new MappedSourceException(SourceErrorKinds.Validation, $"{request.Message} needs the record id as a GUID.", request.CorrelationId!);
        return keys.Decode(id);
    }
}
