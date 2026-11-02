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

        // The gate stops two calls on this replica refreshing at once. Across replicas, and against the user's
        // own disconnect or reconnect, every write below is conditional on the row version read here.
        using (await _gate.EnterAsync(instance.Name, user, ct))
        {
            var row = await store.GetVersionedAsync(instance.Name, user, ct);
            credential = Usable(instance, row?.Credential, correlationId);
            if (!ExpiresSoon(credential))
                return Channel(credential.AccessToken!);
            if (credential.RefreshToken == null)
            {
                await store.SetStatusIfUnchangedAsync(instance.Name, user, CredentialStatus.Expired, row!.Version, ct);
                throw ConnectionRequired.Because(instance, "the stored token has expired and cannot be refreshed", correlationId, connectUrl);
            }

            TokenSet refreshed;
            try
            {
                refreshed = await tokens.RefreshAsync(resources(instance.Auth.OAuthResource!), credential.RefreshToken, ct);
            }
            catch (RefreshRejectedException)
            {
                // Refused because another replica used this refresh token first, or the user reconnected meanwhile:
                // the row then holds a newer grant, and that grant is the answer.
                if (!await store.SetStatusIfUnchangedAsync(instance.Name, user, CredentialStatus.Expired, row!.Version, ct)
                    && await store.GetAsync(instance.Name, user, ct) is { } current && current.RefreshToken != credential.RefreshToken && Fresh(current))
                    return Channel(current.AccessToken!);
                log.LogWarning("The {Mode} refresh for {Instance} user {UserObjectId} was refused; marked expired", Mode, instance.Name, user);
                throw ConnectionRequired.Because(instance, "the stored token has expired and the source refused to refresh it", correlationId, connectUrl);
            }

            var updated = credential with
            {
                Status = CredentialStatus.Connected,
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? credential.RefreshToken,
                ExpiresAt = refreshed.ExpiresAt,
            };
            var version = row!.Version;
            // Two attempts: the second covers a racing replica that marked this same grant expired after its
            // refresh of the same token was refused (only the status changed, so the refreshed grant still stands).
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (await store.ReplaceIfUnchangedAsync(instance.Name, user, updated, version, ct))
                {
                    log.LogInformation("Refreshed the {Mode} token for {Instance} user {UserObjectId}", Mode, instance.Name, user);
                    return Channel(refreshed.AccessToken);
                }
                var now = await store.GetVersionedAsync(instance.Name, user, ct);
                if (now == null)
                    throw ConnectionRequired.Because(instance, "the user disconnected while the token was being refreshed", correlationId, connectUrl);
                if (now.Credential.RefreshToken != credential.RefreshToken)
                {
                    // The user reconnected, or another replica refreshed: keep that grant and discard this one.
                    if (Fresh(now.Credential))
                        return Channel(now.Credential.AccessToken!);
                    break;
                }
                version = now.Version;
            }
            throw ConnectionRequired.Because(instance, "the stored credential changed while it was being refreshed", correlationId, connectUrl);
        }
    }

    private bool Fresh(UserCredential credential) =>
        credential is { Status: CredentialStatus.Connected, AccessToken: not null } && credential.Kind == Kind && !ExpiresSoon(credential);

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
