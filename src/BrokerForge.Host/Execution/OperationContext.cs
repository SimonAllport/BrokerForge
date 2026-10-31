using BrokerForge.Abstractions;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Rendering;
using BrokerForge.Keys;

namespace BrokerForge.Host.Execution;

/// <summary>Everything resolved for one canonical request. The router builds it once per request.</summary>
public sealed record OperationContext(
    ServiceInstance Instance,
    ObjectMap Map,
    OperationDescriptor Descriptor,
    IKeyStrategy Keys,
    IAuthenticatedChannel Channel,
    BindingContext Binding);
