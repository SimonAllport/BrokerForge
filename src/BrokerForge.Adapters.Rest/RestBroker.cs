using System.Text;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;

namespace BrokerForge.Adapters.Rest;

/// <summary>
/// Sends a rendered request over HTTP and returns the raw result. All shaping lives in the descriptor engine;
/// this adapter only transports (HLD 7.2). Starting point: RESTVTP's HTTP path.
/// </summary>
public sealed class RestBroker(HttpClient http) : IBroker
{
    public string BrokerType => "rest";

    public BrokerCapabilities Capabilities { get; } = new()
    {
        FilterOperators = FilterOperators.All,
        PagingStrategies = new[] { "cursor", "offset", "pageNumber", "link", "none" },
        SupportsTransactions = false,
    };

    public Task<DiscoveryResult> DiscoverAsync(ServiceInstance instance, CancellationToken ct) =>
        throw new NotImplementedException("Phase 2: OpenAPI discovery is SwaggerForge (tools/SwaggerForge).");

    public async Task<BrokerResult> ExecuteAsync(RenderedRequest request, IAuthenticatedChannel channel, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(request.Method, request.Uri);
        foreach (var (name, value) in request.Headers)
        {
            if (channel.ReservedHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new BrokerConfigurationException($"Header '{name}' is set by the {channel.Mode} authentication mode and cannot come from a descriptor.");
            }

            message.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.Body is not null)
        {
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
        }

        await channel.ApplyAsync(message, ct).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);

        try
        {
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
                .ToList();

            return new BrokerResult { StatusCode = (int)response.StatusCode, Headers = headers, Body = body };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SourceTimeoutException($"Source did not respond within {request.Timeout.TotalSeconds:0}s.", ex) { CorrelationId = request.CorrelationId };
        }
        catch (HttpRequestException ex)
        {
            throw new SourceException(null, $"Could not reach the source: {ex.HttpRequestError}.", ex) { CorrelationId = request.CorrelationId };
        }
    }
}
