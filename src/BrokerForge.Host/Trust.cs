namespace BrokerForge.Host;

/// <summary>Outcome of checking the caller. Reasons never echo the token.</summary>
public sealed record TrustDecision(bool Allowed, string Reason, string? EnvironmentId = null)
{
    public static TrustDecision Allow(string? environmentId = null) => new(true, "allowed", environmentId);

    public static TrustDecision Deny(string reason) => new(false, reason);
}

/// <summary>Plug-in to host trust (HLD 9.2). Runs before the body is read.</summary>
public interface IRequestTrust
{
    Task<TrustDecision> ValidateAsync(HttpRequest request, CancellationToken ct);
}

/// <summary>
/// Validates the plug-in's Power Platform managed identity token: issuer and tenant, audience, allowed app IDs,
/// and the calling environment. Phase 0 must confirm which claims the token carries (CLAUDE.md "Verify before
/// building on it"); until then this denies every call, so a misconfigured host fails closed.
/// </summary>
public sealed class ManagedIdentityRequestTrust(BrokerForgeOptions options) : IRequestTrust
{
    public Task<TrustDecision> ValidateAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(TrustDecision.Deny("missing bearer token"));
        }

        if (options.Trust.TenantId is null || options.Trust.Audience is null || options.Trust.AllowedAppIds.Count == 0)
        {
            return Task.FromResult(TrustDecision.Deny("trust is not configured (TenantId, Audience, AllowedAppIds)"));
        }

        // Phase 0: validate signature against the tenant's signing keys, iss, aud, appid/azp in AllowedAppIds,
        // expiry, and extract the environment ID claim if the token carries one. Record findings in docs/findings/phase0.md.
        return Task.FromResult(TrustDecision.Deny("managed identity token validation is not implemented yet (phase 0)"));
    }
}

/// <summary>Accepts every call. Registered only when ASPNETCORE_ENVIRONMENT=Development; the host refuses to start otherwise.</summary>
public sealed class DevelopmentRequestTrust : IRequestTrust
{
    public Task<TrustDecision> ValidateAsync(HttpRequest request, CancellationToken ct) => Task.FromResult(TrustDecision.Allow());
}
