using BrokerForge.Contracts;

namespace BrokerForge.Abstractions;

/// <summary>One adapter per source type (HLD 7.2). Adapters never read secrets or descriptors directly.</summary>
public interface IBroker
{
    /// <summary>"rest", "sql", "odata", … Matches <see cref="ServiceInstance.BrokerType"/>.</summary>
    string BrokerType { get; }

    BrokerCapabilities Capabilities { get; }

    Task<DiscoveryResult> DiscoverAsync(ServiceInstance instance, CancellationToken ct);

    Task<BrokerResult> ExecuteAsync(RenderedRequest request, IAuthenticatedChannel channel, CancellationToken ct);
}

/// <summary>What an adapter can do, so the descriptor validator can reject descriptors it cannot honour.</summary>
public sealed record BrokerCapabilities
{
    public IReadOnlyCollection<string> FilterOperators { get; init; } = Array.Empty<string>();

    public IReadOnlyCollection<string> PagingStrategies { get; init; } = Array.Empty<string>();

    public bool SupportsTransactions { get; init; }
}

/// <summary>Draft operations and schema found by discovery (SwaggerForge for REST).</summary>
public sealed record DiscoveryResult
{
    public IReadOnlyList<string> DraftDescriptors { get; init; } = Array.Empty<string>();

    public IReadOnlyList<TableSchema> Tables { get; init; } = Array.Empty<TableSchema>();
}

/// <summary>A generated virtual table's columns, used by validator rule 7 (every column mapped or listed as unmapped).</summary>
public sealed record TableSchema
{
    public required string LogicalName { get; init; }

    public IReadOnlyDictionary<string, FieldType> Columns { get; init; } = new Dictionary<string, FieldType>();
}

/// <summary>A request with every binding resolved except authentication, ready for an adapter to send.</summary>
public sealed record RenderedRequest
{
    public required string CorrelationId { get; init; }

    public required HttpMethod Method { get; init; }

    /// <summary>Absolute URI including the encoded query string.</summary>
    public required Uri Uri { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public string? Body { get; init; }

    public string? ContentType { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>The raw result an adapter returns; the response mapper turns it into rows, a record or outputs.</summary>
public sealed record BrokerResult
{
    public required int StatusCode { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public string? Body { get; init; }
}
