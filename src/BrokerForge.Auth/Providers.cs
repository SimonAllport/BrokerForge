using System.Net.Http.Headers;
using System.Text;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Auth;

/// <summary>Applies a fixed set of headers. Used by the Static and Service Account modes.</summary>
public sealed class HeaderChannel : IAuthenticatedChannel
{
    private readonly IReadOnlyList<KeyValuePair<string, string>> _headers;

    public HeaderChannel(AuthMode mode, IReadOnlyList<KeyValuePair<string, string>> headers)
    {
        Mode = mode;
        _headers = headers;
        ReservedHeaders = headers.Select(h => h.Key).Append("Authorization").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public AuthMode Mode { get; }

    public IReadOnlyCollection<string> ReservedHeaders { get; }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        foreach (var (name, value) in _headers)
        {
            if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>Acquires app-only tokens for the Service Account mode. Implemented over Azure.Identity in <c>AzureIdentity/AzureAdapters.cs</c>.</summary>
public interface ITokenSource
{
    Task<string> GetTokenAsync(string scope, CancellationToken ct);
}

/// <summary>
/// Service Account: the host's managed identity or app registration (client credentials).
/// Instance config: <c>serviceAccount.scope</c>, e.g. "api://orders/.default".
/// </summary>
public sealed class ServiceAccountAuthProvider(ITokenSource tokens) : IAuthProvider
{
    public AuthMode Mode => AuthMode.ServiceAccount;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct)
    {
        var scope = instance.Config.TryGetValue("serviceAccount.scope", out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : throw new BrokerConfigurationException($"Service instance '{instance.Name}' uses Service Account but has no serviceAccount.scope.");

        var token = await tokens.GetTokenAsync(scope, ct).ConfigureAwait(false);
        return new HeaderChannel(Mode, new[] { new KeyValuePair<string, string>("Authorization", $"Bearer {token}") });
    }
}

/// <summary>
/// Static: one fixed credential per instance, from Key Vault (HLD 9.1).
/// Instance config <c>static.kind</c>: apiKeyHeader (with <c>static.header</c> and secret <c>apiKey</c>),
/// basic (secrets <c>username</c>, <c>password</c>) or bearer (secret <c>token</c>).
/// </summary>
public sealed class StaticAuthProvider(ISecretResolver secrets) : IAuthProvider
{
    public AuthMode Mode => AuthMode.Static;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct)
    {
        var kind = Config(instance, "static.kind");
        IReadOnlyList<KeyValuePair<string, string>> headers = kind switch
        {
            "apiKeyHeader" => new[]
            {
                new KeyValuePair<string, string>(Config(instance, "static.header"), await Secret(instance, "apiKey", ct).ConfigureAwait(false)),
            },
            "basic" => new[]
            {
                new KeyValuePair<string, string>("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{await Secret(instance, "username", ct).ConfigureAwait(false)}:{await Secret(instance, "password", ct).ConfigureAwait(false)}"))),
            },
            "bearer" => new[]
            {
                new KeyValuePair<string, string>("Authorization", "Bearer " + await Secret(instance, "token", ct).ConfigureAwait(false)),
            },
            _ => throw new BrokerConfigurationException($"Service instance '{instance.Name}': static.kind '{kind}' is not apiKeyHeader, basic or bearer."),
        };

        return new HeaderChannel(Mode, headers);
    }

    private static string Config(ServiceInstance instance, string key) =>
        instance.Config.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new BrokerConfigurationException($"Service instance '{instance.Name}' uses Static auth but has no {key}.");

    private async Task<string> Secret(ServiceInstance instance, string name, CancellationToken ct)
    {
        if (!instance.SecretReferences.TryGetValue(name, out var reference))
        {
            throw new BrokerConfigurationException($"Service instance '{instance.Name}' has no secret reference named '{name}'.");
        }

        return await secrets.ResolveAsync(reference, ct).ConfigureAwait(false);
    }
}

/// <summary>Phase 3. Delegated token for the calling user from the host token store; ConnectionRequired when absent. Never falls back.</summary>
public sealed class ImpersonateAuthProvider : IAuthProvider
{
    public AuthMode Mode => AuthMode.Impersonate;

    public Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct) =>
        throw new NotImplementedException("Phase 3: Impersonate mode (delegated token from the host token store).");
}

/// <summary>Phase 3. The user's stored credentials for the source; ConnectionRequired when absent. Never falls back.</summary>
public sealed class SsoAuthProvider : IAuthProvider
{
    public AuthMode Mode => AuthMode.Sso;

    public Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct) =>
        throw new NotImplementedException("Phase 3: SSO mode (encrypted per-user credential store).");
}

/// <summary>Phase 3. Authorisation-code flow tokens with refresh; ConnectionRequired when absent or unrefreshable. Never falls back.</summary>
public sealed class OAuthAuthProvider : IAuthProvider
{
    public AuthMode Mode => AuthMode.OAuth;

    public Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct) =>
        throw new NotImplementedException("Phase 3: OAuth mode (bf_oauthresource, token store, refresh).");
}

/// <summary>Picks the provider for an instance's mode. There is no default and no fallback.</summary>
public sealed class AuthProviderRegistry(IEnumerable<IAuthProvider> providers)
{
    private readonly IReadOnlyDictionary<AuthMode, IAuthProvider> _providers = providers.ToDictionary(p => p.Mode);

    public IAuthProvider For(ServiceInstance instance) =>
        _providers.TryGetValue(instance.AuthMode, out var provider)
            ? provider
            : throw new BrokerConfigurationException($"Authentication mode {instance.AuthMode} is not available on this host (service instance '{instance.Name}').");
}
