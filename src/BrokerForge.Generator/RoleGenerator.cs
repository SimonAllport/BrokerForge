using System.Text.Json.Nodes;

namespace BrokerForge.Generator;

/// <summary>
/// The source's role, <c>&lt;Instance&gt; user</c>, with organisation-level privileges on its table that follow the
/// messages the descriptors bind. Only this table's own privileges are added or removed: a new role starts with
/// platform privileges that must stay (generator spike section 4).
/// </summary>
public static class RoleGenerator
{
    private const int Global = 8;
    private static readonly string[] TablePrivileges = { "Read", "Create", "Write", "Delete", "Append", "AppendTo" };

    public static string RoleName(string instance) => $"{instance} user";

    /// <summary>The privilege verbs the messages allow: Read for list and open, then Create, Write and Delete as bound.</summary>
    public static IReadOnlyList<string> Wanted(IReadOnlySet<string> messages)
    {
        var wanted = new List<string>();
        if (messages.Contains("RetrieveMultiple") || messages.Contains("Retrieve"))
            wanted.Add("Read");
        if (messages.Contains("Create"))
            wanted.Add("Create");
        if (messages.Contains("Update"))
            wanted.Add("Write");
        if (messages.Contains("Delete"))
            wanted.Add("Delete");
        return wanted;
    }

    /// <summary>What the role write would do, or null when the role already holds exactly the wanted privileges.</summary>
    public static async Task<string?> DescribeAsync(DataverseClient client, string table, string instance, IReadOnlySet<string> messages, CancellationToken ct)
    {
        var wanted = Wanted(messages);
        var role = await FindAsync(client, instance, ct);
        if (role == null)
            return $"create role {RoleName(instance)} with {string.Join(", ", wanted)} on {table}";
        var (add, remove) = await PlanAsync(client, role.Value.Id, table, wanted, ct);
        return add.Count == 0 && remove.Count == 0 ? null
            : $"set role {RoleName(instance)} to {string.Join(", ", wanted)} on {table}";
    }

    public static async Task<bool> ApplyAsync(DataverseClient client, string table, string instance, IReadOnlySet<string> messages, string solution, CancellationToken ct)
    {
        var wanted = Wanted(messages);
        var bu = (string)(await client.ListAsync("businessunits?$select=businessunitid&$filter=_parentbusinessunitid_value eq null", ct)).Single()["businessunitid"]!;
        var role = await FindAsync(client, instance, ct);
        var wrote = false;
        string roleId;
        if (role == null)
        {
            var created = await client.WriteAsync(HttpMethod.Post, "roles", new JsonObject
            {
                ["name"] = RoleName(instance), ["businessunitid@odata.bind"] = $"/businessunits({bu})",
            }, solution, ct);
            if (client.DryRun)
                return true;
            roleId = (string?)created?["roleid"] ?? (await FindAsync(client, instance, ct))!.Value.Id;
            wrote = true;
        }
        else
            roleId = role.Value.Id;

        var (add, remove) = await PlanAsync(client, roleId, table, wanted, ct);
        if (add.Count > 0)
            await client.WriteAsync(HttpMethod.Post, $"roles({roleId})/Microsoft.Dynamics.CRM.AddPrivilegesRole", new JsonObject
            {
                ["Privileges"] = new JsonArray(add.Select(p => (JsonNode)new JsonObject
                {
                    ["PrivilegeId"] = p.Id, ["PrivilegeName"] = p.Name, ["Depth"] = "Global", ["BusinessUnitId"] = bu,
                }).ToArray()),
            }, null, ct);
        foreach (var p in remove)
            await client.WriteAsync(HttpMethod.Post, $"roles({roleId})/Microsoft.Dynamics.CRM.RemovePrivilegeRole", new JsonObject
            {
                ["Privilege"] = new JsonObject { ["@odata.type"] = "Microsoft.Dynamics.CRM.privilege", ["privilegeid"] = p.Id },
            }, null, ct);
        return wrote || add.Count > 0 || remove.Count > 0;
    }

    private static async Task<(string Id, bool Exists)?> FindAsync(DataverseClient client, string instance, CancellationToken ct)
    {
        var row = (await client.ListAsync($"roles?$select=roleid&$filter=name eq {DataverseClient.Literal(RoleName(instance))} and _parentroleid_value eq null", ct)).FirstOrDefault();
        return row == null ? null : ((string)row["roleid"]!, true);
    }

    /// <summary>Wanted privileges missing or below organisation depth, and this table's held privileges that are not wanted.</summary>
    private static async Task<(List<(string Id, string Name)> Add, List<(string Id, string Name)> Remove)> PlanAsync(
        DataverseClient client, string roleId, string table, IReadOnlyList<string> wanted, CancellationToken ct)
    {
        var names = TablePrivileges.Select(v => $"prv{v}{table}").ToList();
        var filter = string.Join(" or ", names.Select(n => $"name eq {DataverseClient.Literal(n)}"));
        var privileges = (await client.ListAsync($"privileges?$select=privilegeid,name&$filter={filter}", ct))
            .ToDictionary(p => (string)p["name"]!, p => (string)p["privilegeid"]!);
        var missing = wanted.Select(v => $"prv{v}{table}").Where(n => !privileges.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new GenerationRefusedException(missing.Select(n => $"privilege {n} does not exist in this environment").ToList());
        var held = (await client.ListAsync($"roleprivilegescollection?$select=privilegeid,privilegedepthmask&$filter=roleid eq {roleId}", ct))
            .ToDictionary(h => ((string)h["privilegeid"]!).ToLowerInvariant(), h => (int?)h["privilegedepthmask"] ?? 0);
        var add = wanted.Select(v => $"prv{v}{table}").Select(n => (Id: privileges[n], Name: n))
            .Where(p => held.GetValueOrDefault(p.Id.ToLowerInvariant()) != Global).ToList();
        var wantedNames = wanted.Select(v => $"prv{v}{table}").ToHashSet(StringComparer.Ordinal);
        var remove = privileges.Where(p => !wantedNames.Contains(p.Key) && held.ContainsKey(p.Value.ToLowerInvariant()))
            .Select(p => (Id: p.Value, Name: p.Key)).ToList();
        return (add, remove);
    }
}
