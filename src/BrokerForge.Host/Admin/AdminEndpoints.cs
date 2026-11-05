using BrokerForge.Abstractions;
using BrokerForge.Auth.Credentials;
using BrokerForge.Connections;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Generator;
using BrokerForge.Host.Config;
using BrokerForge.Host.Connect;
using BrokerForge.Host.Generation;
using BrokerForge.Host.Ingress;
using BrokerForge.Schema;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Admin;

/// <summary>
/// The host's admin pages, behind the connect app's Entra sign-in (HLD D-26):
/// <list type="bullet">
/// <item><c>/admin/{environmentId}/{instance}/secret</c> (spec 8): set the connection's keys. The host checks, as its own
/// application user in that environment, that the signed-in user is a BrokerForge administrator there. A value is stored
/// in the instance secret store and never written back to a page or a log.</item>
/// <item><c>/admin/{environmentId}/{instance}/role</c> (HLD D-36): the role step the host may not take. The administrator
/// signs in for Dataverse with the connect app, and <see cref="RoleGenerator"/> sets <c>&lt;Instance&gt; user</c> with their
/// own token, used for that request only.</item>
/// </list>
/// </summary>
public static class AdminEndpoints
{
    public const int MaxSecret = 4096;
    private const string RolePrefix = "role:";

