using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using BrokerForge.Abstractions;

namespace BrokerForge.Auth;

/// <summary>Routes <c>kv:</c> to Key Vault and <c>env:</c> to environment variables. <c>env:</c> works only when Secrets:AllowEnvironment is true (tests, local runs).</summary>
public sealed class SecretResolverRouter(ISecretResolver keyVault, ISecretResolver environment, bool allowEnvironment) : ISecretResolver
{
    public Task<string> GetAsync(SecretRef reference, CancellationToken ct) => reference.Scheme switch
    {
        "kv" => keyVault.GetAsync(reference, ct),
        "env" when allowEnvironment => environment.GetAsync(reference, ct),
        "env" => throw new CredentialUnavailableException($"Secret reference {reference} uses env:, which is disabled (Secrets:AllowEnvironment is false)."),
        _ => throw new CredentialUnavailableException($"Secret reference scheme `{reference.Scheme}` is not supported."),
    };
}

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public Task<string> GetAsync(SecretRef reference, CancellationToken ct) =>
        Environment.GetEnvironmentVariable(reference.Location) is { Length: > 0 } value
            ? Task.FromResult(value)
            : throw new CredentialUnavailableException($"Environment variable {reference.Location} named by {reference} is not set.");
}

/// <summary>Reads <c>kv:https://&lt;vault&gt;.vault.azure.net/secrets/&lt;name&gt;[/&lt;version&gt;]</c> with the host identity. Values are cached for five minutes.</summary>
public sealed class KeyVaultSecretResolver(TokenCredential credential) : ISecretResolver
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Uri, SecretClient> _clients = new();
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset Until)> _cache = new();

    public async Task<string> GetAsync(SecretRef reference, CancellationToken ct)
    {
        if (_cache.TryGetValue(reference.Location, out var hit) && hit.Until > DateTimeOffset.UtcNow)
            return hit.Value;

        KeyVaultSecretIdentifier id;
        try
        {
            id = new KeyVaultSecretIdentifier(new Uri(reference.Location));
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            throw new CredentialUnavailableException($"Secret reference {reference} is not a Key Vault secret URI.", ex);
        }

        var client = _clients.GetOrAdd(id.VaultUri, uri => new SecretClient(uri, credential));
        try
        {
            var secret = await client.GetSecretAsync(id.Name, id.Version, ct);
            _cache[reference.Location] = (secret.Value.Value, DateTimeOffset.UtcNow + Lifetime);
            return secret.Value.Value;
        }
        catch (RequestFailedException ex)
        {
            throw new CredentialUnavailableException($"Key Vault returned {ex.Status} for {reference}.", ex);
        }
    }
}
