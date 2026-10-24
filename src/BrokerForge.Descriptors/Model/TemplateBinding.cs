using System.Globalization;

namespace BrokerForge.Descriptors.Model;

/// <summary>
/// Reads a binding embedded in a body template. Templates are parsed as plain maps, lists and scalars,
/// so a map whose keys include a binding source key is turned into a <see cref="ValueBinding"/> here.
/// </summary>
public static class TemplateBinding
{
    public static ValueBinding From(IDictionary<object, object> map)
    {
        string? Get(string key) => map.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        bool? GetBool(string key) => Get(key) is { } s ? bool.Parse(s) : null;

        IReadOnlyDictionary<string, string>? sort = null;
        if (map.TryGetValue("sort", out var sortNode) && sortNode is IDictionary<object, object> sortMap)
        {
            sort = sortMap.ToDictionary(kv => kv.Key.ToString()!, kv => Convert.ToString(kv.Value, CultureInfo.InvariantCulture)!);
        }

        IReadOnlyList<string>? ops = null;
        if (map.TryGetValue("ops", out var opsNode) && opsNode is IList<object> opsList)
        {
            ops = opsList.Select(o => o.ToString()!).ToList();
        }

        return new ValueBinding
        {
            Static = Get("static"),
            Instance = Get("instance"),
            Secret = Get("secret"),
            Context = Get("context"),
            Col = Get("col"),
            Id = Get("id"),
            Filter = Get("filter"),
            Sort = sort,
            Param = Get("param"),
            Computed = Get("computed"),
            Step = Get("step"),
            Ops = ops,
            Format = Get("format"),
            Required = GetBool("required"),
            OmitIfNull = GetBool("omitIfNull"),
            AlwaysSend = GetBool("alwaysSend"),
        };
    }
}
