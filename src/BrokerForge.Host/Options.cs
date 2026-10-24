using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Host;

/// <summary>Bound from the "BrokerForge" configuration section.</summary>
public sealed class BrokerForgeOptions
{
    /// <summary>Folder holding descriptors/&lt;ServiceInstance&gt;/*.yaml, relative to the content root. Phase 1 moves to bf_operation.</summary>
    public string DescriptorRoot { get; set; } = "descriptors";

    /// <summary>Base URL of this host's connect page, used in ConnectionRequired errors (phase 3).</summary>
    public string? ConnectBaseUrl { get; set; }

    public TrustOptions Trust { get; set; } = new();

    /// <summary>Local stand-in for bf_serviceinstance rows until the Dataverse store exists (phase 1).</summary>
    public List<ServiceInstanceOptions> ServiceInstances { get; set; } = new();

    /// <summary>Local stand-in for bf_objectmap rows.</summary>
    public List<ObjectMapOptions> ObjectMaps { get; set; } = new();

    /// <summary>Custom API unique name → operation, per service instance.</summary>
    public List<CustomApiOptions> CustomApis { get; set; } = new();
}

public sealed class TrustOptions
{
    /// <summary>"ManagedIdentity" (default, fails closed until phase 0) or "Development" (only when ASPNETCORE_ENVIRONMENT=Development).</summary>
    public string Mode { get; set; } = "ManagedIdentity";

    public string? TenantId { get; set; }

    public string? Audience { get; set; }

    /// <summary>Application (client) IDs of the plug-in's Power Platform managed identity.</summary>
    public List<string> AllowedAppIds { get; set; } = new();
}

public sealed class ServiceInstanceOptions
{
    public string Name { get; set; } = "";
    public string BrokerType { get; set; } = "rest";
    public string BaseUrl { get; set; } = "";
    public AuthMode AuthMode { get; set; }
    public Dictionary<string, string> SecretReferences { get; set; } = new();
    public Dictionary<string, string> Config { get; set; } = new();
    public string? OAuthResource { get; set; }
    public AsyncContextRule AsyncContextRule { get; set; } = AsyncContextRule.RequireUserCredential;
    public List<string> AllowedEnvironments { get; set; } = new();

    public ServiceInstance ToModel() => new()
    {
        Name = Name,
        BrokerType = BrokerType,
        BaseUrl = Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            ? uri
            : throw new BrokerConfigurationException($"Service instance '{Name}' has no valid BaseUrl."),
        AuthMode = AuthMode,
        SecretReferences = SecretReferences,
        Config = Config,
        OAuthResource = OAuthResource,
        AsyncContextRule = AsyncContextRule,
        AllowedEnvironments = AllowedEnvironments,
    };
}

public sealed class ObjectMapOptions
{
    public string ServiceInstance { get; set; } = "";
    public string Table { get; set; } = "";
    public KeyStrategy KeyStrategy { get; set; } = KeyStrategy.Reversible;
    public Dictionary<BrokerMessage, string> Operations { get; set; } = new();

    public ObjectMap ToModel() => new()
    {
        ServiceInstance = ServiceInstance,
        Table = Table,
        KeyStrategy = KeyStrategy,
        Operations = Operations,
    };
}

public sealed class CustomApiOptions
{
    public string ServiceInstance { get; set; } = "";
    public string UniqueName { get; set; } = "";
    public string Operation { get; set; } = "";
}
