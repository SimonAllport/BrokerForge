using System;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin;

/// <summary>Where the host is and which service instance a virtual table belongs to.</summary>
public sealed class DataSourceSettings
{
    // Columns on the BrokerForge data source table (solution/). Names are provisional until the solution is built.
    public const string HostUrlColumn = "bf_hosturl";
    public const string HostScopeColumn = "bf_hostscope";
    public const string ServiceInstanceColumn = "bf_serviceinstance";
    public const string EnvironmentIdColumn = "bf_environmentid";

    public DataSourceSettings(Uri hostUrl, string hostScope, string serviceInstance, string environmentId)
    {
        HostUrl = hostUrl;
        HostScope = hostScope;
        ServiceInstance = serviceInstance;
        EnvironmentId = environmentId;
    }

    public Uri HostUrl { get; }

    /// <summary>Scope requested from the managed identity, e.g. "api://brokerforge-host/.default".</summary>
    public string HostScope { get; }

    public string ServiceInstance { get; }

    /// <summary>
    /// Phase 0: confirm the right source for the environment ID (data source column, organisation ID, or a token claim)
    /// and make the host check it against the token.
    /// </summary>
    public string EnvironmentId { get; }

    /// <summary>
    /// Reads the data source record bound to the current virtual table.
    /// UNVERIFIED (CLAUDE.md, phase 0): resolution of IEntityDataSourceRetrieverService from the plug-in's service provider.
    /// </summary>
    public static DataSourceSettings Resolve(IServiceProvider serviceProvider)
    {
        var retriever = serviceProvider.GetService(typeof(IEntityDataSourceRetrieverService)) as IEntityDataSourceRetrieverService
            ?? throw new BrokerConfigurationException("IEntityDataSourceRetrieverService is not available. Is this plug-in registered as the data provider for a virtual table?");

        var dataSource = retriever.RetrieveEntityDataSource()
            ?? throw new BrokerConfigurationException("The virtual table has no BrokerForge data source record.");

        return new DataSourceSettings(
            ReadUri(dataSource, HostUrlColumn),
            Read(dataSource, HostScopeColumn),
            Read(dataSource, ServiceInstanceColumn),
            Read(dataSource, EnvironmentIdColumn));
    }

    internal static string Read(Entity record, string column)
    {
        var value = record.GetAttributeValue<string>(column);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new BrokerConfigurationException("BrokerForge data source record has no value for " + column + ".");
        }

        return value;
    }

    internal static Uri ReadUri(Entity record, string column)
    {
        var text = Read(record, column);
        if (!text.EndsWith("/", StringComparison.Ordinal))
        {
            text += "/";
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new BrokerConfigurationException(column + " must be an absolute https URL.");
        }

        return uri;
    }
}

/// <summary>Host settings for a Custom API call, which has no virtual table data source to read.</summary>
public sealed class CustomApiSettings
{
    public CustomApiSettings(Uri hostUrl, string hostScope, string serviceInstance, string environmentId)
    {
        HostUrl = hostUrl;
        HostScope = hostScope;
        ServiceInstance = serviceInstance;
        EnvironmentId = environmentId;
    }

    public Uri HostUrl { get; }

    public string HostScope { get; }

    public string ServiceInstance { get; }

    public string EnvironmentId { get; }

    /// <summary>
    /// Open question (HLD 16): how a Custom API finds its host and service instance. Likely environment variables for the
    /// host URL and scope, plus a per-API service instance recorded by the generator. Decide before phase 1's Custom API work.
    /// </summary>
    public static CustomApiSettings Resolve(IServiceProvider serviceProvider, IPluginExecutionContext context) =>
        throw new BrokerConfigurationException(
            "Custom API routing is not decided yet: how '" + context.MessageName + "' finds its host and service instance (HLD 16).");
}
