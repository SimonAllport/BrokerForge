using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Execution;
using BrokerForge.Descriptors.Storage;
using BrokerForge.Keys;

namespace BrokerForge.Host;

/// <summary>
/// The K2 host-server equivalent: resolves the instance and descriptor, rejects undeclared queries, authenticates,
/// renders, calls the adapter and maps the result (HLD 10). Every failure is a typed BrokerForgeException.
/// </summary>
public sealed class BrokerRouter(
    IConfigurationStore config,
    IDescriptorStore descriptors,
    AuthProviderRegistry auth,
    IEnumerable<IBroker> brokers,
    IRequestRenderer renderer,
    IResponseMapper mapper)
{
    private readonly IReadOnlyDictionary<string, IBroker> _brokers =
        brokers.ToDictionary(b => b.BrokerType, StringComparer.OrdinalIgnoreCase);

    public async Task<CanonicalResponse> ExecuteAsync(CanonicalRequest request, TrustDecision trust, CancellationToken ct)
    {
        if (request.ContractVersion != ContractVersions.Current)
        {
            throw new BrokerConfigurationException($"Contract version '{request.ContractVersion}' is not supported; this host speaks '{ContractVersions.Current}'.");
        }

        if (trust.EnvironmentId is not null && !string.Equals(trust.EnvironmentId, request.EnvironmentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new BrokerConfigurationException("The request's environment does not match the caller's token.");
        }

        var instance = await config.GetServiceInstanceAsync(request.ServiceInstance, ct).ConfigureAwait(false)
            ?? throw new BrokerConfigurationException($"Unknown service instance '{request.ServiceInstance}'.");

        if (!instance.AllowedEnvironments.Contains(request.EnvironmentId, StringComparer.OrdinalIgnoreCase))
        {
            throw new BrokerConfigurationException($"Environment '{request.EnvironmentId}' is not allowed to call service instance '{instance.Name}'.");
        }

        var message = request.Message;
        var (operation, keys) = await ResolveOperationAsync(request, instance, message, ct).ConfigureAwait(false);
        var descriptor = await descriptors.GetAsync(instance.Name, operation, ct).ConfigureAwait(false);

        var expectedBinds = message == BrokerMessage.CustomApi ? $"customapi:{request.CustomApi}" : message.ToString();
        if (!string.Equals(descriptor.Binds, expectedBinds, StringComparison.Ordinal))
        {
            throw new BrokerConfigurationException($"Operation '{operation}' binds {descriptor.Binds}, not {expectedBinds}.");
        }

        // Fail before any call to the source (HLD 10).
        if (request.Query is not null)
        {
            QueryCapabilityCheck.Ensure(descriptor, request.Query);
        }

        var key = DecodeKey(request, keys);

        // Phase 3: apply instance.AsyncContextRule when request.Caller.Async is true.
        var channel = await auth.For(instance).AuthenticateAsync(instance, request.Caller, ct).ConfigureAwait(false);

        var rendered = await renderer.RenderAsync(descriptor, new RenderContext(request, instance, key, channel.ReservedHeaders), ct).ConfigureAwait(false);

        var broker = _brokers.TryGetValue(instance.BrokerType, out var b)
            ? b
            : throw new BrokerConfigurationException($"No adapter for broker type '{instance.BrokerType}'.");

        var result = await broker.ExecuteAsync(rendered, channel, ct).ConfigureAwait(false);
        var response = mapper.Map(descriptor, result, new MapContext(request, keys));
        response.CorrelationId = request.CorrelationId;
        return response;
    }

    private async Task<(string Operation, IKeyStrategy Keys)> ResolveOperationAsync(
        CanonicalRequest request, ServiceInstance instance, BrokerMessage message, CancellationToken ct)
    {
        if (message == BrokerMessage.CustomApi)
        {
            var name = request.CustomApi ?? throw new BrokerConfigurationException("CustomApi requests must name the Custom API.");
            var op = await config.GetCustomApiOperationAsync(instance.Name, name, ct).ConfigureAwait(false)
                ?? throw new BrokerConfigurationException($"Custom API '{name}' is not bound to an operation on '{instance.Name}'.");

            // Custom API ids arrive as parameters and are bound by the descriptor; no table key strategy applies.
            return (op, KeyStrategyFactory.For(KeyStrategy.NativeGuid));
        }

        var table = request.Table ?? throw new BrokerConfigurationException($"{message} requests must name the table.");
        var map = await config.GetObjectMapAsync(instance.Name, table, ct).ConfigureAwait(false)
            ?? throw new BrokerConfigurationException($"Table '{table}' is not mapped on service instance '{instance.Name}'.");

        return map.Operations.TryGetValue(message, out var operation)
            ? (operation, KeyStrategyFactory.For(map.KeyStrategy))
            : throw new BrokerConfigurationException($"{message} is not available for table '{table}'.");
    }

    private static ExternalKey? DecodeKey(CanonicalRequest request, IKeyStrategy keys)
    {
        if (request.Id is null || request.Message == BrokerMessage.CustomApi)
        {
            return null;
        }

        if (!Guid.TryParse(request.Id, out var id))
        {
            throw new MappingException("id", "Record id is not a GUID.");
        }

        // A GUID this strategy did not produce cannot exist in the source.
        return keys.TryDecode(id, out var key)
            ? key
            : throw new SourceMappedException(ErrorCode.NotFound, "Record not found.");
    }
}
