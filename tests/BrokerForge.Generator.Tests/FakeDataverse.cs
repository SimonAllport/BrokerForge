using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BrokerForge.Generator.Tests;

/// <summary>
/// An in-memory stand-in for the Dataverse Web API endpoints the generator calls. It keeps just enough
/// metadata state for re-runs to see what an earlier run created, and records every request.
/// </summary>
internal sealed partial class FakeDataverse : HttpMessageHandler
{
    public const string Org = "https://org.example.crm.dynamics.com";
    public const string ProviderId = "4c75d224-30d5-f111-84ea-002248db5cf2";
    public const string CustomApiPluginTypeId = "7d000000-0000-0000-0000-0000000000a1";

    public sealed record Recorded(string Method, string Path, string? Body, string? Solution, string? Authorization);

    public List<Recorded> Requests { get; } = new();
    public IEnumerable<Recorded> Writes => Requests.Where(r => r.Method != "GET");
    public bool DropProviderOnCreate { get; set; }

    private readonly Dictionary<string, string> _publishers = new();
    private readonly Dictionary<string, string> _solutions = new();
    private readonly List<JsonObject> _dataSources = new();
    private readonly Dictionary<string, JsonObject> _tables = new();
    private readonly Dictionary<string, List<JsonObject>> _attributes = new();
    private readonly List<JsonObject> _customApis = new();
    private int _ids;

    private string NewId() => $"00000000-0000-0000-0000-{++_ids:D12}";

    public void RenameDataSources(string instance)
    {
        foreach (var d in _dataSources)
            d["bf_serviceinstance"] = instance;
    }

    public void SetCustomApi(string uniqueName, string key, JsonNode value) =>
        _customApis.Single(a => (string)a["uniquename"]! == uniqueName)[key] = value;

