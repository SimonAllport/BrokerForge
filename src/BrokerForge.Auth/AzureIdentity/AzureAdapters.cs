using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth.AzureIdentity;

/// <summary>App-only tokens from the host's managed identity (or any Azure.Identity credential).</summary>
public sealed class AzureTokenSource(TokenCredential credential) : ITokenSource
{
    public async Task<string> GetTokenAsync(string scope, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(new[] { scope }), ct).ConfigureAwait(false);
        return token.Token;
    }
}

/// <summary>Resolves <c>kv:https://&lt;vault&gt;.vault.azure.net/secrets/&lt;name&gt;</c> references with the host's identity.</summary>
public sealed class KeyVaultSecretResolver(TokenCredential credential) : ISecretResolver
{
    public async Task<string> ResolveAsync(string reference, CancellationToken ct)
    {
        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            throw new BrokerConfigurationException("Key Vault secret reference is not an absolute secret URI.");
        }

        var id = new KeyVaultSecretIdentifier(uri);
        var client = new SecretClient(id.VaultUri, credential);
        var secret = await client.GetSecretAsync(id.Name, id.Version, ct).ConfigureAwait(false);
        return secret.Value.Value;
    }
}
