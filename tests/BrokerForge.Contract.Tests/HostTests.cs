using System.Net;
using System.Text;
using BrokerForge.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace BrokerForge.Contract.Tests;

/// <summary>
/// The host end to end, up to the phase 1 boundary. Uses appsettings.Development.json (OrdersAPI, local-dev environment,
/// development trust). Calls never reach the mock API yet: the renderer is a stub.
/// </summary>
[Collection("Host")]
public class HostTests : IClassFixture<HostTests.DevelopmentHost>
{
    private readonly HttpClient _client;

    public HostTests(DevelopmentHost host) => _client = host.CreateClient();

    [Fact]
    public async Task Health_is_ok()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Malformed_body_is_400()
    {
        var response = await _client.PostAsync("/v1/execute", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_service_instance_is_a_configuration_error()
    {
        var response = await Execute(List("NoSuchApi"));
        Assert.Equal(ErrorCode.Configuration, response.Error!.Code);
        Assert.Equal("corr-test", response.Error.CorrelationId);
    }

    [Fact]
    public async Task Environment_not_allowed_is_rejected()
    {
        var request = List();
        request.EnvironmentId = "someone-else";
        var response = await Execute(request);
        Assert.Equal(ErrorCode.Configuration, response.Error!.Code);
        Assert.Contains("not allowed", response.Error.Message);
    }

    [Fact]
    public async Task Undeclared_filter_is_rejected_before_the_source_is_called()
    {
        var request = List();
        request.Query = new QuerySpec
        {
            Filter = new FilterGroup { Conditions = { new FilterCondition { Column = "ext_name", Operator = "eq", Values = { "ORD-0001" } } } },
        };

        var response = await Execute(request);
        Assert.Equal(ErrorCode.UnsupportedQuery, response.Error!.Code);
        Assert.Equal("ext_name", response.Error.Column);
    }

    [Fact]
    public async Task Guid_not_produced_by_the_key_strategy_is_not_found()
    {
        var request = new CanonicalRequest
        {
            CorrelationId = "corr-test",
            EnvironmentId = "local-dev",
            ServiceInstance = "OrdersAPI",
            Table = "ext_order",
            Message = BrokerMessage.Retrieve,
            Id = Guid.NewGuid().ToString("D"),
        };

        var response = await Execute(request);
        Assert.Equal(ErrorCode.NotFound, response.Error!.Code);
    }

    [Fact]
    public async Task Declared_query_reaches_the_phase_1_renderer_stub()
    {
        Environment.SetEnvironmentVariable("ORDERS_API_KEY", "dev-key");
        var request = List();
        request.Query = new QuerySpec
        {
            Filter = new FilterGroup { Conditions = { new FilterCondition { Column = "ext_status", Operator = "eq", Values = { "100000000" } } } },
            Page = new PageSpec { Size = 10 },
        };

        var response = await Execute(request);
        Assert.Equal(ErrorCode.Configuration, response.Error!.Code);
        Assert.Contains("Phase 1", response.Error.Message);
    }

    // HLD 11 rows that need the phase 1 renderer and mapper, or phase 3 auth. Unskip as each lands.
    [Fact(Skip = "Phase 1: needs the renderer and the mock API (/v2/fail/500).")]
    public void Unmapped_500_is_SourceException_with_correlation_id() { }

    [Fact(Skip = "Phase 1: needs the renderer and the mock API (/v2/slow).")]
    public void Slow_source_is_SourceTimeoutException() { }

    [Fact(Skip = "Phase 1: needs the response mapper and the mock API (/v2/fail/malformed).")]
    public void Response_shape_mismatch_is_MappingException() { }

    [Fact(Skip = "Phase 3: per-user modes and the connect page.")]
    public void Missing_user_credential_is_ConnectionRequired_with_connect_url_and_never_falls_back() { }

    private async Task<CanonicalResponse> Execute(CanonicalRequest request)
    {
        var http = await _client.PostAsync("/v1/execute", new StringContent(CanonicalJson.Serialize(request), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, http.StatusCode);
        return CanonicalJson.Deserialize<CanonicalResponse>(await http.Content.ReadAsStringAsync());
    }

    private static CanonicalRequest List(string instance = "OrdersAPI") => new()
    {
        CorrelationId = "corr-test",
        EnvironmentId = "local-dev",
        ServiceInstance = instance,
        Table = "ext_order",
        Message = BrokerMessage.RetrieveMultiple,
        Query = new QuerySpec(),
    };

    public sealed class DevelopmentHost : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
    }
}

/// <summary>
/// Outside Development the host fails closed. Settings go in as environment variables because they must be visible
/// when Program reads configuration; the "Host" collection keeps these tests from running alongside each other.
/// </summary>
[Collection("Host")]
public class ProductionTrustTests
{
    [Fact]
    public async Task Managed_identity_trust_rejects_calls_until_phase_0()
    {
        using var env = new EnvironmentScope(("BrokerForge__DescriptorRoot", "../../descriptors"));
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var response = await host.CreateClient().PostAsync("/v1/execute", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Development_trust_refuses_to_start_outside_Development()
    {
        using var env = new EnvironmentScope(("BrokerForge__DescriptorRoot", "../../descriptors"), ("BrokerForge__Trust__Mode", "Development"));
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Production"));

        var ex = Assert.ThrowsAny<Exception>(() => host.CreateClient());
        Assert.Contains("only allowed when ASPNETCORE_ENVIRONMENT=Development", ex.ToString());
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string[] _names;

        public EnvironmentScope(params (string Name, string Value)[] variables)
        {
            _names = variables.Select(v => v.Name).ToArray();
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var name in _names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }
    }
}

[CollectionDefinition("Host", DisableParallelization = true)]
public class HostCollection
{
}
