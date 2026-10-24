using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace BrokerForge.Contracts;

/// <summary>
/// The one request shape the plug-in sends to the broker host for every message (HLD 7.1).
/// It carries intent, not source syntax.
/// </summary>
[DataContract]
public sealed class CanonicalRequest
{
    [DataMember(Name = "contractVersion", Order = 0)]
    public string ContractVersion { get; set; } = ContractVersions.Current;

    [DataMember(Name = "correlationId", Order = 1)]
    public string CorrelationId { get; set; } = "";

    [DataMember(Name = "environmentId", Order = 2)]
    public string EnvironmentId { get; set; } = "";

    [DataMember(Name = "serviceInstance", Order = 3)]
    public string ServiceInstance { get; set; } = "";

    /// <summary>Virtual table logical name. Absent for Custom APIs.</summary>
    [DataMember(Name = "table", Order = 4, EmitDefaultValue = false)]
    public string? Table { get; set; }

    [DataMember(Name = "message", Order = 5)]
    public string MessageName { get; set; } = "";

    /// <summary>Parsed <see cref="MessageName"/>. Throws on an unknown value rather than guessing.</summary>
    [IgnoreDataMember]
    public BrokerMessage Message
    {
        get => (BrokerMessage)Enum.Parse(typeof(BrokerMessage), MessageName, ignoreCase: false);
        set => MessageName = value.ToString();
    }

    /// <summary>Custom API unique name when <see cref="Message"/> is <see cref="BrokerMessage.CustomApi"/>.</summary>
    [DataMember(Name = "customApi", Order = 6, EmitDefaultValue = false)]
    public string? CustomApi { get; set; }

    [DataMember(Name = "caller", Order = 7)]
    public CallerContext Caller { get; set; } = new CallerContext();

    /// <summary>RetrieveMultiple only.</summary>
    [DataMember(Name = "query", Order = 8, EmitDefaultValue = false)]
    public QuerySpec? Query { get; set; }

    /// <summary>Dataverse record GUID ("D" format) for Retrieve, Update and Delete. The host decodes it to the source key.</summary>
    [DataMember(Name = "id", Order = 9, EmitDefaultValue = false)]
    public string? Id { get; set; }

    /// <summary>Create: all supplied columns. Update: changed columns only.</summary>
    [DataMember(Name = "record", Order = 10, EmitDefaultValue = false)]
    public List<FieldValue>? Record { get; set; }

    /// <summary>Custom API input parameters.</summary>
    [DataMember(Name = "inputs", Order = 11, EmitDefaultValue = false)]
    public List<FieldValue>? Inputs { get; set; }
}

/// <summary>Who the call is for. Trusted by the host only because the caller authenticated as the plug-in (HLD 9.2).</summary>
[DataContract]
public sealed class CallerContext
{
    /// <summary>Entra object ID of the initiating user.</summary>
    [DataMember(Name = "userObjectId", Order = 0, EmitDefaultValue = false)]
    public string? UserObjectId { get; set; }

    /// <summary>True when running in an async plug-in or flow with no interactive user (HLD 9.3).</summary>
    [DataMember(Name = "async", Order = 1)]
    public bool Async { get; set; }
}

[DataContract]
public sealed class QuerySpec
{
    [DataMember(Name = "allColumns", Order = 0)]
    public bool AllColumns { get; set; }

    [DataMember(Name = "columns", Order = 1)]
    public List<string> Columns { get; set; } = new List<string>();

    [DataMember(Name = "filter", Order = 2, EmitDefaultValue = false)]
    public FilterGroup? Filter { get; set; }

    [DataMember(Name = "orderBy", Order = 3)]
    public List<SortSpec> OrderBy { get; set; } = new List<SortSpec>();

    [DataMember(Name = "page", Order = 4)]
    public PageSpec Page { get; set; } = new PageSpec();
}

/// <summary>An and/or group of conditions and nested groups.</summary>
[DataContract]
public sealed class FilterGroup
{
    /// <summary>"and" or "or".</summary>
    [DataMember(Name = "op", Order = 0)]
    public string Op { get; set; } = "and";

    [DataMember(Name = "conditions", Order = 1)]
    public List<FilterCondition> Conditions { get; set; } = new List<FilterCondition>();

    [DataMember(Name = "groups", Order = 2)]
    public List<FilterGroup> Groups { get; set; } = new List<FilterGroup>();
}

[DataContract]
public sealed class FilterCondition
{
    [DataMember(Name = "col", Order = 0)]
    public string Column { get; set; } = "";

    /// <summary>One of <see cref="FilterOperators"/>.</summary>
    [DataMember(Name = "op", Order = 1)]
    public string Operator { get; set; } = FilterOperators.Eq;

    /// <summary>Invariant string values. One for comparison operators, many for "in", none for null checks.</summary>
    [DataMember(Name = "values", Order = 2)]
    public List<string> Values { get; set; } = new List<string>();
}

[DataContract]
public sealed class SortSpec
{
    [DataMember(Name = "col", Order = 0)]
    public string Column { get; set; } = "";

    [DataMember(Name = "desc", Order = 1)]
    public bool Descending { get; set; }
}

[DataContract]
public sealed class PageSpec
{
    [DataMember(Name = "size", Order = 0, EmitDefaultValue = false)]
    public int? Size { get; set; }

    /// <summary>1-based page number Dataverse asked for.</summary>
    [DataMember(Name = "number", Order = 1, EmitDefaultValue = false)]
    public int? Number { get; set; }

    /// <summary>Opaque token from the previous response's <c>next</c>.</summary>
    [DataMember(Name = "token", Order = 2, EmitDefaultValue = false)]
    public string? Token { get; set; }
}
