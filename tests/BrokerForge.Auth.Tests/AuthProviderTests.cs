using System.Net;
using System.Text;
using Azure.Core;
using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Contracts;

namespace BrokerForge.Auth.Tests;

public class AuthProviderTests
{
    private const string Dummy = "bf-test-dummy-secret";
    private static readonly CallerContext Caller = new() { UserObjectId = "u1" };

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class FixedSecrets(string? value) : ISecretResolver
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) =>
            value != null ? Task.FromResult(value) : throw new CredentialUnavailableException($"secret {reference} is not available");
    }

    private sealed class StubCredential(string token, bool fail = false) : TokenCredential
    {
        public string[]? Scopes { get; private set; }
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken ct) => GetTokenAsync(context, ct).Result;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken ct)
        {
            Scopes = context.Scopes;
            if (fail)
                throw new Azure.Identity.AuthenticationFailedException("no identity");
            return ValueTask.FromResult(new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private static ServiceInstance Instance(AuthConfig auth) =>
        new("OrdersAPI", "rest", "https://orders.example", auth, new Dictionary<string, string>(), new Dictionary<string, string>());

    private static (StaticAuthProvider Provider, Capture Capture) Static(string? secret)
    {
        var capture = new Capture();
        return (new StaticAuthProvider(new FixedSecrets(secret), new HttpClient(capture)), capture);
    }

    private static async Task<HttpRequestMessage> SendVia(IAuthProvider provider, Capture capture, ServiceInstance instance, string url = "https://orders.example/v2/orders?limit=5")
    {
        var channel = await provider.AuthenticateAsync(instance, Caller, "corr-1", CancellationToken.None);
        await channel.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), CancellationToken.None);
        return capture.Last!;
    }

    [Fact]
    public async Task Static_ApiKeyHeader_AttachesHeader()
    {
        var (provider, capture) = Static(Dummy);
        var request = await SendVia(provider, capture, Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:K")));
        Assert.Equal(Dummy, request.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal(AuthMode.Static, provider.Mode);
    }

    [Fact]
    public async Task Static_ApiKeyQuery_AppendsParam()
    {
        var (provider, capture) = Static("a&b c");
        var request = await SendVia(provider, capture, Instance(new AuthConfig(AuthMode.Static, "apiKeyQuery", QueryParam: "api_key", SecretRef: "env:K")));
        Assert.Equal("https://orders.example/v2/orders?limit=5&api_key=a%26b%20c", request.RequestUri!.AbsoluteUri);
        var noQuery = await SendVia(provider, capture, Instance(new AuthConfig(AuthMode.Static, "apiKeyQuery", QueryParam: "api_key", SecretRef: "env:K")), "https://orders.example/v2/orders");
        Assert.Equal("https://orders.example/v2/orders?api_key=a%26b%20c", noQuery.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Static_Basic_EncodesCredentials()
    {
        var (provider, capture) = Static(Dummy);
        var request = await SendVia(provider, capture, Instance(new AuthConfig(AuthMode.Static, "basic", Username: "svc", SecretRef: "env:K")));
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("svc:" + Dummy, Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
    }

    [Fact]
    public async Task Static_MissingSecret_ThrowsNamingRefNotValue()
    {
        var (provider, _) = Static(null);
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:ORDERS_KEY")), Caller, "c", CancellationToken.None));
        Assert.Contains("env:ORDERS_KEY", ex.Message);
    }

    [Fact]
    public async Task Static_NoSecretRef_Throws()
    {
        var (provider, _) = Static(Dummy);
        await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key")), Caller, "c", CancellationToken.None));
    }

    [Fact]
    public async Task Channels_ExposeCredentialValuesForRedaction()
    {
        var (provider, _) = Static(Dummy);
        foreach (var kind in new[] { "apiKeyHeader", "apiKeyQuery", "basic" })
        {
            var channel = await provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, kind, HeaderName: "X-Api-Key", QueryParam: "k", Username: "svc", SecretRef: "env:K")), Caller, "c", CancellationToken.None);
            Assert.Contains(Dummy, channel.CredentialValues);
        }
        var bearer = await new ServiceAccountAuthProvider(new StubCredential("tok-xyz"), new HttpClient(new Capture()))
            .AuthenticateAsync(Instance(new AuthConfig(AuthMode.ServiceAccount, Scope: "s")), Caller, "c", CancellationToken.None);
        Assert.Contains("tok-xyz", bearer.CredentialValues);
    }

    [Fact]
    public async Task ServiceAccount_AttachesBearerFromCredential()
    {
        var capture = new Capture();
        var credential = new StubCredential("tok-123");
        var provider = new ServiceAccountAuthProvider(credential, new HttpClient(capture));
        var request = await SendVia(provider, capture, Instance(new AuthConfig(AuthMode.ServiceAccount, Scope: "api://orders/.default")));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok-123", request.Headers.Authorization.Parameter);
        Assert.Equal(new[] { "api://orders/.default" }, credential.Scopes);
    }

    [Fact]
    public async Task ServiceAccount_CredentialFails_ThrowsNamingScope()
    {
        var provider = new ServiceAccountAuthProvider(new StubCredential("x", fail: true), new HttpClient(new Capture()));
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() =>
            provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.ServiceAccount, Scope: "api://orders/.default")), Caller, "c", CancellationToken.None));
        Assert.Contains("api://orders/.default", ex.Message);
    }

    [Fact]
    public async Task Providers_RejectOtherModes()
    {
        var (provider, _) = Static(Dummy);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.ServiceAccount, Scope: "s")), Caller, "c", CancellationToken.None));
    }

    [Fact]
    public async Task EnvScheme_NotAllowed_Throws()
    {
        var router = new SecretResolverRouter(new FixedSecrets("kv-value"), new EnvironmentSecretResolver(), allowEnvironment: false);
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() => router.GetAsync(SecretRef.Parse("env:ANYTHING"), CancellationToken.None));
        Assert.Contains("Secrets:AllowEnvironment", ex.Message);
        Assert.Equal("kv-value", await router.GetAsync(SecretRef.Parse("kv:https://v.vault.azure.net/secrets/s"), CancellationToken.None));
    }

    [Fact]
    public async Task EnvScheme_Allowed_ReadsVariable()
    {
        Environment.SetEnvironmentVariable("BF_AUTH_TEST_KEY", Dummy);
        var router = new SecretResolverRouter(new FixedSecrets(null), new EnvironmentSecretResolver(), allowEnvironment: true);
        Assert.Equal(Dummy, await router.GetAsync(SecretRef.Parse("env:BF_AUTH_TEST_KEY"), CancellationToken.None));
        var ex = await Assert.ThrowsAsync<CredentialUnavailableException>(() => router.GetAsync(SecretRef.Parse("env:BF_AUTH_TEST_MISSING"), CancellationToken.None));
        Assert.Contains("BF_AUTH_TEST_MISSING", ex.Message);
    }

    [Fact]
    public void AuthHeaders_ForStaticHeaderMode_ReservesHeader()
    {
        var reserved = AuthHeaders.For(Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:K")));
        Assert.True(reserved.Contains("x-api-key"));
        Assert.True(reserved.Contains("Authorization"));
        Assert.False(AuthHeaders.For(Instance(new AuthConfig(AuthMode.ServiceAccount, Scope: "s"))).Contains("X-Api-Key"));
    }

    [Fact]
    public async Task ExceptionMessages_NeverContainSecretValue()
    {
        var failures = new List<Exception>();
        async Task Collect(Func<Task> action)
        {
            try { await action(); } catch (Exception ex) { failures.Add(ex); }
        }
        var (provider, _) = Static(Dummy);
        await Collect(() => provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", SecretRef: "env:K")), Caller, "c", CancellationToken.None));
        await Collect(() => provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "apiKeyQuery", SecretRef: "env:K")), Caller, "c", CancellationToken.None));
        await Collect(() => provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "basic", SecretRef: "env:K")), Caller, "c", CancellationToken.None));
        await Collect(() => provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "bogus", SecretRef: "env:K")), Caller, "c", CancellationToken.None));
        var colon = Static("a:b");
        await Collect(() => colon.Provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "basic", Username: "svc:x", SecretRef: "env:K")), Caller, "c", CancellationToken.None));
        var newline = Static("bad\r\nvalue " + Dummy);
        await Collect(async () =>
        {
            var ch = await newline.Provider.AuthenticateAsync(Instance(new AuthConfig(AuthMode.Static, "apiKeyHeader", HeaderName: "X-Api-Key", SecretRef: "env:K")), Caller, "c", CancellationToken.None);
            await ch.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://orders.example/"), CancellationToken.None);
        });
        Environment.SetEnvironmentVariable("BF_AUTH_TEST_KEY", Dummy);
        await Collect(() => new SecretResolverRouter(new FixedSecrets(Dummy), new EnvironmentSecretResolver(), false).GetAsync(SecretRef.Parse("env:BF_AUTH_TEST_KEY"), CancellationToken.None));

        Assert.True(failures.Count >= 6, $"only {failures.Count} failure paths ran");
        Assert.All(failures, ex => Assert.DoesNotContain(Dummy, ex.ToString()));
    }
}
