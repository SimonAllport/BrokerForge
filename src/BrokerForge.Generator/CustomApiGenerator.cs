using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>Reads and creates unbound Custom APIs (HLD D-19) bound to <c>BrokerForge.Plugin.CustomApiPlugin</c>.</summary>
public static class CustomApiGenerator
{
    public const string PluginTypeName = "BrokerForge.Plugin.CustomApiPlugin";

    public static async Task<ExistingCustomApi?> ReadAsync(DataverseClient client, string uniqueName, CancellationToken ct)
    {
        var rows = await client.ListAsync(
            $"customapis?$select=customapiid,uniquename,displayname,bindingtype,isfunction,_plugintypeid_value&$filter=uniquename eq {DataverseClient.Literal(uniqueName)}" +
            "&$expand=CustomAPIRequestParameters($select=uniquename,type,isoptional),CustomAPIResponseProperties($select=uniquename,type)", ct);
        if (rows.Count == 0)
            return null;
        var api = rows[0];
        return new ExistingCustomApi(
            (string?)api["_plugintypeid_value"] ?? "(none)",
            (int?)api["bindingtype"] ?? -1,
            (bool?)api["isfunction"] ?? false,
            api["CustomAPIRequestParameters"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => ((int)p!["type"]!, (bool)p["isoptional"]!)),
            api["CustomAPIResponseProperties"]!.AsArray().ToDictionary(p => (string)p!["uniquename"]!, p => (int)p!["type"]!),
            (string?)api["displayname"]);
    }

    /// <summary>The Custom API with its parameters and response properties, created in one deep insert.</summary>
    public static JsonObject Payload(string uniqueName, CustomApiSchema api, string pluginTypeId) => new()
    {
        ["uniquename"] = uniqueName,
        ["name"] = uniqueName,
        ["displayname"] = api.DisplayName,
        ["description"] = api.DisplayName,
        ["bindingtype"] = 0,
        ["isfunction"] = false,
        ["isprivate"] = false,
        ["allowedcustomprocessingsteptype"] = 0,
        // The navigation property is PluginTypeId; Web API binding names are case-sensitive (phase 2 findings).
        ["PluginTypeId@odata.bind"] = $"/plugintypes({pluginTypeId})",
        ["CustomAPIRequestParameters"] = new JsonArray(api.Inputs.Select(i => (JsonNode)new JsonObject
        {
            ["uniquename"] = i.Key, ["name"] = $"{uniqueName}.{i.Key}", ["displayname"] = i.Key, ["description"] = i.Key,
            ["type"] = GenerationPlan.ParamTypeCodes[i.Value.Type], ["isoptional"] = !i.Value.Required,
        }).ToArray()),
        ["CustomAPIResponseProperties"] = new JsonArray(api.Outputs.Select(o => (JsonNode)new JsonObject
        {
            ["uniquename"] = o.Key, ["name"] = $"{uniqueName}.{o.Key}", ["displayname"] = o.Key, ["description"] = o.Key,
            ["type"] = GenerationPlan.ParamTypeCodes[o.Value],
        }).ToArray()),
    };
}
