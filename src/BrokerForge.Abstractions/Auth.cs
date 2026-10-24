using BrokerForge.Contracts;

namespace BrokerForge.Abstractions;

/// <summary>
/// One provider per authentication mode (HLD 7.3). A per-user mode with no credential throws
/// <see cref="ConnectionRequiredException"/>. No provider falls back to another mode.
/// </summary>
public interface IAuthProvider
{
    AuthMode Mode { get; }

    Task<IAuthenticatedChannel> AuthenticateAsync(ServiceInstance instance, CallerContext caller, CancellationToken ct);
}

/// <summary>
/// Attaches the credential for one call. HTTP adapters call <see cref="ApplyAsync"/>.
/// Non-HTTP adapters (SQL, phase 4) will need a token accessor here; add it then, not speculatively.
/// </summary>
public interface IAuthenticatedChannel
{
    AuthMode Mode { get; }

    /// <summary>Header names this channel sets. Descriptors may not declare them (descriptor spec rule 4).</summary>
    IReadOnlyCollection<string> ReservedHeaders { get; }

    Task ApplyAsync(HttpRequestMessage request, CancellationToken ct);
}
