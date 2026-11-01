using Azure.Core;
using Azure.Data.Tables;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Host.Config;

/// <summary>
/// The per-user credential store (HLD D-25): the Azure table at Credentials:TableEndpoint. A host with a per-user
/// instance and no table refuses to start; the in-memory store is only for tests and local runs that ask for it
/// (Credentials:InMemory), because it is lost on restart and not shared between replicas.
/// </summary>
public static class CredentialStores
{
    public const string DefaultTable = "bfusercredentials";

    public static IUserCredentialStore Create(IConfiguration configuration, HostConfigStore config, TokenCredential credential, IDataProtectionProvider protection)
    {
        var endpoint = configuration["Credentials:TableEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new ConfigurationException("Credentials:TableEndpoint must be the storage account's https table endpoint.");
            return new TableUserCredentialStore(new TableClient(uri, configuration["Credentials:TableName"] ?? DefaultTable, credential), protection);
        }
        var perUser = config.PerUserInstances;
        if (perUser.Count > 0 && !configuration.GetValue("Credentials:InMemory", false))
            throw new ConfigurationException(
                $"Credentials:TableEndpoint is required: {string.Join(", ", perUser)} use per-user modes, and their credentials need a durable store.");
        return new InMemoryUserCredentialStore();
    }
}
