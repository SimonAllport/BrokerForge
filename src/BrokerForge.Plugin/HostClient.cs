using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin;

/// <summary>Gets a token the broker host will accept.</summary>
public interface ITokenSource
{
    string GetToken();
}

/// <summary>
/// Power Platform managed identity (HLD 9.2). The plug-in assembly must be bound to a managedidentity record.
/// UNVERIFIED (CLAUDE.md, phase 0): token acquisition here and the claims the host can validate.
/// </summary>
public sealed class ManagedIdentityTokenSource : ITokenSource
{
    private readonly IServiceProvider _services;
    private readonly string _scope;

    public ManagedIdentityTokenSource(IServiceProvider services, string scope)
    {
        _services = services;
        _scope = scope;
    }

    public string GetToken()
    {
        var identity = _services.GetService(typeof(IManagedIdentityService)) as IManagedIdentityService
            ?? throw new BrokerUnavailableException("Managed identity is not available to this plug-in. Bind the assembly to a managedidentity record.");

        return identity.AcquireToken(new[] { _scope });
    }
}

/// <summary>
/// Posts the canonical request to the host. Domain errors come back in the envelope and are rethrown typed;
/// anything else is <see cref="BrokerUnavailableException"/>.
/// </summary>
public sealed class BrokerHostClient
{
    /// <summary>Under the 2-minute sandbox limit, above the host's 90-second source timeout.</summary>
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(110) };

    private readonly Uri _hostUrl;
    private readonly ITokenSource _tokens;

    public BrokerHostClient(Uri hostUrl, ITokenSource tokens)
    {
        _hostUrl = hostUrl;
        _tokens = tokens;
    }

    public CanonicalResponse Execute(CanonicalRequest request)
    {
        using (var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_hostUrl, "v1/execute")))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.GetToken());
            message.Content = new StringContent(CanonicalJson.Serialize(request), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = Http.SendAsync(message).GetAwaiter().GetResult();
            }
            catch (TaskCanceledException ex)
            {
                throw new BrokerUnavailableException("Broker host did not respond within 110 seconds.", ex) { CorrelationId = request.CorrelationId };
            }
            catch (HttpRequestException ex)
            {
                throw new BrokerUnavailableException("Could not reach the broker host.", ex) { CorrelationId = request.CorrelationId };
            }

            using (response)
            {
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new BrokerUnavailableException("Broker host rejected the plug-in's identity (401). Check the managed identity binding and host trust settings.") { CorrelationId = request.CorrelationId };
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new BrokerUnavailableException("Broker host returned " + (int)response.StatusCode + ".") { CorrelationId = request.CorrelationId };
                }

                var canonical = CanonicalJson.Deserialize<CanonicalResponse>(body);
                if (canonical.Error != null)
                {
                    throw BrokerForgeException.FromError(canonical.Error);
                }

                return canonical;
            }
        }
    }
}
