using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Planning;
using BrokerForge.Keys;

namespace BrokerForge.Descriptors.Rendering;

/// <summary>
/// Everything a binding can read for one canonical request. <see cref="Record"/> holds the supplied
/// cells by column; <see cref="ChangedColumns"/> is the set an Update carries.
/// </summary>
public sealed record BindingContext(
    ServiceInstance Instance,
    string CorrelationId,
    string EnvironmentId,
    string? UserObjectId,
    bool Async,
    IReadOnlyDictionary<string, Cell>? Record,
    ISet<string>? ChangedColumns,
    ExternalKey? Id,
    QueryPlan? Plan,
    PagingValues? Paging);

/// <summary>Values for one source call, set by the paging coordinator.</summary>
public sealed record PagingValues(int PageSize, string? Token, int? Offset, int? PageNumber);
