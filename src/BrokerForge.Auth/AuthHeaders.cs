using BrokerForge.Abstractions;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Auth;

/// <summary>The headers an instance's auth mode sets, which descriptors may not declare (descriptor spec 4).</summary>
public static class AuthHeaders
{
    public static ReservedHeaders For(ServiceInstance instance) =>
        ReservedHeaders.For(instance.Auth is { Mode: AuthMode.Static, StaticKind: "apiKeyHeader", HeaderName: { } header }
            ? new[] { header }
            : Array.Empty<string>());
}
