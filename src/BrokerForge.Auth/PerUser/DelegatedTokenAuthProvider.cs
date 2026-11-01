using System.Net.Http.Headers;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Contracts;
using Microsoft.Extensions.Logging;

namespace BrokerForge.Auth.PerUser;

/// <summary>
/// OAuth and Impersonate (HLD 9.1, D-28): the caller's stored access token as a bearer, refreshed when it has
/// under a minute left. No credential, an expired or revoked one, or a refused refresh is a
/// ConnectionRequiredException. Nothing here can reach another identity.
/// </summary>
public sealed class DelegatedTokenAuthProvider(
    AuthMode mode,
    IUserCredentialStore store,
    Func<string, OAuthResource> resources,
    TokenEndpointClient tokens,
    Func<string, string> connectUrl,
    HttpClient http,
    ILogger log,
    TimeProvider time) : IAuthProvider
{
    public static readonly TimeSpan RefreshWindow = TimeSpan.FromSeconds(60);
    private readonly RefreshGate _gate = new();

    public AuthMode Mode => mode is AuthMode.OAuth or AuthMode.Impersonate
        ? mode
        : throw new ArgumentOutOfRangeException(nameof(mode), "DelegatedTokenAuthProvider serves OAuth and Impersonate only.");

    private CredentialKind Kind => mode == AuthMode.OAuth ? CredentialKind.OAuth : CredentialKind.Impersonate;

    public async Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, string correlationId, CancellationToken ct)
    {
        if (instance.Auth.Mode != Mode)
            throw new ConfigurationException($"Service instance {instance.Name} uses {instance.Auth.Mode}, not {Mode}.");
        var user = ConnectionRequired.User(instance, caller, correlationId, connectUrl);

        var credential = Usable(instance, await store.GetAsync(instance.Name, user, ct), correlationId);
        if (!ExpiresSoon(credential))
            return Channel(credential.AccessToken!);

        using (await _gate.EnterAsync(instance.Name, user, ct))
        {
            // Another call may have refreshed while this one waited.
            credential = Usable(instance, await store.GetAsync(instance.Name, user, ct), correlationId);
            if (!ExpiresSoon(credential))
                return Channel(credential.AccessToken!);
            if (credential.RefreshToken == null)
            {
                await store.SetStatusAsync(instance.Name, user, CredentialStatus.Expired, ct);
                throw ConnectionRequired.Because(instance, "the stored token has expired and cannot be refreshed", correlationId, connectUrl);
            }

            TokenSet refreshed;
            try
            {
                refreshed = await tokens.RefreshAsync(resources(instance.Auth.OAuthResource!), credential.RefreshToken, ct);
            }
            catch (RefreshRejectedException)
            {
                await store.SetStatusAsync(instance.Name, user, CredentialStatus.Expired, ct);
                log.LogWarning("The {Mode} refresh for {Instance} user {UserObjectId} was refused; marked expired", Mode, instance.Name, user);
                throw ConnectionRequired.Because(instance, "the stored token has expired and the source refused to refresh it", correlationId, connectUrl);
            }
            await store.SaveAsync(instance.Name, user, credential with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? credential.RefreshToken,
                ExpiresAt = refreshed.ExpiresAt,
            }, ct);
            log.LogInformation("Refreshed the {Mode} token for {Instance} user {UserObjectId}", Mode, instance.Name, user);
            return Channel(refreshed.AccessToken);
        }
    }

    private UserCredential Usable(ServiceInstance instance, UserCredential? credential, string correlationId) => credential switch
    {
        null => throw ConnectionRequired.Because(instance, "there is no credential for this user", correlationId, connectUrl),
        { Status: CredentialStatus.Expired } => throw ConnectionRequired.Because(instance, "the stored credential has expired", correlationId, connectUrl),
        { Status: CredentialStatus.Revoked } => throw ConnectionRequired.Because(instance, "the stored credential was revoked", correlationId, connectUrl),
        _ when credential.Kind != Kind || credential.AccessToken == null =>
            throw ConnectionRequired.Because(instance, $"the stored credential was connected for another mode ({credential.Kind})", correlationId, connectUrl),
        _ => credential,
    };

    private bool ExpiresSoon(UserCredential credential) => credential.ExpiresAt is not { } at || at - time.GetUtcNow() < RefreshWindow;

    private HttpChannel Channel(string accessToken)
    {
        var header = new AuthenticationHeaderValue("Bearer", accessToken);
        return new HttpChannel(http, request => request.Headers.Authorization = header, accessToken);
    }
}
