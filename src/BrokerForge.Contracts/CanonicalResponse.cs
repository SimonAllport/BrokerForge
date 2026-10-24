using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;

namespace BrokerForge.Contracts;

/// <summary>
/// The host's reply. Exactly one of <see cref="Rows"/>, <see cref="Record"/>, <see cref="Id"/>,
/// <see cref="Outputs"/> or <see cref="Error"/> is meaningful, depending on the message.
/// Domain errors travel here with HTTP 200; non-2xx statuses mean transport or trust failures (HLD 7.1).
/// </summary>
[DataContract]
public sealed class CanonicalResponse
{
    [DataMember(Name = "contractVersion", Order = 0)]
    public string ContractVersion { get; set; } = ContractVersions.Current;

    [DataMember(Name = "correlationId", Order = 1)]
    public string CorrelationId { get; set; } = "";

    /// <summary>RetrieveMultiple.</summary>
    [DataMember(Name = "rows", Order = 2, EmitDefaultValue = false)]
    public List<CanonicalRow>? Rows { get; set; }

    /// <summary>RetrieveMultiple: token for the next page, or null when there is none.</summary>
    [DataMember(Name = "next", Order = 3, EmitDefaultValue = false)]
    public string? Next { get; set; }

    /// <summary>Retrieve.</summary>
    [DataMember(Name = "record", Order = 4, EmitDefaultValue = false)]
    public CanonicalRow? Record { get; set; }

    /// <summary>Create: GUID of the new record ("D" format).</summary>
    [DataMember(Name = "id", Order = 5, EmitDefaultValue = false)]
    public string? Id { get; set; }

    /// <summary>Custom API output parameters.</summary>
    [DataMember(Name = "outputs", Order = 6, EmitDefaultValue = false)]
    public List<FieldValue>? Outputs { get; set; }

    [DataMember(Name = "error", Order = 7, EmitDefaultValue = false)]
    public BrokerError? Error { get; set; }

    public static CanonicalResponse Failure(string correlationId, BrokerError error) =>
        new CanonicalResponse { CorrelationId = correlationId, Error = error };
}

[DataContract]
public sealed class CanonicalRow
{
    /// <summary>Dataverse GUID ("D" format), already encoded by the host's key strategy.</summary>
    [DataMember(Name = "id", Order = 0)]
    public string Id { get; set; } = "";

    [DataMember(Name = "fields", Order = 1)]
    public List<FieldValue> Fields { get; set; } = new List<FieldValue>();
}

/// <summary>
/// A typed value. <see cref="Value"/> is always an invariant string so both serialisers agree:
/// numbers use invariant culture, booleans "true"/"false", date-times ISO 8601 round-trip ("o") in UTC,
/// dates "yyyy-MM-dd", GUIDs "D", option sets the integer value. Null means null.
/// </summary>
[DataContract]
public sealed class FieldValue
{
    public FieldValue() { }

    public FieldValue(string name, FieldType type, string? value)
    {
        Name = name;
        Type = type;
        Value = value;
    }

    [DataMember(Name = "name", Order = 0)]
    public string Name { get; set; } = "";

    [DataMember(Name = "type", Order = 1)]
    public string TypeName { get; set; } = nameof(FieldType.String);

    [IgnoreDataMember]
    public FieldType Type
    {
        get => (FieldType)Enum.Parse(typeof(FieldType), TypeName, ignoreCase: false);
        set => TypeName = value.ToString();
    }

    [DataMember(Name = "value", Order = 2)]
    public string? Value { get; set; }

    public static string Format(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
}

/// <summary>A typed failure. Never contains secrets; source body excerpts are redacted before they get here.</summary>
[DataContract]
public sealed class BrokerError
{
    [DataMember(Name = "code", Order = 0)]
    public string CodeName { get; set; } = nameof(ErrorCode.Source);

    [IgnoreDataMember]
    public ErrorCode Code
    {
        get => (ErrorCode)Enum.Parse(typeof(ErrorCode), CodeName, ignoreCase: false);
        set => CodeName = value.ToString();
    }

    [DataMember(Name = "message", Order = 1)]
    public string Message { get; set; } = "";

    [DataMember(Name = "correlationId", Order = 2, EmitDefaultValue = false)]
    public string? CorrelationId { get; set; }

    /// <summary>UnsupportedQuery: the column. Mapping: the path.</summary>
    [DataMember(Name = "column", Order = 3, EmitDefaultValue = false)]
    public string? Column { get; set; }

    /// <summary>UnsupportedQuery: the operator, sort or paging feature.</summary>
    [DataMember(Name = "operator", Order = 4, EmitDefaultValue = false)]
    public string? Operator { get; set; }

    /// <summary>Mapping: the JSONPath or binding that failed.</summary>
    [DataMember(Name = "path", Order = 5, EmitDefaultValue = false)]
    public string? Path { get; set; }

    /// <summary>Source: HTTP status (or adapter equivalent).</summary>
    [DataMember(Name = "statusCode", Order = 6, EmitDefaultValue = false)]
    public int? StatusCode { get; set; }

    /// <summary>ConnectionRequired: where the user grants consent or stores credentials.</summary>
    [DataMember(Name = "connectUrl", Order = 7, EmitDefaultValue = false)]
    public string? ConnectUrl { get; set; }

    public override string ToString()
    {
        var parts = new[]
        {
            $"{CodeName}: {Message}",
            Column is null ? null : $"column={Column}",
            Operator is null ? null : $"operator={Operator}",
            Path is null ? null : $"path={Path}",
            StatusCode is null ? null : $"status={StatusCode}",
            CorrelationId is null ? null : $"correlationId={CorrelationId}",
        };
        return string.Join(" | ", parts.Where(p => p != null));
    }
}
