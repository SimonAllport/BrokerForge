using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

// Phase 0 stub host. Validates the plug-in's Power Platform managed identity token and
// echoes its non-secret claims so phase 0 can record exactly what the token carries.
var builder = WebApplication.CreateBuilder(args);

var trust = builder.Configuration.GetSection("Trust");
var tenantId = Require(trust, "TenantId");
var audience = Require(trust, "Audience");
var allowedClientId = Require(trust, "AllowedClientId");

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidAudiences = [audience],
            // Entra issues v1 or v2 tokens depending on the resource app's accessTokenAcceptedVersion.
            ValidIssuers =
            [
                $"https://sts.windows.net/{tenantId}/",
                $"https://login.microsoftonline.com/{tenantId}/v2.0",
            ],
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var p = ctx.Principal!;
                if (p.FindFirstValue("tid") != tenantId)
                    ctx.Fail($"Token tenant is not {tenantId}.");
                else if ((p.FindFirstValue("appid") ?? p.FindFirstValue("azp")) != allowedClientId)
                    ctx.Fail("Token was not issued to the allowed plug-in identity.");
                return Task.CompletedTask;
            },
            OnAuthenticationFailed = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>().LogWarning(
                    "Token rejected correlationId={CorrelationId}: {Reason}",
                    ctx.Request.Headers["x-correlation-id"].ToString(), ctx.Exception.Message);
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/spike/whoami", (HttpContext http, ILogger<Program> log) =>
{
    var correlationId = http.Request.Headers["x-correlation-id"].ToString();
    var claims = http.User.Claims
        .GroupBy(c => c.Type)
        .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(c => c.Value)));
    log.LogInformation("Validated plug-in call correlationId={CorrelationId} claims={Claims}",
        correlationId, string.Join(",", claims.Keys));
    return Results.Ok(new { correlationId, validated = true, claims });
}).RequireAuthorization();

app.Run();

static string Require(IConfiguration section, string key) =>
    section[key] is { Length: > 0 } v ? v : throw new InvalidOperationException($"Missing configuration Trust:{key}.");
