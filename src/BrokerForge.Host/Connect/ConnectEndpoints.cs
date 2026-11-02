using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Auth.PerUser;
using BrokerForge.Host.Config;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Host.Connect;

/// <summary>
/// The connect page (HLD 9.1, D-26). The user signs in with Entra (the Connect cookie scheme), and what they
/// grant is stored under their Entra object ID, the ID the plug-in sends as caller.userObjectId:
/// OAuth and Impersonate through the authorisation-code flow with PKCE, SSO through a form. Every POST checks
/// antiforgery. No credential is logged, echoed or put in a page.
/// </summary>
public static partial class ConnectEndpoints
{
    public const string Scheme = "Connect";
    public const string OidcScheme = "ConnectOidc";
    public const string Policy = "Connect";
    private const int MaxSecret = 1024;
    private const int MaxUsername = 256;

    [GeneratedRegex("^[a-z_]{1,64}$")]
    private static partial Regex OAuthErrorCode();

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/connect").RequireAuthorization(Policy);
        group.MapGet("/callback", Callback);
        group.MapGet("/{instance}", Status);
        group.MapPost("/{instance}/start", Start);
        group.MapPost("/{instance}/sso", SaveSso);
        group.MapPost("/{instance}/disconnect", Disconnect);
    }

    private static string? UserObjectId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirst("oid")?.Value, out var oid) ? oid.ToString("D") : null;

    private static ServiceInstance? PerUser(HostConfigStore config, string name) =>
        config.Instance(name) is { Auth.Mode: AuthMode.OAuth or AuthMode.Impersonate or AuthMode.Sso } instance ? instance : null;

    private static CredentialKind KindFor(AuthMode mode) => mode switch
    {
        AuthMode.OAuth => CredentialKind.OAuth,
        AuthMode.Impersonate => CredentialKind.Impersonate,
        _ => CredentialKind.Sso,
    };

    private static IResult NotFound(string name) => ConnectPages.Message(404, "Not found", $"There is no per-user service instance called {name}.");

    private static IResult NoIdentity() => ConnectPages.Message(403, "Not signed in", "Your sign-in did not include an Entra object ID, so nothing can be stored for you.");

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

    private static IResult Refused(string message) => ConnectPages.Message(400, "Connection not saved", message);

    private static async Task<IResult> Status(string instance, HttpContext http, HostConfigStore config, IUserCredentialStore store, IAntiforgery antiforgery, CancellationToken ct)
    {
        if (PerUser(config, instance) is not { } si)
            return NotFound(instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        UserCredential? credential = null;
        var unreadable = false;
        try
        {
            credential = await store.GetAsync(si.Name, user, ct);
        }
        catch (CredentialUnreadableException)
        {
            unreadable = true;
        }
        var token = antiforgery.GetAndStoreTokens(http).RequestToken!;
        var status = credential switch
        {
            null when unreadable => "the stored connection could not be read; connect again",
            null => "not connected",
            { Kind: var k } when k != KindFor(si.Auth.Mode) => "not connected (an earlier connection was for another mode)",
            { Status: CredentialStatus.Connected } => "connected",
            { Status: var s } => s.ToString().ToLowerInvariant() + "; connect again",
        };
        var body = ConnectPages.Paragraph($"Service: {si.Name}. Status: {status}.");
        body += si.Auth.Mode == AuthMode.Sso
            ? ConnectPages.Form($"/connect/{si.Name}/sso", token, ConnectPages.SsoFields(si.Auth.SsoKind == "basic"), "Save")
            : ConnectPages.Form($"/connect/{si.Name}/start", token, "", credential == null && !unreadable ? "Connect" : "Connect again");
        if (credential != null || unreadable)
            body += ConnectPages.Form($"/connect/{si.Name}/disconnect", token, "", "Disconnect");
        return ConnectPages.Page(200, $"Connect to {si.Name}", body);
    }

    private static async Task<IResult> Start(string instance, HttpContext http, HostConfigStore config, IAntiforgery antiforgery,
        IDataProtectionProvider protection, TimeProvider time, IServiceProvider services)
    {
        if (PerUser(config, instance) is not { } si)
            return NotFound(instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        if (si.Auth.Mode is not (AuthMode.OAuth or AuthMode.Impersonate))
            return Refused($"{si.Name} uses {si.Auth.Mode}; there is no sign-in to start.");

        var resource = config.OAuthResource(si.Auth.OAuthResource!);
        var clientId = resource.UsesConnectApp
            ? (services.GetService<IConnectAppClient>() ?? throw new ConfigurationException("Connect:ClientId is not set.")).ClientId
            : resource.ClientId!;
        var state = new ConnectState(si.Name, user, ConnectState.Random(32), ConnectState.Random(16), time.GetUtcNow());
        http.Response.Cookies.Append(state.NonceCookieName, state.Nonce, new CookieOptions
        {
            HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, Path = "/connect/callback", MaxAge = ConnectState.Lifetime,
        });
        var query = QueryString.Create(new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = config.ConnectCallbackUrl,
            ["scope"] = string.Join(' ', resource.Scopes),
            ["state"] = state.Protect(protection),
            ["code_challenge"] = ConnectState.Challenge(state.Verifier),
            ["code_challenge_method"] = "S256",
        });
        var separator = resource.AuthorizeUrl.Contains('?') ? "&" : "?";
        return Results.Redirect(resource.AuthorizeUrl + separator + query.Value![1..]);
    }

    private static async Task<IResult> Callback(HttpContext http, HostConfigStore config, IUserCredentialStore store, IDataProtectionProvider protection,
        TokenEndpointClient tokens, TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        var log = loggers.CreateLogger("BrokerForge.Connect");
        var q = http.Request.Query;
        var state = ConnectState.Unprotect(protection, q["state"]);
        if (state == null)
            return Refused("This sign-in link is not valid. Start again from the connect page.");
        var nonce = http.Request.Cookies[state.NonceCookieName];
        http.Response.Cookies.Delete(state.NonceCookieName, new CookieOptions { Path = "/connect/callback", Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });
        var age = time.GetUtcNow() - state.IssuedAt;
        if (age < TimeSpan.Zero || age > ConnectState.Lifetime)
            return Refused("This sign-in took longer than 10 minutes. Start again from the connect page.");
        if (nonce == null || nonce != state.Nonce)
            return Refused("This sign-in was not started in this browser, or has already been used. Start again from the connect page.");
        if (UserObjectId(http) != state.UserObjectId)
        {
            log.LogWarning("Connect callback for {Instance} refused: started by another user", state.Instance);
            return Refused("This sign-in was started by another user. Start again from the connect page.");
        }
        if (PerUser(config, state.Instance) is not { Auth.Mode: AuthMode.OAuth or AuthMode.Impersonate } si)
            return NotFound(state.Instance);
        if (q["error"].ToString() is { Length: > 0 } error)
            return Refused($"The sign-in at the source did not complete ({(OAuthErrorCode().IsMatch(error) ? error : "unknown error")}).");
        if (q["code"].ToString() is not { Length: > 0 } code)
            return Refused("The source did not return a sign-in code.");

        TokenSet set;
        try
        {
            set = await tokens.RedeemCodeAsync(config.OAuthResource(si.Auth.OAuthResource!), code, state.Verifier, config.ConnectCallbackUrl, ct);
        }
        catch (RefreshRejectedException)
        {
            return Refused("The source refused the sign-in code. Start again from the connect page.");
        }
        catch (CredentialUnavailableException ex)
        {
            log.LogWarning("Connect for {Instance} user {UserObjectId} failed: {Reason}", si.Name, state.UserObjectId, ex.Message);
            return ConnectPages.Message(502, "Connection not saved", "The source's sign-in service did not answer properly. Try again later.");
        }
        await store.SaveAsync(si.Name, state.UserObjectId,
            new UserCredential(KindFor(si.Auth.Mode), CredentialStatus.Connected, set.AccessToken, set.RefreshToken, set.ExpiresAt, null, null, time.GetUtcNow()), ct);
        log.LogInformation("Connected {Mode} for {Instance} user {UserObjectId}", si.Auth.Mode, si.Name, state.UserObjectId);
        return SeeOther($"/connect/{si.Name}");
    }

    private static async Task<IResult> SaveSso(string instance, HttpContext http, HostConfigStore config, IUserCredentialStore store, IAntiforgery antiforgery,
        TimeProvider time, ILoggerFactory loggers, CancellationToken ct)
    {
        if (PerUser(config, instance) is not { } si)
            return NotFound(instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        if (si.Auth.Mode != AuthMode.Sso)
            return Refused($"{si.Name} uses {si.Auth.Mode}, not a username and password.");
        var form = await http.Request.ReadFormAsync(ct);
        var basic = si.Auth.SsoKind == "basic";
        var username = basic ? form["username"].ToString() : null;
        var secret = form["secret"].ToString();
        if (secret.Length is 0 or > MaxSecret || secret.IndexOfAny(new[] { '\r', '\n' }) >= 0)
            return Refused($"Enter a password or key of 1 to {MaxSecret} characters on one line.");
        if (basic && (username!.Length is 0 or > MaxUsername || username.IndexOfAny(new[] { '\r', '\n', ':' }) >= 0))
            return Refused($"Enter a username of 1 to {MaxUsername} characters without a colon.");
        await store.SaveAsync(si.Name, user, new UserCredential(CredentialKind.Sso, CredentialStatus.Connected, null, null, null, username, secret, time.GetUtcNow()), ct);
        loggers.CreateLogger("BrokerForge.Connect").LogInformation("Connected Sso for {Instance} user {UserObjectId}", si.Name, user);
        return SeeOther($"/connect/{si.Name}");
    }

    private static async Task<IResult> Disconnect(string instance, HttpContext http, HostConfigStore config, IUserCredentialStore store, IAntiforgery antiforgery,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (PerUser(config, instance) is not { } si)
            return NotFound(instance);
        if (UserObjectId(http) is not { } user)
            return NoIdentity();
        if (!await AntiforgeryValid(http, antiforgery))
            return Refused("The form has expired. Open the connect page again.");
        await store.DeleteAsync(si.Name, user, ct);
        loggers.CreateLogger("BrokerForge.Connect").LogInformation("Disconnected {Instance} user {UserObjectId}", si.Name, user);
        return SeeOther($"/connect/{si.Name}");
    }

    /// <summary>303, so the browser follows a POST with a GET of the status page.</summary>
    private static IResult SeeOther(string location) => new SeeOtherResult(location);

    private sealed class SeeOtherResult(string location) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.StatusCode = StatusCodes.Status303SeeOther;
            http.Response.Headers.Location = location;
            return Task.CompletedTask;
        }
    }
}
