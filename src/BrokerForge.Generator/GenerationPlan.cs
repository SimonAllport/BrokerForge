using BrokerForge.Schema;

namespace BrokerForge.Generator;

/// <param name="Instance">Service instance name; the data source record is found by it.</param>
/// <param name="Solution">Unique name of the source solution that receives the table and Custom APIs.</param>
/// <param name="Publisher">Unique name of the publisher whose prefix the schema uses.</param>
public sealed record GenerateOptions(string Instance, string Solution, string Publisher, string HostUrl, string Scope, bool DryRun);

/// <summary><see cref="Writes"/> lists the write calls made, or planned in a dry run; empty when nothing changed.</summary>
public sealed record GenerationResult(IReadOnlyList<string> Writes);

/// <summary>The schema and the environment disagree in a way the generator will not change. Nothing was written.</summary>
public sealed class GenerationRefusedException(IReadOnlyList<string> differences)
    : Exception("The generator made no changes:" + string.Concat(differences.Select(d => "\n - " + d)))
{
    public IReadOnlyList<string> Differences { get; } = differences;
}

/// <summary>A column as it exists in Dataverse, described with schema type names (descriptor spec 14).</summary>
public sealed record ExistingColumn(string LogicalName, string Type, string? ExternalName, int? MaxLength, int? Precision, IReadOnlyList<int>? Options);

public sealed record ExistingTable(string? TableType, string? DataProviderId, string? DataSourceId, string? ExternalName, IReadOnlyList<ExistingColumn> Columns);

public sealed record ExistingCustomApi(string PluginTypeId, IReadOnlyDictionary<string, (int Type, bool Optional)> Inputs, IReadOnlyDictionary<string, int> Outputs);

/// <summary>
/// Compares the schema with what exists. The generator only adds: a changed column, a column the schema no
/// longer has, or a changed Custom API is a difference, and any difference refuses the whole run.
/// </summary>
public static class GenerationPlan
{
    /// <summary>Custom API parameter type codes (customapirequestparameter.type).</summary>
    public static readonly IReadOnlyDictionary<string, int> ParamTypeCodes = new Dictionary<string, int>
    {
        ["bool"] = 0, ["datetime"] = 1, ["decimal"] = 2, ["int"] = 7, ["string"] = 10, ["guid"] = 12,
    };

    public sealed record Result(IReadOnlyList<string> Differences, IReadOnlyList<string> ColumnsToAdd, IReadOnlyList<string> ApisToAdd);

    public static Result Compare(SourceSchema schema, ExistingTable? table, IReadOnlyDictionary<string, ExistingCustomApi?> apis,
        string providerId, string? dataSourceId, string? customApiPluginTypeId)
    {
        var differences = new List<string>();
        var add = new List<string>();
        if (schema.Table is { } name)
        {
            if (table == null)
            {
                add.AddRange(schema.Columns.Keys.Where(c => c != schema.PrimaryName));
            }
            else
            {
                if (table.TableType != "Virtual")
                    differences.Add($"{name} exists as a {table.TableType ?? "(unknown)"} table, not a virtual table");
                if (!SameId(table.DataProviderId, providerId))
                    differences.Add($"{name} DataProviderId is {table.DataProviderId ?? "(none)"}, not the BrokerForge provider {providerId}");
                if (dataSourceId != null && !SameId(table.DataSourceId, dataSourceId))
                    differences.Add($"{name} DataSourceId is {table.DataSourceId ?? "(none)"}, not the {schema.Table} data source record {dataSourceId}");
                if (table.ExternalName != schema.ExternalName)
                    differences.Add($"{name} ExternalName is {table.ExternalName ?? "(none)"}, the schema says {schema.ExternalName}");
                var existing = table.Columns.ToDictionary(c => c.LogicalName);
                foreach (var (column, declared) in schema.Columns)
                {
                    if (!existing.TryGetValue(column, out var actual))
                    {
                        add.Add(column);
                        continue;
                    }
                    differences.AddRange(CompareColumn(name, column, declared, actual));
                }
                foreach (var column in existing.Keys.Where(c => !schema.Columns.ContainsKey(c)))
                    differences.Add($"{name}.{column} exists but is not in the schema; the generator does not delete columns");
            }
        }

        var apisToAdd = new List<string>();
        foreach (var (api, declared) in schema.CustomApis)
        {
            if (apis.GetValueOrDefault(api) is not { } actual)
            {
                apisToAdd.Add(api);
                continue;
            }
            if (customApiPluginTypeId != null && !SameId(actual.PluginTypeId, customApiPluginTypeId))
                differences.Add($"Custom API {api} runs plug-in type {actual.PluginTypeId}, not BrokerForge.Plugin.CustomApiPlugin {customApiPluginTypeId}");
            var wantedInputs = declared.Inputs.ToDictionary(i => i.Key, i => (ParamTypeCodes[i.Value.Type], !i.Value.Required));
            differences.AddRange(CompareSets($"Custom API {api} input", wantedInputs, actual.Inputs, v => $"type {v.Item1}{(v.Item2 ? ", optional" : ", required")}"));
            var wantedOutputs = declared.Outputs.ToDictionary(o => o.Key, o => ParamTypeCodes[o.Value]);
            differences.AddRange(CompareSets($"Custom API {api} output", wantedOutputs, actual.Outputs, v => $"type {v}"));
        }
        return new Result(differences, add, apisToAdd);
    }

    private static IEnumerable<string> CompareColumn(string table, string column, ColumnSchema declared, ExistingColumn actual)
    {
        var where = $"{table}.{column}";
        if (actual.Type != declared.Type)
        {
            yield return $"{where} is {actual.Type} in Dataverse but {declared.Type} in the schema";
            yield break;
        }
        if (actual.ExternalName != column)
            yield return $"{where} ExternalName is {actual.ExternalName ?? "(none)"}, expected {column}";
        if (declared.MaxLength is { } length && actual.MaxLength != length)
            yield return $"{where} maxLength is {actual.MaxLength} in Dataverse but {length} in the schema";
        if (declared.Precision is { } precision && actual.Precision != precision)
            yield return $"{where} precision is {actual.Precision} in Dataverse but {precision} in the schema";
        if (declared.Options is { } options && !options.Values.Order().SequenceEqual(actual.Options?.Order() ?? Enumerable.Empty<int>()))
            yield return $"{where} options are {string.Join(", ", actual.Options ?? Array.Empty<int>())} in Dataverse but {string.Join(", ", options.Values)} in the schema";
    }

    private static IEnumerable<string> CompareSets<T>(string what, IReadOnlyDictionary<string, T> wanted, IReadOnlyDictionary<string, T> actual, Func<T, string> describe)
    {
        foreach (var (name, value) in wanted)
            if (!actual.TryGetValue(name, out var existing))
                yield return $"{what} {name} is in the schema but not in Dataverse";
            else if (!EqualityComparer<T>.Default.Equals(existing, value))
                yield return $"{what} {name} is {describe(existing)} in Dataverse but {describe(value)} in the schema";
        foreach (var name in actual.Keys.Where(n => !wanted.ContainsKey(n)))
            yield return $"{what} {name} is in Dataverse but not in the schema";
    }

    public static bool SameId(string? a, string? b) => a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
