using System.Text.Json.Nodes;
using BrokerForge.Auth.Credentials;
using BrokerForge.Generator;
using BrokerForge.Host.Config;

namespace BrokerForge.Host.Admin;

/// <summary>
/// This environment's connections (spec 9): the <c>bf_connection</c> rows, which are edited here, and the designs that
/// arrived by solution import, which have no row and are read-only. Each has a status: ready, needs URL, needs key, or
/// not generated.
/// </summary>
public static class ConnectionList
{
    public static async Task<JsonArray> ListAsync(DataverseClient host, HostConfigStore config, string environmentId, IInstanceSecretStore secrets,
        Func<string, string?> secretUrl, CancellationToken ct)
    {
        var rows = (await ConnectionRows.ListAsync(host, ct)).ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var designs = config.Instances.Where(i => string.Equals(i.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
        var list = new JsonArray();
        foreach (var name in rows.Keys.Union(designs.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var row = rows.GetValueOrDefault(name);
            var design = designs.GetValueOrDefault(name);
            var instance = design?.Name ?? row!.Name;
            string status;
            if (design == null)
                status = "not generated";
            else if (design.Incomplete.Any(i => i.Contains("base URL", StringComparison.Ordinal)))
                status = "needs URL";
            else
            {
                status = "ready";
                foreach (var secret in design.Secrets.Keys)
                    if (!await secrets.ExistsAsync(environmentId, instance, secret, ct))
                        status = "needs key";
            }
            list.Add(new JsonObject
            {
                ["name"] = instance,
                ["rowId"] = row?.Id.ToString("D"),
                ["readOnly"] = row == null,
                ["status"] = status,
                ["designVersion"] = design?.DesignVersion,
                ["baseUrlVariable"] = $"bf_{instance}_BaseUrl",
                ["secretUrl"] = secretUrl(instance),
            });
        }
        return list;
    }

    /// <summary>The row for <paramref name="instance"/> in this environment, or null.</summary>
    public static async Task<ConnectionRow?> RowAsync(DataverseClient host, string instance, CancellationToken ct) =>
        (await host.ListAsync($"{ConnectionRows.EntitySet}?$select={ConnectionRow.Select}&$filter=bf_name eq {DataverseClient.Literal(instance)}", ct))
            .Select(ConnectionRow.FromJson).FirstOrDefault();

    /// <summary>
    /// The instance secrets a connection needs: from its row where it is edited, else from the design that arrived by
    /// import. Null when the environment has neither.
    /// </summary>
    public static async Task<(string Name, IReadOnlyList<string> Secrets)?> SecretNamesAsync(DataverseClient host, HostConfigStore config, string environmentId,
        string instance, CancellationToken ct)
    {
        if (await RowAsync(host, instance, ct) is { } row)
            return (row.Name, DesignBuilder.Auth(row).SecretNames);
        if (config.Instance(instance) is { } design && string.Equals(design.EnvironmentId, environmentId, StringComparison.OrdinalIgnoreCase))
            return (design.Name, design.Secrets.Keys.Order(StringComparer.Ordinal).ToList());
        return null;
    }
}
