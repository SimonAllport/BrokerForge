using BrokerForge.Abstractions;

namespace BrokerForge.Auth;

/// <summary>Sends through a shared HttpClient after attaching this channel's credential to each request.</summary>
internal sealed class HttpChannel(HttpClient http, Action<HttpRequestMessage> attach, params string[] credentialValues) : IAuthenticatedChannel
{
    public IReadOnlyCollection<string> CredentialValues { get; } = credentialValues;

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        attach(request);
        return http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }
}
