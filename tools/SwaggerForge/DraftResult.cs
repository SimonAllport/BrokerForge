namespace BrokerForge.SwaggerForge;

/// <summary><see cref="Prefix"/> is the publisher prefix (ext); <see cref="Table"/> the logical name of the table to draft (ext_item).</summary>
public sealed record DraftOptions(string ServiceInstance, string Prefix, string Table);

/// <summary>
/// The drafted schema file and descriptors (operation name to YAML), what was skipped and why, and
/// <see cref="Notes"/>: defaults and choices a person must review before the draft is used.
/// </summary>
public sealed record DraftResult(
    string SchemaYaml,
    IReadOnlyDictionary<string, string> Descriptors,
    IReadOnlyList<SkippedOperation> Skipped,
    IReadOnlyList<string> Notes);

public sealed record SkippedOperation(string Method, string Path, string Reason);

/// <summary>An OpenAPI shape SwaggerForge will not draft. Caught per operation and reported as skipped.</summary>
internal sealed class UnsupportedShapeException(string reason) : Exception(reason);
