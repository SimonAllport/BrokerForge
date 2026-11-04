using System.Text.Json.Nodes;
using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <summary>
/// Creates a source's Dataverse components from its table schema (HLD 5.3): the solution and publisher, the
/// data source record (host URL, scope and instance name; no secrets), the virtual table and its columns, and
/// each Custom API. Everything is read first; any difference from the schema refuses the run before a write.
/// </summary>
public static class Generator
{
    public const string ProviderName = "BrokerForge";
    private const string Setup = "tools/dev/setup-dataverse.sh";

    public static async Task<GenerationResult> RunAsync(DataverseClient client, SourceSchema schema, GenerateOptions o, CancellationToken ct)
    {
        // ---- read ----
        var provider = (await client.ListAsync($"entitydataproviders?$select=entitydataproviderid&$filter=name eq {DataverseClient.Literal(ProviderName)}", ct)).FirstOrDefault();
        var refusals = new List<string>();
        if (provider == null)
            refusals.Add($"the {ProviderName} data provider is not registered; run {Setup} (core) first");
        string? pluginTypeId = null;
        if (schema.CustomApis.Count > 0)
        {
            pluginTypeId = (string?)(await client.ListAsync($"plugintypes?$select=plugintypeid&$filter=typename eq {DataverseClient.Literal(CustomApiGenerator.PluginTypeName)}", ct)).FirstOrDefault()?["plugintypeid"];
            if (pluginTypeId == null)
                refusals.Add($"plug-in type {CustomApiGenerator.PluginTypeName} is not registered; run {Setup} (core) first");
        }
        if (refusals.Count > 0)
            throw new GenerationRefusedException(refusals);
        var providerId = (string)provider!["entitydataproviderid"]!;

        var publisher = (await client.ListAsync($"publishers?$select=publisherid,customizationprefix&$filter=uniquename eq {DataverseClient.Literal(o.Publisher)}", ct)).FirstOrDefault();
        var solution = (await client.ListAsync($"solutions?$select=solutionid&$filter=uniquename eq {DataverseClient.Literal(o.Solution)}", ct)).FirstOrDefault();
        var dataSource = schema.Table == null ? null
            : (await client.ListAsync($"bf_brokerdatasources?$select=bf_brokerdatasourceid,bf_hosturl,bf_scope&$filter=bf_serviceinstance eq {DataverseClient.Literal(o.Instance)}", ct)).FirstOrDefault();
        var table = schema.Table == null ? null : await TableGenerator.ReadAsync(client, schema, ct);
        var apis = new Dictionary<string, ExistingCustomApi?>();
        foreach (var api in schema.CustomApis.Keys)
            apis[api] = await CustomApiGenerator.ReadAsync(client, api, ct);

        // The schema parser holds every table, column and Custom API name to one prefix, so one name gives it.
        var prefix = (schema.Table ?? schema.CustomApis.Keys.First()).Split('_')[0];
        var plan = GenerationPlan.Diff(schema, table, apis, providerId, (string?)dataSource?["bf_brokerdatasourceid"], pluginTypeId, o.Instance);
        var differences = plan.Refusals.ToList();
        differences.AddRange(plan.Changes.Where(c => c.Kind is not (ChangeKind.AddTable or ChangeKind.AddColumn or ChangeKind.AddApi))
            .Select(c => c.Kind is ChangeKind.RemoveColumn or ChangeKind.RemoveApi ? $"{c.Text}: it is in Dataverse but not in the schema, and the generator only adds" : $"{c.Text}: the generator only adds"));
        // Shown in target order, created in schema order.
        var adding = plan.Changes.Where(c => c.Kind == ChangeKind.AddColumn).Select(c => c.Target[(c.Target.IndexOf('.') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var columnsToAdd = schema.Columns.Keys.Where(adding.Contains).ToList();
        var apisToAdd = plan.Changes.Where(c => c.Kind == ChangeKind.AddApi).Select(c => c.Target).ToList();
        if (publisher != null && (string?)publisher["customizationprefix"] is { } existingPrefix && existingPrefix != prefix)
            differences.Add($"publisher {o.Publisher} has prefix {existingPrefix}, but the schema uses {prefix}");
        if (differences.Count > 0)
            throw new GenerationRefusedException(differences);

        // ---- write ----
        if (publisher == null)
            await client.WriteAsync(HttpMethod.Post, "publishers", new JsonObject
            {
                ["uniquename"] = o.Publisher, ["friendlyname"] = o.Publisher, ["customizationprefix"] = prefix, ["customizationoptionvalueprefix"] = 10000,
            }, null, ct);
        if (solution == null)
        {
            var publisherId = (string?)publisher?["publisherid"]
                ?? (string?)(await ListAfterWrite(client, $"publishers?$select=publisherid&$filter=uniquename eq {DataverseClient.Literal(o.Publisher)}", ct))?["publisherid"] ?? "(new publisher)";
            await client.WriteAsync(HttpMethod.Post, "solutions", new JsonObject
            {
                ["uniquename"] = o.Solution, ["friendlyname"] = o.Solution, ["version"] = "1.0.0.0", ["publisherid@odata.bind"] = $"/publishers({publisherId})",
            }, null, ct);
        }

        if (schema.Table != null)
        {
            var dataSourceId = await EnsureDataSource(client, dataSource, o, ct);
            var created = false;
            if (table == null)
            {
                await client.WriteAsync(HttpMethod.Post, "EntityDefinitions", TableGenerator.TablePayload(schema, providerId, dataSourceId), o.Solution, ct);
                created = true;
            }
            foreach (var column in columnsToAdd)
                await client.WriteAsync(HttpMethod.Post, $"EntityDefinitions(LogicalName='{schema.Table}')/Attributes",
                    TableGenerator.ColumnPayload(column, schema.Columns[column]), o.Solution, ct);
            if (created || columnsToAdd.Count > 0)
                await client.WriteAsync(HttpMethod.Post, "PublishXml", new JsonObject
                {
                    ["ParameterXml"] = $"<importexportxml><entities><entity>{schema.Table}</entity></entities></importexportxml>",
                }, null, ct);
            if (created && !client.DryRun)
                await CheckTable(client, schema, providerId, dataSourceId, ct);
        }

        foreach (var api in apisToAdd)
            await client.WriteAsync(HttpMethod.Post, "customapis", CustomApiGenerator.Payload(api, schema.CustomApis[api], pluginTypeId!), o.Solution, ct);

        return new GenerationResult(client.Writes);
    }

    private static async Task<JsonObject?> ListAfterWrite(DataverseClient client, string path, CancellationToken ct) =>
        client.DryRun ? null : (await client.ListAsync(path, ct)).FirstOrDefault();

    /// <summary>The instance's data source record. Host URL and scope follow the options: they are environment values, not metadata.</summary>
    private static async Task<string> EnsureDataSource(DataverseClient client, JsonObject? existing, GenerateOptions o, CancellationToken ct)
    {
        var values = new JsonObject { ["bf_hosturl"] = o.HostUrl, ["bf_scope"] = o.Scope };
        if (existing != null)
        {
            var id = (string)existing["bf_brokerdatasourceid"]!;
            if ((string?)existing["bf_hosturl"] != o.HostUrl || (string?)existing["bf_scope"] != o.Scope)
                await client.WriteAsync(HttpMethod.Patch, $"bf_brokerdatasources({id})", values, null, ct);
            return id;
        }
        values["bf_name"] = $"{o.Instance} host";
        values["bf_serviceinstance"] = o.Instance;
        var created = await client.WriteAsync(HttpMethod.Post, "bf_brokerdatasources", values, null, ct);
        return (string?)created?["bf_brokerdatasourceid"] ?? "(new bf_brokerdatasource)";
    }

    /// <summary>The platform drops DataProviderId on a create it does not accept as a virtual table (HLD 15), so read it back.</summary>
    private static async Task CheckTable(DataverseClient client, SourceSchema schema, string providerId, string dataSourceId, CancellationToken ct)
    {
        var table = await client.GetAsync($"EntityDefinitions(LogicalName='{schema.Table}')?$select=TableType,DataProviderId,DataSourceId", ct);
        var problems = new List<string>();
        if ((string?)table?["TableType"] != "Virtual")
            problems.Add($"{schema.Table} TableType is {(string?)table?["TableType"] ?? "(missing)"} after create, not Virtual (HLD 15)");
        if (!GenerationPlan.SameId((string?)table?["DataProviderId"], providerId))
            problems.Add($"{schema.Table} DataProviderId is {(string?)table?["DataProviderId"] ?? "(none)"} after create: the platform dropped the provider (HLD 15); {GenerationPlan.DeleteByHand(schema.Table!)}");
        if (!GenerationPlan.SameId((string?)table?["DataSourceId"], dataSourceId))
            problems.Add($"{schema.Table} DataSourceId is {(string?)table?["DataSourceId"] ?? "(none)"} after create, expected {dataSourceId} (HLD 15)");
        if (problems.Count > 0)
            throw new GenerationRefusedException(problems);
    }
}
