using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Contracts;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.DependencyInjection;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// A connection design never borrows the host's identity for an audience the host did not allow (HLD D-34, final
/// review C1): ServiceAccount and Impersonate designs need their scope in <c>Trust:AllowedCallers[i]:DesignScopes</c>,
/// and Azure management, Key Vault, Storage, Dataverse and Graph are refused whatever the list says.
/// </summary>
public partial class EnvironmentConfigTests
{
    private const string AllowedScope = "api://contoso-inventory/.default";

    /// <summary>Stands in for the host's managed identity; records every scope it is asked for.</summary>
    private sealed class RecordingCredential : TokenCredential
    {
        public List<string> Scopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext request, CancellationToken ct)
        {
            lock (Scopes)
                Scopes.AddRange(request.Scopes);
            return new AccessToken("test-only-host-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext request, CancellationToken ct) => new(GetToken(request, ct));
    }

    private readonly RecordingCredential _hostIdentity = new();

    private HostFactory ScopedHost(params string[] designScopesForA)
    {
        var edits = designScopesForA.Select((s, i) => ($"Trust:AllowedCallers:0:DesignScopes:{i}", (string?)s)).ToArray();
        return Host(edits);
    }

    private static ConnectionDesign ServiceAccountDesign(string scope) => Warehouse() with
    {
        Auth = new DesignAuth("ServiceAccount", null, null, null, null, scope, null, null, null, Array.Empty<string>()),
    };

    private ConnectionDesign ImpersonateDesign(params string[] scopes) => TicketsDesign() with
    {
        Auth = new DesignAuth("Impersonate", null, null, null, null, null, null, null,
            new DesignOAuth("https://login.contoso.example/authorize", "https://login.contoso.example/token", scopes, null, true), Array.Empty<string>()),
    };

    private static void AssertRefused((int Status, CanonicalResponse? Response, ErrorPayload? Error) result, params string[] parts)
    {
        Assert.Equal(500, result.Status);
        Assert.Equal("ConfigurationError", result.Error!.Error);
        foreach (var part in parts)
            Assert.Contains(part, result.Error.Message);
    }

    [Fact]
    public async Task DesignServiceAccountScopeNotAllowedFails()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", AllowedScope, EnvA, "DesignScopes");
        Assert.Empty(_hostIdentity.Scopes);
        Assert.Equal(0, _sent.Value);
    }

    [Theory]
    [InlineData("https://vault.azure.net/.default")]
    [InlineData("https://contoso-kv.vault.azure.net/.default")]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://management.core.windows.net//.default")]
    [InlineData("https://contosostore.blob.core.windows.net/.default")]
    [InlineData("https://storage.azure.com/user_impersonation")]
    [InlineData("https://contoso.crm4.dynamics.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("00000003-0000-0000-c000-000000000000/.default")]
    [InlineData("cfa8b339-82a2-471a-a3c9-0fc0be7a4093/.default")]
    public async Task DesignServiceAccountKeyVaultScopeAlwaysRefused(string reserved)
    {
        _designs.Add(EnvA, ServiceAccountDesign(reserved), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", reserved, EnvA, "never");
        Assert.Empty(_hostIdentity.Scopes);
        Assert.Equal(0, _sent.Value);
    }

    [Fact]
    public async Task DesignServiceAccountReservedScopeThroughOverrideRefused()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl, ("scope", "https://vault.azure.net/.default")));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        AssertRefused(result, "WarehouseAPI", "https://vault.azure.net/.default", "never");
        Assert.Empty(_hostIdentity.Scopes);
    }

    [Fact]
    public async Task DesignServiceAccountAllowedScopeWorks()
    {
        _designs.Add(EnvA, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvA));
        // The Inventory mock wants its API key, so it refuses the bearer token: what matters is that the call was made.
        Assert.NotEqual("ConfigurationError", result.Error?.Error);
        Assert.Equal(new[] { AllowedScope }, _hostIdentity.Scopes);
        Assert.Equal(1, _sent.Value);
    }

    [Fact]
    public async Task DesignScopesForOneEnvironmentDoNotAllowAnother()
    {
        _designs.Add(EnvB, ServiceAccountDesign(AllowedScope), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_item", "WarehouseAPI", EnvB));
        AssertRefused(result, "WarehouseAPI", AllowedScope, EnvB);
        Assert.Empty(_hostIdentity.Scopes);
    }

    [Fact]
    public async Task DesignImpersonateScopeNotAllowedFails()
    {
        _designs.Add(EnvA, ImpersonateDesign(Tickets.EntraAudience + "/user_impersonation", "offline_access"), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", Tickets.EntraAudience + "/user_impersonation", EnvA, "DesignScopes");
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/User.Read")]
    [InlineData("User.Read")]
    [InlineData("https://vault.azure.net/user_impersonation")]
    public async Task DesignImpersonateReservedScopeAlwaysRefused(string reserved)
    {
        _designs.Add(EnvA, ImpersonateDesign(reserved), At(mock.BaseUrl));
        var result = await Run(ScopedHost(Tickets.EntraAudience + "/user_impersonation"), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", reserved, "never");
    }

    [Fact]
    public async Task DesignImpersonateWithOnlySignInScopesRefused()
    {
        // openid and offline_access alone still give a Microsoft Graph access token.
        _designs.Add(EnvA, ImpersonateDesign("openid", "offline_access"), At(mock.BaseUrl));
        var result = await Run(ScopedHost(AllowedScope), List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        AssertRefused(result, "TicketsAPI", "Microsoft Graph");
    }

    [Fact]
    public async Task DesignImpersonateAllowedScopeWorks()
    {
        var scope = Tickets.EntraAudience + "/user_impersonation";
        _designs.Add(EnvA, ImpersonateDesign(scope, "offline_access"), At(mock.BaseUrl));
        var host = ScopedHost(scope);
        await host.Services.GetRequiredService<IUserCredentialStore>().SaveAsync($"{Guid.Parse(EnvA):N}_TicketsAPI", Tickets.AliceOid,
            new UserCredential(CredentialKind.Impersonate, CredentialStatus.Connected, Tickets.EntraToken(Tickets.AliceOid), "entra-rt-unused",
                DateTimeOffset.UtcNow.AddHours(1), null, null, DateTimeOffset.UtcNow), default);
        var (status, response, error) = await Run(host, List("ext_ticket", "TicketsAPI", EnvA, Tickets.AliceOid));
        Assert.True(status == 200, error?.Message);
        Assert.NotEmpty(response!.Rows);
    }

    [Theory]
    [InlineData("https://vault.azure.net/.default")]
    [InlineData("https://management.azure.com/.default")]
    [InlineData("https://contoso.crm.dynamics.com/.default")]
    [InlineData("https://graph.microsoft.com/.default")]
    [InlineData("https://contosostore.table.core.windows.net/.default")]
    public void DesignScopesListWithReservedAudienceRejectedAtStartup(string reserved)
    {
        var trust = new TrustOptions
        {
            TenantId = HostFactory.Tenant,
            Audience = HostFactory.Audience,
            AllowedCallers = { new AllowedCaller { ClientId = HostFactory.PluginApp, EnvironmentId = EnvA, DesignScopes = { AllowedScope, reserved } } },
        };
        var problem = Assert.Single(trust.Problems());
        Assert.Contains($"Trust:AllowedCallers entry for environment {EnvA}: DesignScopes lists {reserved}", problem);

        using var host = new HostFactory(mock.BaseUrl, extra: new Dictionary<string, string?> { ["Trust:AllowedCallers:0:DesignScopes:0"] = reserved });
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains($"DesignScopes lists {reserved}", ex.Message);
    }
}
