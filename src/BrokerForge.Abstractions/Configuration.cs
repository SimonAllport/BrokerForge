using BrokerForge.Contracts;

namespace BrokerForge.Abstractions;

/// <summary>Who an async call runs as when there is no interactive user (HLD 9.3).</summary>
public enum AsyncContextRule
{
    /// <summary>Run as the host's service identity (K2's behaviour for workflow server events).</summary>
    ServiceIdentity,

    /// <summary>Require a stored credential for the user who triggered the run; otherwise ConnectionRequired.</summary>
    RequireUserCredential,
}

/// <summary>One configured connection to a source: a <c>bf_serviceinstance</c> row (HLD 6).</summary>
public sealed record ServiceInstance
{
    public required string Name { get; init; }

    /// <summary>Matches <see cref="IBroker.BrokerType"/>: "rest", "sql", "odata", …</summary>
    public required string BrokerType { get; init; }

    public required Uri BaseUrl { get; init; }

    public required AuthMode AuthMode { get; init; }

    /// <summary>Named secret references, e.g. "partnerKey" → "kv:https://vault.vault.azure.net/secrets/partner-key". Never the secret itself.</summary>
    public IReadOnlyDictionary<string, string> SecretReferences { get; init; } = new Dictionary<string, string>();

    /// <summary>Non-secret instance config readable by descriptors through the <c>instance</c> binding.</summary>
    public IReadOnlyDictionary<string, string> Config { get; init; } = new Dictionary<string, string>();

    /// <summary>Name of a <c>bf_oauthresource</c> row, for the OAuth mode.</summary>
    public string? OAuthResource { get; init; }

    public AsyncContextRule AsyncContextRule { get; init; } = AsyncContextRule.RequireUserCredential;

    /// <summary>Dataverse environment IDs allowed to call this instance. Empty means none: fail closed.</summary>
    public IReadOnlyCollection<string> AllowedEnvironments { get; init; } = Array.Empty<string>();
}

/// <summary>Links a virtual table to its operations and key strategy: a <c>bf_objectmap</c> row (HLD 6).</summary>
public sealed record ObjectMap
{
    public required string ServiceInstance { get; init; }

    public required string Table { get; init; }

    public required KeyStrategy KeyStrategy { get; init; }

    /// <summary>Operation name per message. A message with no entry is not supported for this table.</summary>
    public IReadOnlyDictionary<BrokerMessage, string> Operations { get; init; } = new Dictionary<BrokerMessage, string>();
}

/// <summary>Read access to service instances and object maps. Dataverse-backed in production, file-backed for local work.</summary>
public interface IConfigurationStore
{
    Task<ServiceInstance?> GetServiceInstanceAsync(string name, CancellationToken ct);

    Task<ObjectMap?> GetObjectMapAsync(string serviceInstance, string table, CancellationToken ct);

    /// <summary>Operation name bound to a Custom API unique name, or null.</summary>
    Task<string?> GetCustomApiOperationAsync(string serviceInstance, string customApi, CancellationToken ct);
}

/// <summary>Resolves a secret reference to its value. Values are used, never logged or returned.</summary>
public interface ISecretResolver
{
    Task<string> ResolveAsync(string reference, CancellationToken ct);
}
