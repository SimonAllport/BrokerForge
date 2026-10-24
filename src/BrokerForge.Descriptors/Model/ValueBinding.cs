namespace BrokerForge.Descriptors.Model;

/// <summary>
/// A request value: a literal, or a binding with exactly one source key (descriptor spec 3).
/// The parser fills every source key that was present so the validator can report "zero or more than one".
/// </summary>
public sealed class ValueBinding
{
    /// <summary>Binding source keys. Nothing else may supply a value.</summary>
    public static readonly IReadOnlyList<string> SourceKeys = new[]
    {
        "static", "instance", "secret", "context", "col", "id", "filter", "sort", "param", "computed", "step",
    };

    public static readonly IReadOnlyList<string> ContextNames = new[] { "userObjectId", "correlationId", "environmentId", "async" };

    public static readonly IReadOnlyList<string> ComputedNames = new[] { "uuid", "utcnow" };

    public static readonly IReadOnlyList<string> Formats = new[] { "iso8601", "date", "epochSeconds", "epochMillis", "lower", "upper", "csv", "json" };

    public bool IsLiteral { get; init; }

    public string? Literal { get; init; }

    public string? Static { get; init; }

    public string? Instance { get; init; }

    public string? Secret { get; init; }

    public string? Context { get; init; }

    public string? Col { get; init; }

    /// <summary>"true" for the whole key, or the name of a composite key part.</summary>
    public string? Id { get; init; }

    public string? Filter { get; init; }

    /// <summary>Requested column → source sort field.</summary>
    public IReadOnlyDictionary<string, string>? Sort { get; init; }

    public string? Param { get; init; }

    public string? Computed { get; init; }

    /// <summary>"stepName.captureName" from a previous step.</summary>
    public string? Step { get; init; }

    public IReadOnlyList<string>? Ops { get; init; }

    public string? Format { get; init; }

    public bool? Required { get; init; }

    public bool? OmitIfNull { get; init; }

    public bool? AlwaysSend { get; init; }

    public static ValueBinding FromLiteral(string? value) => new() { IsLiteral = true, Literal = value };

    /// <summary>The source keys present on this binding. Exactly one is valid.</summary>
    public IReadOnlyList<string> PresentSources()
    {
        var present = new List<string>();
        if (Static is not null) present.Add("static");
        if (Instance is not null) present.Add("instance");
        if (Secret is not null) present.Add("secret");
        if (Context is not null) present.Add("context");
        if (Col is not null) present.Add("col");
        if (Id is not null) present.Add("id");
        if (Filter is not null) present.Add("filter");
        if (Sort is not null) present.Add("sort");
        if (Param is not null) present.Add("param");
        if (Computed is not null) present.Add("computed");
        if (Step is not null) present.Add("step");
        return present;
    }

    /// <summary>The single source key, or null when the binding is a literal or malformed.</summary>
    public string? Source => IsLiteral ? null : PresentSources() is { Count: 1 } one ? one[0] : null;
}
