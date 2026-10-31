using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using BrokerForge.Abstractions;
using BrokerForge.Adapters.Rest;
using BrokerForge.Auth;
using BrokerForge.Host.Config;
using BrokerForge.Host.Endpoints;
using BrokerForge.Host.Ingress;
using BrokerForge.Host.Routing;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// BrokerForge host (HLD 5.2): validates the plug-in's managed identity token, routes the canonical
// request to its descriptor and adapter, and replies with rows, a record, an id or a typed error.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);

builder.Services.Configure<TrustOptions>(builder.Configuration.GetSection("Trust"));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
// Configured from TrustOptions when first used, so the values come from the final configuration.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<TrustOptions>>((options, trustOptions) =>
    {
        var trust = trustOptions.Value;
        options.Authority = $"https://login.microsoftonline.com/{trust.TenantId}/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences = [trust.Audience],
            // Entra issues v1 or v2 tokens depending on the resource app's accessTokenAcceptedVersion.
            ValidIssuers =
            [
                $"https://sts.windows.net/{trust.TenantId}/",
                $"https://login.microsoftonline.com/{trust.TenantId}/v2.0",
            ],
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                if (ctx.Principal!.FindFirst("tid")?.Value != trust.TenantId)
                    ctx.Fail($"Token tenant is not {trust.TenantId}.");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("BrokerForge.Ingress")
                    .LogWarning("Token rejected: {Reason}", ctx.Exception.Message);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);

builder.Services.AddHttpClient("sources", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false });
builder.Services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
builder.Services.AddSingleton<ISecretResolver>(sp => new SecretResolverRouter(
    new KeyVaultSecretResolver(sp.GetRequiredService<TokenCredential>()),
    new EnvironmentSecretResolver(),
    builder.Configuration.GetValue("Secrets:AllowEnvironment", false)));
builder.Services.AddSingleton<IAuthProvider>(sp => new StaticAuthProvider(sp.GetRequiredService<ISecretResolver>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources")));
builder.Services.AddSingleton<IAuthProvider>(sp => new ServiceAccountAuthProvider(sp.GetRequiredService<TokenCredential>(), sp.GetRequiredService<IHttpClientFactory>().CreateClient("sources")));
builder.Services.AddSingleton<IBroker, RestBroker>();
builder.Services.AddSingleton<HostConfigStore>();
builder.Services.AddSingleton<CallerGuard>();
builder.Services.AddSingleton<RequestRouter>();

var app = builder.Build();

// Validate trust and load every descriptor before taking traffic; any problem stops the host here.
var trustProblems = app.Services.GetRequiredService<IOptions<TrustOptions>>().Value.Problems().ToList();
if (trustProblems.Count > 0)
    throw new InvalidOperationException("Host configuration is invalid:" + string.Concat(trustProblems.Select(p => "\n - " + p)));
var store = app.Services.GetRequiredService<HostConfigStore>();
app.Logger.LogInformation("Loaded descriptors: {Descriptors}", string.Join(", ", store.DescriptorNames));

app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok("ok"));
ExecuteEndpoint.Map(app);
app.Run();

public partial class Program;
