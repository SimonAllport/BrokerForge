using Azure.Core;
using BrokerForge.Generator;

namespace BrokerForge.Host.Generation;

/// <summary>Makes the generator's Web API client for one environment.</summary>
public interface IGenerationClientFactory
{
    DataverseClient Create(string orgUrl);
}

/// <summary>
/// The generator's client as the host's own application user: a token for <c>&lt;org&gt;/.default</c> from the host's
/// <see cref="TokenCredential"/>, on the named <c>generation</c> client. Writes such as a table create can take minutes.
/// </summary>
public sealed class HostDataverseClientFactory(TokenCredential credential, IHttpClientFactory http) : IGenerationClientFactory
{
    public DataverseClient Create(string orgUrl)
    {
        var org = orgUrl.TrimEnd('/');
        var client = http.CreateClient("generation");
        client.BaseAddress = new Uri(org + "/api/data/v9.2/");
        var context = new TokenRequestContext(new[] { org + "/.default" });
        return new DataverseClient(client, async ct => (await credential.GetTokenAsync(context, ct)).Token, dryRun: false);
    }
}
