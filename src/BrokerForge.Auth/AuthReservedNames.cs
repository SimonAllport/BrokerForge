using BrokerForge.Abstractions;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Auth;

/// <summary>The headers and query parameters an instance's auth mode sets, which descriptors may not declare (descriptor spec 4).</summary>
public static class AuthReservedNames
{
    public static ReservedNames For(ServiceInstance instance) => instance.Auth switch
    {
        { Mode: AuthMode.Static, StaticKind: "apiKeyHeader", HeaderName: { } header } => ReservedNames.For(new[] { header }),
        { Mode: AuthMode.Static, StaticKind: "apiKeyQuery", QueryParam: { } param } => ReservedNames.For(Array.Empty<string>(), new[] { param }),
        _ => ReservedNames.For(Array.Empty<string>()),
    };
}
