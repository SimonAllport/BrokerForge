namespace BrokerForge.Abstractions;

public enum AuthMode { ServiceAccount, Static, Impersonate, Sso, OAuth }

/// <summary>
/// One configured connection to a source (HLD 6, bf_serviceinstance). In phase 1 it comes from host
/// configuration (HLD D-10). It holds secret references, never secret values.
/// </summary>
public sealed record ServiceInstance(
    string Name,
    string BrokerType,
    string BaseUrl,
    AuthConfig Auth,
    IReadOnlyDictionary<string, string> Config,
    IReadOnlyDictionary<string, string> Secrets);

/// <summary>
/// Static: <see cref="StaticKind"/> is apiKeyHeader (with <see cref="HeaderName"/>), apiKeyQuery
/// (with <see cref="QueryParam"/>) or basic (with <see cref="Username"/>); the value comes from
/// <see cref="SecretRef"/>. ServiceAccount: <see cref="Scope"/> is the token scope.
/// </summary>
public sealed record AuthConfig(
    AuthMode Mode,
    string? StaticKind = null,
    string? HeaderName = null,
    string? QueryParam = null,
    string? Username = null,
    string? SecretRef = null,
    string? Scope = null);

/// <summary>Links a virtual table to its operations (HLD 6, bf_objectmap). Operations is keyed by message name.</summary>
public sealed record ObjectMap(
    string ServiceInstance,
    string Table,
    string KeyStrategy,
    string KeyKind,
    IReadOnlyDictionary<string, string> Operations);
