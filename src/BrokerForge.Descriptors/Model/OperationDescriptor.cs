namespace BrokerForge.Descriptors.Model;

/// <summary>One operation descriptor (descriptor spec 1). Property names map to camelCase YAML keys; unknown keys fail parsing.</summary>
public sealed class OperationDescriptor
{
    public string Operation { get; set; } = "";

    public int Version { get; set; }

    /// <summary>RetrieveMultiple, Retrieve, Create, Update, Delete, or customapi:&lt;UniqueName&gt; (descriptor spec 2).</summary>
    public string Binds { get; set; } = "";

    public string? Table { get; set; }

    public RequestSpec Request { get; set; } = new();

    public ResponseSpec? Response { get; set; }

    public PagingSpec? Paging { get; set; }

    public FiltersSpec? Filters { get; set; }

    public bool? SortStable { get; set; }

    /// <summary>Key: status code ("404") or "default".</summary>
    public Dictionary<string, ErrorMapping>? Errors { get; set; }

    public List<StepSpec>? Steps { get; set; }

    public HookSpec? Hook { get; set; }

    public CacheSpec? Cache { get; set; }

    public RetrySpec? Retry { get; set; }

    /// <summary>Option set name → source value → Dataverse integer, plus optional "_unknown": "error" | "null".</summary>
    public Dictionary<string, Dictionary<string, string>>? Optionsets { get; set; }
}

public sealed class RequestSpec
{
    public string Method { get; set; } = "";

    public string Path { get; set; } = "";

    public Dictionary<string, ValueBinding>? PathParams { get; set; }

    public Dictionary<string, ValueBinding>? Query { get; set; }

    public Dictionary<string, ValueBinding>? Headers { get; set; }

    public BodySpec? Body { get; set; }
}

public sealed class BodySpec
{
    public string? ContentType { get; set; }

    /// <summary>Structured template: nested maps, lists and scalars; a map holding a binding source key is a binding.</summary>
    public object? Template { get; set; }

    /// <summary>Raw body with {{ source.name }} placeholders, for XML and other formats.</summary>
    public string? Raw { get; set; }
}

public sealed class ResponseSpec
{
    /// <summary>JSONPath to the row array (RetrieveMultiple) or the single object (Retrieve).</summary>
    public string? Rows { get; set; }

    /// <summary>JSONPath string; composite map of part → JSONPath; or { header, pattern } for Location-style keys.</summary>
    public object? Key { get; set; }

    public Dictionary<string, ColumnMapping>? Map { get; set; }

    public List<string>? Unmapped { get; set; }

    /// <summary>Custom API output parameter → mapping.</summary>
    public Dictionary<string, ColumnMapping>? Outputs { get; set; }
}

/// <summary>A JSONPath string, or { path, type }, or { jsonata }.</summary>
public sealed class ColumnMapping
{
    public string? Path { get; set; }

    public string? Type { get; set; }

    public string? Jsonata { get; set; }
}

public sealed class PagingSpec
{
    /// <summary>cursor | offset | pageNumber | link | none.</summary>
    public string Strategy { get; set; } = "";

    public PagingParam? PageSize { get; set; }

    public PagingParam? Token { get; set; }

    public PagingParam? Offset { get; set; }

    public PagingParam? Page { get; set; }

    public int? MaxCallsPerPage { get; set; }
}

public sealed class PagingParam
{
    public string? Param { get; set; }

    public int? Max { get; set; }

    /// <summary>Cursor strategy: JSONPath to the next token.</summary>
    public string? Next { get; set; }

    /// <summary>pageNumber strategy: 0 or 1.</summary>
    public int? Base { get; set; }

    /// <summary>offset/pageNumber: JSONPath to a total count, when the source returns one.</summary>
    public string? Total { get; set; }
}

public sealed class FiltersSpec
{
    public bool AllowOr { get; set; }

    public string? OrStyle { get; set; }
}

/// <summary>A scalar exception name, or { exception, message }.</summary>
public sealed class ErrorMapping
{
    public string Exception { get; set; } = "";

    public string? Message { get; set; }
}

public sealed class StepSpec
{
    public string Name { get; set; } = "";

    public Dictionary<string, string>? When { get; set; }

    public RequestSpec? Request { get; set; }

    public Dictionary<string, string>? PathFrom { get; set; }

    public Dictionary<string, string>? Capture { get; set; }

    public Dictionary<string, string>? Until { get; set; }

    public string? Every { get; set; }

    public string? Timeout { get; set; }
}

public sealed class HookSpec
{
    public string? Pre { get; set; }

    public string? Post { get; set; }

    public string? Reason { get; set; }
}

public sealed class CacheSpec
{
    public string? Ttl { get; set; }

    public bool? PerUser { get; set; }
}

public sealed class RetrySpec
{
    public bool Idempotent { get; set; }

    public int Attempts { get; set; }

    public List<string>? On { get; set; }
}
