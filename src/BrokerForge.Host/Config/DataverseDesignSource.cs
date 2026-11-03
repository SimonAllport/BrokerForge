using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Connections;
using BrokerForge.Host.Ingress;
using Microsoft.Extensions.Options;

namespace BrokerForge.Host.Config;

/// <summary>
/// Reads an environment's connection designs from its own Dataverse (HLD D-34, docs/findings/portal-carrier.md): every
/// published web resource named <c>&lt;prefix&gt;_/brokerforge/&lt;Instance&gt;/design.xml</c>, whatever the publisher
/// prefix, and the environment variables <c>bf_&lt;Instance&gt;_BaseUrl</c> and <c>bf_&lt;Instance&gt;_Auth</c> (the
/// current value, else the default). The org URL is the environment's <see cref="AllowedCaller.OrgUrl"/>.
/// A design that cannot be read fails the whole environment with every problem listed; Dataverse that cannot be read
/// at all is a <see cref="DesignSourceUnavailableException"/>, so the provider can keep serving the last good store.
/// </summary>
public sealed partial class DataverseDesignSource(IOptions<TrustOptions> trust, IDataverseReader reader) : IDesignSource
{
    /// <summary>
    /// Every candidate design. The filter has no <c>_</c> in it: Dataverse treats <c>_</c> in contains/endswith as a
    /// one-character wildcard. Each name found is then checked against the exact layout in code.
    /// </summary>
    public static readonly string DesignQuery = "webresourceset?$select=webresourceid,name,webresourcetype,content&$filter="
        + Uri.EscapeDataString("contains(name,'/brokerforge/') and endswith(name,'/design.xml')");

    private const string VariablesQuery =
        "environmentvariabledefinitions?$select=schemaname,defaultvalue&$expand=environmentvariabledefinition_environmentvariablevalue($select=value)&$filter=";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*_/brokerforge/(?<instance>[^/]+)/design\.xml$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DesignName();

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IReadOnlyList<(ConnectionDesign Design, EnvironmentSettings Settings)>> LoadAsync(string environmentId, CancellationToken ct)
    {
        var org = OrgUrl(environmentId);
        var problems = new List<string>();
        var found = new Dictionary<string, List<(string Name, ConnectionDesign Design)>>(StringComparer.Ordinal);
        using (var resources = await Read(environmentId, org, DesignQuery, ct))
            foreach (var row in resources.RootElement.GetProperty("value").EnumerateArray())
                if (ReadDesign(row, problems) is { } d)
                {
                    if (!found.TryGetValue(d.Design.Instance, out var list))
                        found[d.Design.Instance] = list = new();
                    list.Add(d);
                }
        foreach (var (instance, list) in found.Where(f => f.Value.Count > 1))
            problems.Add($"Instance {instance} has two connection designs in environment {environmentId}: "
                + string.Join(" and ", list.Select(d => d.Name).Order(StringComparer.Ordinal)));

        var instances = found.Keys.Order(StringComparer.Ordinal).ToList();
        var variables = instances.Count == 0 ? new Dictionary<string, string?>() : await ReadVariables(environmentId, org, instances, problems, ct);
        var loaded = new List<(ConnectionDesign, EnvironmentSettings)>();
        foreach (var instance in instances.Where(i => found[i].Count == 1))
        {
            var auth = new Dictionary<string, string>(StringComparer.Ordinal);
            if (variables.GetValueOrDefault($"bf_{instance}_Auth") is { } text && !string.IsNullOrWhiteSpace(text)
                && !TryReadOverrides(text, auth))
                problems.Add($"bf_{instance}_Auth in environment {environmentId} must be a JSON object of strings, for example {{\"scope\":\"api://x/.default\"}}");
            loaded.Add((found[instance][0].Design, new EnvironmentSettings(variables.GetValueOrDefault($"bf_{instance}_BaseUrl"), auth)));
        }
        if (problems.Count > 0)
            throw new ConfigurationException($"Connection designs in environment {environmentId} could not be read:" + string.Concat(problems.Select(p => "\n - " + p)));
        return loaded;
    }

