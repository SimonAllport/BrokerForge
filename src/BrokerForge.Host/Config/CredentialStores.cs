using Azure.Core;
using Azure.Data.Tables;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Host.Config;

/// <summary>
/// The per-user credential store (HLD D-25): the Azure table at Credentials:TableEndpoint. A host with a per-user
/// instance, or with connection designs (any of which may be per-user), and no table refuses to start; the in-memory store is only for tests and local runs that ask for it
/// (Credentials:InMemory), because it is lost on restart and not shared between replicas.
/// </summary>
public static class CredentialStores
{
    public const string DefaultTable = "bfusercredentials";

    public static IUserCredentialStore Create(IConfiguration configuration, HostConfigStore files, bool designs, TokenCredential credential, IDataProtectionProvider protection)
    {
        var endpoint = configuration["Credentials:TableEndpoint"];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                throw new ConfigurationException("Credentials:TableEndpoint must be the storage account's https table endpoint.");
            if (string.IsNullOrWhiteSpace(configuration["DataProtection:BlobUri"]) || string.IsNullOrWhiteSpace(configuration["DataProtection:KeyId"]))
                throw new ConfigurationException("DataProtection:BlobUri and DataProtection:KeyId are required with Credentials:TableEndpoint: without persisted keys, stored credentials cannot be read after a restart.");
            return new TableUserCredentialStore(new TableClient(uri, configuration["Credentials:TableName"] ?? DefaultTable, credential), protection);
        }
        if (configuration.GetValue("Credentials:InMemory", false))
            return new InMemoryUserCredentialStore();
        var perUser = files.PerUserInstances;
        if (perUser.Count > 0)
            throw new ConfigurationException(
                $"Credentials:TableEndpoint is required: {string.Join(", ", perUser)} use per-user modes, and their credentials need a durable store.");
        if (designs)
            throw new ConfigurationException("Credentials:TableEndpoint is required: connection designs can use per-user modes, and their credentials need a durable store.");
        return new InMemoryUserCredentialStore();
    }
}