    public void RemoveAttribute(string table, string logical) => _attributes[table].RemoveAll(a => (string)a["LogicalName"]! == logical);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)["/api/data/v9.2/".Length..];
        Requests.Add(new Recorded(request.Method.Method, path, body,
            request.Headers.TryGetValues("MSCRM.SolutionUniqueName", out var s) ? s.Single() : null, request.Headers.Authorization?.ToString()));
        var json = body == null ? null : JsonNode.Parse(body)!.AsObject();
        return Route(request.Method.Method, path, json);
    }

    private static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Value(IEnumerable<JsonNode> rows) => Json(new JsonObject { ["value"] = new JsonArray(rows.Select(r => r.DeepClone()).ToArray()) });

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    [GeneratedRegex(@"eq '((?:[^']|'')+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^EntityDefinitions\(LogicalName='([^']+)'\)(.*)$")]
    private static partial Regex Entity();

    private HttpResponseMessage Route(string method, string path, JsonObject? body)
    {
        var name = Quoted().Match(path) is { Success: true } q ? q.Groups[1].Value.Replace("''", "'") : null;
        switch (method, path)
        {
            case ("GET", _) when path.StartsWith("entitydataproviders?"):
                return Value(name == "BrokerForge" ? new[] { new JsonObject { ["entitydataproviderid"] = ProviderId } } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("plugintypes?"):
                return Value(name == "BrokerForge.Plugin.CustomApiPlugin" ? new[] { new JsonObject { ["plugintypeid"] = CustomApiPluginTypeId } } : Array.Empty<JsonNode>());
            case ("GET", _) when path.StartsWith("publishers?"):
                return Value(_publishers.TryGetValue(name!, out var pid) ? new[] { new JsonObject { ["publisherid"] = pid } } : Array.Empty<JsonNode>());
            case ("POST", "publishers"):
                var publisher = _publishers[(string)body!["uniquename"]!] = NewId();
                return Json(new JsonObject { ["publisherid"] = publisher }, HttpStatusCode.Created);
            case ("GET", _) when path.StartsWith("solutions?"):
                return Value(_solutions.TryGetValue(name!, out var sid) ? new[] { new JsonObject { ["solutionid"] = sid } } : Array.Empty<JsonNode>());
            case ("POST", "solutions"):
                var solution = _solutions[(string)body!["uniquename"]!] = NewId();
                return Json(new JsonObject { ["solutionid"] = solution }, HttpStatusCode.Created);
            case ("GET", _) when path.StartsWith("bf_brokerdatasources?"):
                return Value(_dataSources.Where(d => (string)d["bf_serviceinstance"]! == name));
            case ("POST", "bf_brokerdatasources"):
                body!["bf_brokerdatasourceid"] = NewId();
                _dataSources.Add(body);
                return Json(body.DeepClone(), HttpStatusCode.Created);
            case ("PATCH", _) when path.StartsWith("bf_brokerdatasources("):
                var id = path["bf_brokerdatasources(".Length..^1];
                var row = _dataSources.Single(d => (string)d["bf_brokerdatasourceid"]! == id);
                foreach (var (k, v) in body!)
                    row[k] = v?.DeepClone();
                return Status(HttpStatusCode.NoContent);
            case ("POST", "EntityDefinitions"):
                return CreateTable(body!);
            case ("POST", "PublishXml"):
                return Status(HttpStatusCode.NoContent);
            case ("GET", _) when path.StartsWith("customapis?"):
                return Value(_customApis.Where(a => (string)a["uniquename"]! == name));
            case ("POST", "customapis"):
                body!["customapiid"] = NewId();
                body["_plugintypeid_value"] = ((string)body["PluginTypeId@odata.bind"]!)["/plugintypes(".Length..^1];
                _customApis.Add(body);
                return Status(HttpStatusCode.NoContent);
        }
        if (Entity().Match(path) is { Success: true } e)
            return EntityRoute(method, e.Groups[1].Value, e.Groups[2].Value, body);
        throw new InvalidOperationException($"FakeDataverse has no route for {method} {path}");
    }

    private HttpResponseMessage CreateTable(JsonObject body)
    {
        var logical = ((string)body["SchemaName"]!).ToLowerInvariant();
        _tables[logical] = new JsonObject
        {
            ["TableType"] = "Virtual",
            ["DataProviderId"] = DropProviderOnCreate ? null : body["DataProviderId"]?.DeepClone(),
            ["DataSourceId"] = body["DataSourceId"]?.DeepClone(),
            ["ExternalName"] = body["ExternalName"]?.DeepClone(),
        };
        _attributes[logical] = new List<JsonObject>
        {
            new() { ["LogicalName"] = logical + "id", ["AttributeType"] = "Uniqueidentifier", ["IsCustomAttribute"] = true, ["ExternalName"] = null },
        };
        foreach (var attribute in body["Attributes"]!.AsArray())
            AddAttribute(logical, attribute!.AsObject());
        return Status(HttpStatusCode.NoContent);
    }

    private void AddAttribute(string table, JsonObject payload)
    {
        var type = ((string)payload["@odata.type"]!).Replace("Microsoft.Dynamics.CRM.", "").Replace("AttributeMetadata", "");
        _attributes[table].Add(new JsonObject
        {
            ["LogicalName"] = ((string)payload["SchemaName"]!).ToLowerInvariant(),
            ["AttributeType"] = type,
            ["IsCustomAttribute"] = true,
            ["ExternalName"] = payload["ExternalName"]?.DeepClone(),
            ["MaxLength"] = payload["MaxLength"]?.DeepClone(),
            ["Precision"] = payload["Precision"]?.DeepClone(),
            ["Format"] = payload["Format"]?.DeepClone(),
            ["OptionSet"] = payload["OptionSet"]?.DeepClone(),
        });
        // Live: the platform adds a derived <column>name attribute for every picklist (phase 2 findings).
        if (type == "Picklist")
            _attributes[table].Add(new JsonObject
            {
                ["LogicalName"] = ((string)payload["SchemaName"]!).ToLowerInvariant() + "name",
                ["AttributeType"] = "Virtual",
                ["IsCustomAttribute"] = true,
                ["AttributeOf"] = ((string)payload["SchemaName"]!).ToLowerInvariant(),
            });
    }

    /// <summary>Replaces one stored attribute's AttributeType, as if the column had been created differently.</summary>
    public void SetAttributeType(string table, string logical, string type) =>
        _attributes[table].Single(a => (string)a["LogicalName"]! == logical)["AttributeType"] = type;

    private HttpResponseMessage EntityRoute(string method, string table, string rest, JsonObject? body)
    {
        if (!_tables.TryGetValue(table, out var t))
            return Status(HttpStatusCode.NotFound);
        if (method == "GET" && rest.StartsWith("?"))
            return Json(t.DeepClone());
        if (method == "POST" && rest == "/Attributes")
        {
            AddAttribute(table, body!);
            return Status(HttpStatusCode.NoContent);
        }
        if (method == "GET" && rest.StartsWith("/Attributes?"))
            return Value(_attributes[table].Select(a => new JsonObject
            {
                ["LogicalName"] = a["LogicalName"]?.DeepClone(), ["AttributeType"] = a["AttributeType"]?.DeepClone(),
                ["IsCustomAttribute"] = a["IsCustomAttribute"]?.DeepClone(), ["ExternalName"] = a["ExternalName"]?.DeepClone(),
                ["AttributeOf"] = a["AttributeOf"]?.DeepClone(),
            }));
        if (method == "GET" && rest.StartsWith("/Attributes/Microsoft.Dynamics.CRM."))
        {
            var cast = rest["/Attributes/Microsoft.Dynamics.CRM.".Length..rest.IndexOf('?')].Replace("AttributeMetadata", "");
            return Value(_attributes[table].Where(a => (string)a["AttributeType"]! == cast).Select(a => a.DeepClone()));
        }
        throw new InvalidOperationException($"FakeDataverse has no route for {method} EntityDefinitions({table}){rest}");
    }
}
