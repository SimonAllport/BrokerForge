using BrokerForge.Abstractions;

namespace BrokerForge.Contract.Tests;

/// <summary>A per-user instance needs a durable credential store; there is no silent in-memory default (HLD D-25).</summary>
[Collection(MockApiCollection.Name)]
public class CredentialStoreSetupTests(MockApiFixture mock)
{
    private static Dictionary<string, string?> PerUser(params (string, string?)[] more)
    {
        var s = new Dictionary<string, string?>
        {
            ["Connect:PublicBaseUrl"] = "https://bf-host.example",
            ["Connect:ClientId"] = "connect-test-client",
            ["Instances:SsoAPI:BrokerType"] = "rest",
            ["Instances:SsoAPI:BaseUrl"] = "https://sso.example",
            ["Instances:SsoAPI:Auth:Mode"] = "Sso",
            ["Instances:SsoAPI:Auth:SsoKind"] = "basic",
        };
        foreach (var (k, v) in more)
            s[k] = v;
        return s;
    }

    [Fact]
    public void PerUserInstance_NoCredentialStore_StartupFails()
    {
        using var host = new HostFactory(mock.BaseUrl, extra: PerUser());
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains("Credentials:TableEndpoint", ex.Message);
        Assert.Contains("SsoAPI", ex.Message);
    }

    [Fact]
    public void TableStore_WithoutPersistedKeys_StartupFails()
    {
        using var host = new HostFactory(mock.BaseUrl, extra: PerUser(("Credentials:TableEndpoint", "https://acct.table.core.windows.net")));
        var ex = Assert.Throws<ConfigurationException>(() => host.CreateClient());
        Assert.Contains("DataProtection:BlobUri", ex.Message);
    }

    [Fact]
    public void PerUserInstance_InMemoryStoreAllowed_Starts()
    {
        using var host = new HostFactory(mock.BaseUrl, extra: PerUser(("Credentials:InMemory", "true")));
        Assert.NotNull(host.CreateClient());
    }

    [Fact]
    public void NoPerUserInstance_NoCredentialStore_Starts()
    {
        using var host = new HostFactory(mock.BaseUrl);
        Assert.NotNull(host.CreateClient());
    }
}