    public static string RoleScope(string orgUrl) => orgUrl.TrimEnd('/') + "/user_impersonation";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization(ConnectEndpoints.Policy);
        group.MapGet("/callback", RoleCallback);
        group.MapGet("/{environmentId:guid}/{instance}/secret", Secret);
        group.MapPost("/{environmentId:guid}/{instance}/secret", SaveSecret);
        group.MapGet("/{environmentId:guid}/{instance}/role", Role);
        group.MapPost("/{environmentId:guid}/{instance}/role/start", StartRole);
    }

    private static string? UserObjectId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst("oid")?.Value, out var oid) ? oid.ToString("D") : null;

    private static IResult NotFound(string message) => ConnectPages.Message(404, "Not found", message);
    private static IResult Forbidden() => ConnectPages.Message(403, "Not allowed", AdminCheck.Refusal);
    private static IResult Refused(string title, string message) => ConnectPages.Message(400, title, message);

    /// <summary>The environment's org URL when the host serves it; null otherwise, so a typed GUID makes the host read nothing.</summary>
    private static string? OrgOf(HttpContext http, Guid environmentId)
    {
        var caller = http.RequestServices.GetRequiredService<IOptions<TrustOptions>>().Value.AllowedCallers
            .FirstOrDefault(c => Guid.TryParse(c.EnvironmentId, out var id) && id == environmentId);
        return caller?.OrgUrl is { Length: > 0 } org ? org : null;
    }

    private static async Task<bool> AntiforgeryValid(HttpContext http, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>The checks every admin page starts with: a served environment, a signed-in administrator there.</summary>
    private static async Task<(DataverseClient? Host, IResult? Failure)> Admit(HttpContext http, Guid environmentId, CancellationToken ct)
    {
        if (OrgOf(http, environmentId) is not { } org)
            return (null, NotFound($"This host does not serve environment {environmentId:D}."));
        var host = http.RequestServices.GetRequiredService<IGenerationClientFactory>().Create(org);
        if (!await AdminCheck.IsAdministratorAsync(host, UserObjectId(http), ct))
            return (null, Forbidden());
        return (host, null);
    }

    private static async Task<(string Name, IReadOnlyList<string> Secrets)?> SecretsOf(HttpContext http, DataverseClient host, Guid environmentId, string instance, CancellationToken ct)
    {
        var env = environmentId.ToString("D");
        HostConfigStore config;
        try
        {
            config = await http.RequestServices.GetRequiredService<IHostConfigProvider>().ForEnvironmentAsync(env, ct);
        }
        catch (Exception ex) when (ex is ConfigurationException or DesignSourceUnavailableException)
        {
            // The connection's own row still names its keys when the environment's other designs cannot be read.
            config = http.RequestServices.GetRequiredService<IHostConfigProvider>().Files;
        }
        return await ConnectionList.SecretNamesAsync(host, config, env, instance, ct);
    }

    private static async Task<IResult> Secret(Guid environmentId, string instance, HttpContext http, IInstanceSecretStore store, IAntiforgery antiforgery, CancellationToken ct)
    {
        var (host, failure) = await Admit(http, environmentId, ct);
        if (failure != null)
            return failure;
        if (await SecretsOf(http, host!, environmentId, instance, ct) is not { } found)
            return NotFound($"There is no connection called {instance} in this environment.");
        if (found.Secrets.Count == 0)
            return ConnectPages.Page(200, $"Keys for {found.Name}", ConnectPages.Paragraph($"{found.Name} uses no key."));
        var token = antiforgery.GetAndStoreTokens(http).RequestToken!;
        var body = "";
        foreach (var name in found.Secrets)
        {
            var set = await store.ExistsAsync(environmentId.ToString("D"), found.Name, name, ct);
            body += ConnectPages.Paragraph($"{name}: {(set ? "set" : "needs key")}");
            body += ConnectPages.Form($"/admin/{environmentId:D}/{Uri.EscapeDataString(found.Name)}/secret", token, ConnectPages.SecretFields(name), set ? "Replace key" : "Set key");
        }
        return ConnectPages.Page(200, $"Keys for {found.Name}", body);
    }

    private static async Task<IResult> SaveSecret(Guid environmentId, string instance, HttpContext http, IInstanceSecretStore store, IAntiforgery antiforgery,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var (host, failure) = await Admit(http, environmentId, ct);
        if (failure != null)
            return failure;
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("Key not saved", "The form has expired. Open the key page again.");
        if (await SecretsOf(http, host!, environmentId, instance, ct) is not { } found)
            return NotFound($"There is no connection called {instance} in this environment.");
        var form = await http.Request.ReadFormAsync(ct);
        var name = form["name"].ToString();
        if (!found.Secrets.Contains(name, StringComparer.Ordinal))
            return NotFound($"{found.Name} has no key called {name}.");
        var value = form["value"].ToString();
        if (value.Length is 0 or > MaxSecret || value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return Refused("Key not saved", $"Enter a key of 1 to {MaxSecret} characters on one line.");
        await store.SetAsync(environmentId.ToString("D"), found.Name, name, value, ct);
        loggers.CreateLogger("BrokerForge.Admin").LogInformation("Set instance secret {Name} of {Instance} in environment {EnvironmentId} by {UserObjectId}",
            name, found.Name, environmentId.ToString("D"), UserObjectId(http));
        return new SeeOther($"/admin/{environmentId:D}/{Uri.EscapeDataString(found.Name)}/secret");
    }

    /// <summary>The role step for a connection generated here: what it would change, from the published design, read as the host.</summary>
    private sealed record RolePlan(string Instance, string Table, IReadOnlySet<string> Messages, string Solution, string? Change);

    private static async Task<(RolePlan? Plan, IResult? Failure)> PlanRole(HttpContext http, DataverseClient host, Guid environmentId, string instance, CancellationToken ct)
    {
        if (await ConnectionList.RowAsync(host, instance, ct) is not { } row)
            return (null, NotFound($"There is no connection called {instance} in this environment. The role step is taken where the connection is edited."));
        ConnectionDesign design;
        try
        {
            design = await http.RequestServices.GetRequiredService<GenerationJobs>().ReadPublishedAsync(environmentId.ToString("D"), row.Name, ct);
        }
        catch (ConfigurationException ex)
        {
            return (null, NotFound($"{ex.Message} Generate it first."));
        }
        var table = SchemaParser.Parse(design.Schema, $"{row.Name} design schema").Table;
        var messages = design.Descriptors.Select(d => DescriptorParser.Parse(d.Value, d.Key)).Where(d => d.CustomApi == null).Select(d => d.Binds)
            .ToHashSet(StringComparer.Ordinal);
        if (table == null || messages.Count == 0)
            return (null, ConnectPages.Message(200, $"Role for {row.Name}", $"{row.Name} has no table, so it has no role."));
        var solution = DesignBuilder.SolutionOf(row);
        var solutionId = (string?)(await host.ListAsync($"solutions?$select=solutionid&$filter=uniquename eq {DataverseClient.Literal(solution)}", ct)).FirstOrDefault()?["solutionid"];
        var change = await RoleGenerator.DescribeAsync(host, table, row.Name, messages, solutionId, ct);
        return (new RolePlan(row.Name, table, messages, solution, change), null);
    }

    private static async Task<IResult> Role(Guid environmentId, string instance, HttpContext http, IAntiforgery antiforgery, CancellationToken ct)
    {
        var (host, failure) = await Admit(http, environmentId, ct);
        if (failure != null)
            return failure;
        var (plan, planFailure) = await PlanRole(http, host!, environmentId, instance, ct);
        if (planFailure != null)
            return planFailure;
        var title = $"Role for {plan!.Instance}";
        if (plan.Change == null)
            return ConnectPages.Message(200, title, $"Role {RoleGenerator.RoleName(plan.Instance)} is up to date.");
        var token = antiforgery.GetAndStoreTokens(http).RequestToken!;
        return ConnectPages.Page(200, title, ConnectPages.Paragraph($"To do: {plan.Change}. You sign in for Dataverse, and the role is set as you.")
            + ConnectPages.Form($"/admin/{environmentId:D}/{Uri.EscapeDataString(plan.Instance)}/role/start", token, "", "Set the role"));
    }

    private static async Task<IResult> StartRole(Guid environmentId, string instance, HttpContext http, IAntiforgery antiforgery, IDataProtectionProvider protection,
        TimeProvider time, IConfiguration configuration, IOptions<TrustOptions> trust, CancellationToken ct)
    {
        var (host, failure) = await Admit(http, environmentId, ct);
        if (failure != null)
            return failure;
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("Role not set", "The form has expired. Open the role page again.");
        var (plan, planFailure) = await PlanRole(http, host!, environmentId, instance, ct);
        if (planFailure != null)
            return planFailure;
        if (configuration["Connect:ClientId"] is not { Length: > 0 } clientId)
            return ConnectPages.Message(500, "Role not set", "Connect:ClientId is not set, so an administrator cannot sign in for the role step.");
        var state = new ConnectState(RolePrefix + plan!.Instance, UserObjectId(http)!, ConnectState.Random(32), ConnectState.Random(16), time.GetUtcNow(), environmentId.ToString("D"));
        http.Response.Cookies.Append(state.NonceCookieName, state.Nonce, new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/admin/callback", MaxAge = ConnectState.Lifetime,
        });
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = CallbackUrl(configuration),
            ["scope"] = RoleScope(OrgOf(http, environmentId)!),
            ["state"] = state.Protect(protection),
            ["code_challenge"] = ConnectState.Challenge(state.Verifier),
            ["code_challenge_method"] = "S256",
        });
        return Results.Redirect($"https://login.microsoftonline.com/{trust.Value.TenantId}/oauth2/v2.0/authorize{query.Value}");
    }

    private static string CallbackUrl(IConfiguration configuration) =>
        (configuration["Connect:PublicBaseUrl"] is { Length: > 0 } b ? b.TrimEnd('/') : throw new ConfigurationException("Connect:PublicBaseUrl is not set."))
        + "/admin/callback";

    private static async Task<IResult> RoleCallback(HttpContext http, IDataProtectionProvider protection, TimeProvider time, IConfiguration configuration,
        IAdminTokenExchange exchange, IDelegatedDataverseFactory delegated, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Admin");
        var q = http.Request.Query;
        var state = ConnectState.Unprotect(protection, q["state"]);
        if (state is not { EnvironmentId: { } env } || !state.Instance.StartsWith(RolePrefix, StringComparison.Ordinal))
            return Refused("Role not set", "This sign-in link is not valid. Start again from the role page.");
        var nonce = http.Request.Cookies[state.NonceCookieName];
        http.Response.Cookies.Delete(state.NonceCookieName, new CookieOptions { Path = "/admin/callback", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
        var age = time.GetUtcNow() - state.IssuedAt;
        if (age < TimeSpan.Zero || age > ConnectState.Lifetime)
            return Refused("Role not set", "This sign-in took longer than 10 minutes. Start again from the role page.");
        if (nonce == null || nonce != state.Nonce)
            return Refused("Role not set", "This sign-in was not started in this browser, or has already been used. Start again from the role page.");
        if (UserObjectId(http) != state.UserObjectId)
        {
            log.LogWarning("Role callback in environment {EnvironmentId} refused: started by another user", env);
            return Refused("Role not set", "This sign-in was started by another user. Start again from the role page.");
        }
        if (q["error"].ToString() is { Length: > 0 })
            return Refused("Role not set", "The Dataverse sign-in did not complete. Start again from the role page.");
        if (q["code"].ToString() is not { Length: > 0 } code)
            return Refused("Role not set", "Entra did not return a sign-in code.");

        var environmentId = Guid.Parse(env);
        var (host, failure) = await Admit(http, environmentId, ct);
        if (failure != null)
            return failure;
        var (plan, planFailure) = await PlanRole(http, host!, environmentId, state.Instance[RolePrefix.Length..], ct);
        if (planFailure != null)
            return planFailure;
        var org = OrgOf(http, environmentId)!;
        string token;
        try
        {
            token = await exchange.RedeemAsync(org, code, state.Verifier, CallbackUrl(configuration), ct);
        }
        catch (Exception ex) when (ex is ConfigurationException or HttpRequestException or System.Text.Json.JsonException)
        {
            log.LogWarning("Role sign-in for {Instance} in environment {EnvironmentId} failed: {Reason}", plan!.Instance, env, ex is ConfigurationException ? ex.Message : ex.GetType().Name);
            return ConnectPages.Message(502, "Role not set", "Entra did not issue a Dataverse token. Start again from the role page.");
        }
        try
        {
            await RoleGenerator.ApplyAsync(delegated.Create(org, token), plan!.Table, plan.Instance, plan.Messages, plan.Solution, ct);
        }
        catch (DataverseException ex)
        {
            log.LogWarning("Role {Role} for {Instance} in environment {EnvironmentId} not set: {Reason}", RoleGenerator.RoleName(plan!.Instance), plan.Instance, env, ex.Message);
            return ConnectPages.Message(502, "Role not set", $"Dataverse refused the role change: {ex.Message}");
        }
        log.LogInformation("Role {Role} set for {Instance} in environment {EnvironmentId} by {UserObjectId}", RoleGenerator.RoleName(plan.Instance), plan.Instance, env, state.UserObjectId);
        return ConnectPages.Message(200, $"Role for {plan.Instance}", $"Role {RoleGenerator.RoleName(plan.Instance)} set: {plan.Change ?? "it was already up to date"}.");
    }

    private sealed class SeeOther(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
