using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BrokerForge.Generator;

/// <summary>A Dataverse Web API call failed. The message carries the method, path, status and the platform's error text.</summary>
public sealed class DataverseException(string message) : Exception(message);

/// <summary>
/// Thin Web API client for the generator. Reads always run; with <see cref="DryRun"/> writes are recorded in
/// <see cref="Writes"/> and not sent. The bearer token goes only in the Authorization header.
/// </summary>
public sealed class DataverseClient(HttpClient http, Func<CancellationToken, Task<string>> token, bool dryRun)
{
    private readonly List<string> _writes = new();

    public bool DryRun => dryRun;

    /// <summary>"METHOD path" for each write made (or planned, in a dry run).</summary>
    public IReadOnlyList<string> Writes => _writes;

    /// <summary>The JSON body, or null when the resource does not exist (404).</summary>
    public async Task<JsonObject?> GetAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        return await ReadAsync(response, "GET", path, ct) ?? throw new DataverseException($"GET {path} returned no body.");
    }

    /// <summary>The <c>value</c> rows of a collection query.</summary>
    public async Task<IReadOnlyList<JsonObject>> ListAsync(string path, CancellationToken ct)
    {
        var body = await GetAsync(path, ct) ?? throw new DataverseException($"GET {path} returned 404.");
        return body["value"]?.AsArray().Select(v => v!.AsObject()).ToList() ?? throw new DataverseException($"GET {path} returned no value list.");
    }

    /// <summary>POST, PATCH or PUT. Returns the representation when the platform sends one; null in a dry run.</summary>
    public async Task<JsonObject?> WriteAsync(HttpMethod method, string path, JsonObject? body, string? solution, CancellationToken ct)
    {
        _writes.Add($"{method.Method} {path}");
        if (dryRun)
            return null;
        using var response = await SendAsync(method, path, body, solution, ct);
        return await ReadAsync(response, method.Method, path, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonObject? body, string? solution, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(ct));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("OData-Version", "4.0");
        request.Headers.Add("OData-MaxVersion", "4.0");
        request.Headers.Add("Prefer", "return=representation");
        if (solution != null)
            request.Headers.Add("MSCRM.SolutionUniqueName", solution);
        if (body != null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await http.SendAsync(request, ct);
    }

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, string method, string path, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new DataverseException($"{method} {path} returned HTTP {(int)response.StatusCode}: {(text.Length > 1500 ? text[..1500] : text)}");
        return text.Length == 0 ? null : JsonNode.Parse(text)?.AsObject();
    }
}
