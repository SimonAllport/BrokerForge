using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth;

/// <summary>
/// Routes a secret reference by scheme: <c>kv:&lt;secret URI&gt;</c> to Key Vault, <c>env:&lt;NAME&gt;</c> to an environment
/// variable (development and tests only; the host registers it only in Development).
/// </summary>
public sealed class SecretResolver(IReadOnlyDictionary<string, ISecretResolver> byScheme) : ISecretResolver
{
    public Task<string> ResolveAsync(string reference, CancellationToken ct)
    {
        var colon = reference.IndexOf(':');
        var scheme = colon > 0 ? reference[..colon] : "";
        return byScheme.TryGetValue(scheme, out var resolver)
            ? resolver.ResolveAsync(reference[(colon + 1)..], ct)
            : throw new BrokerConfigurationException($"Secret reference scheme '{scheme}' is not available on this host.");
    }
}

/// <summary>Reads a secret from an environment variable. For local development and tests with dummy values only.</summary>
public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public Task<string> ResolveAsync(string reference, CancellationToken ct) =>
        Environment.GetEnvironmentVariable(reference) is { Length: > 0 } value
            ? Task.FromResult(value)
            : throw new BrokerConfigurationException($"Environment secret '{reference}' is not set.");
}