    private string OrgUrl(string environmentId)
    {
        var orgs = trust.Value.AllowedCallers
            .Where(c => Guid.TryParse(c.EnvironmentId, out var id) && Guid.TryParse(environmentId, out var wanted) && id == wanted && !string.IsNullOrWhiteSpace(c.OrgUrl))
            .Select(c => c.OrgUrl!.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Startup refuses both cases when designs are enabled; this guards a source built some other way.
        return orgs.Count == 1 ? orgs[0] : throw new ConfigurationException(
            $"Environment {environmentId} needs exactly one OrgUrl in Trust:AllowedCallers to read its connection designs; it has {orgs.Count}.");
    }

    private async Task<JsonDocument> Read(string environmentId, string org, string relativeUrl, CancellationToken ct)
    {
        try
        {
            return await reader.GetAsync(org, relativeUrl, ct);
        }
        catch (DataverseUnavailableException ex)
        {
            throw new DesignSourceUnavailableException($"Could not read connection designs for environment {environmentId} from Dataverse: {ex.Message}.");
        }
    }

    private static (string Name, ConnectionDesign Design)? ReadDesign(JsonElement row, List<string> problems)
    {
        var name = row.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "(web resource without a name)";
        var match = DesignName().Match(name);
        if (!match.Success)
        {
            problems.Add($"{name} is not named <prefix>_/brokerforge/<Instance>/design.xml");
            return null;
        }
        if (!row.TryGetProperty("webresourcetype", out var t) || t.ValueKind != JsonValueKind.Number || t.GetInt32() != 4)
        {
            problems.Add($"{name} has web resource type {(row.TryGetProperty("webresourcetype", out var shown) ? shown.GetRawText() : "(none)")}, not 4 (XML)");
            return null;
        }
        if (!row.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String || c.GetString() is not { Length: > 0 } content)
        {
            problems.Add($"{name} has no content");
            return null;
        }
        try
        {
            var xml = StrictUtf8.GetString(Convert.FromBase64String(content)).TrimStart('﻿');
            var design = DesignSerializer.Parse(DesignCarrier.Unwrap(xml, name), name);
            var instance = match.Groups["instance"].Value;
            if (design.Instance != instance)
            {
                problems.Add($"{name} holds the design of instance {design.Instance}; the name must use the instance name");
                return null;
            }
            return (name, design);
        }
        catch (FormatException) { problems.Add($"{name}: content is not base64"); }
        catch (DecoderFallbackException) { problems.Add($"{name}: content is not UTF-8"); }
        catch (DesignFormatException ex) { problems.Add(ex.Message); }
        return null;
    }

    /// <summary>Each instance's variables by schema name: the current value when there is one, else the default.</summary>
    private async Task<Dictionary<string, string?>> ReadVariables(string environmentId, string org, IReadOnlyList<string> instances, List<string> problems, CancellationToken ct)
    {
        var wanted = instances.SelectMany(i => new[] { $"bf_{i}_BaseUrl", $"bf_{i}_Auth" }).ToList();
        // eq, not startswith: a pattern function would treat the _ in the names as a wildcard.
        var filter = string.Join(" or ", wanted.Select(w => $"schemaname eq '{w.Replace("'", "''", StringComparison.Ordinal)}'"));
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using var doc = await Read(environmentId, org, VariablesQuery + Uri.EscapeDataString(filter), ct);
        foreach (var row in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var schema = row.TryGetProperty("schemaname", out var s) ? s.GetString() : null;
            if (schema == null || !wanted.Contains(schema, StringComparer.OrdinalIgnoreCase))
                continue;
            string? value = row.TryGetProperty("defaultvalue", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (row.TryGetProperty("environmentvariabledefinition_environmentvariablevalue", out var current) && current.ValueKind == JsonValueKind.Array)
            {
                if (current.GetArrayLength() > 1)
                    problems.Add($"{schema} in environment {environmentId} has {current.GetArrayLength()} current values; it may have one");
                else if (current.GetArrayLength() == 1 && current[0].TryGetProperty("value", out var cv) && cv.ValueKind == JsonValueKind.String)
                    value = cv.GetString();
            }
            values[schema] = value;
        }
        return values;
    }

    private static bool TryReadOverrides(string text, Dictionary<string, string> into)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind != JsonValueKind.String || !into.TryAdd(p.Name, p.Value.GetString()!))
                    return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
