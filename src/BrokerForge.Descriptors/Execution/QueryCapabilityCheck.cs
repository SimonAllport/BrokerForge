using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Execution;

/// <summary>
/// Rejects any part of a RetrieveMultiple query the descriptor does not declare, before anything is sent to the source
/// (descriptor spec 5, HLD 10). The host never filters, sorts or pages client-side.
/// </summary>
public static class QueryCapabilityCheck
{
    public static void Ensure(OperationDescriptor descriptor, QuerySpec query)
    {
        var bindings = AllBindings(descriptor).ToList();

        if (query.Filter is not null)
        {
            CheckGroup(descriptor, bindings, query.Filter, isRoot: true);
        }

        var sortMap = bindings
            .Where(b => b.Sort is not null)
            .SelectMany(b => b.Sort!)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        foreach (var sort in query.OrderBy)
        {
            if (!sortMap.ContainsKey(sort.Column))
            {
                throw new UnsupportedQueryException(sort.Column, sort.Descending ? "sort desc" : "sort asc", "sorting on this column is not declared in the descriptor");
            }
        }

        var strategy = descriptor.Paging?.Strategy ?? "none";
        if (strategy == "none" && (query.Page.Token is not null || query.Page.Number is > 1))
        {
            throw new UnsupportedQueryException("", "page", "the source does not support paging; only the first page can be requested");
        }

        if (strategy is "cursor" or "link" && query.Page.Number is > 1 && query.Page.Token is null)
        {
            throw new UnsupportedQueryException("", "page", $"{strategy} paging cannot jump to page {query.Page.Number} without the previous page's token");
        }
    }

    private static void CheckGroup(OperationDescriptor descriptor, List<ValueBinding> bindings, FilterGroup group, bool isRoot)
    {
        if (group.Op == "or")
        {
            CheckOr(descriptor, group);
        }
        else if (group.Op != "and")
        {
            throw new UnsupportedQueryException("", group.Op, "unknown filter group operator");
        }
        else if (!isRoot && group.Groups.Count > 0)
        {
            throw new UnsupportedQueryException("", "nested", "nested and-groups deeper than one level are not supported");
        }

        foreach (var condition in group.Conditions)
        {
            var declared = bindings.Any(b =>
                b.Filter == condition.Column &&
                (b.Ops ?? Array.Empty<string>()).Contains(group.Op == "or" ? FilterOperators.In : condition.Operator));

            if (!declared)
            {
                throw new UnsupportedQueryException(condition.Column, condition.Operator, "this filter is not declared in the descriptor");
            }
        }

        foreach (var child in group.Groups)
        {
            CheckGroup(descriptor, bindings, child, isRoot: false);
        }
    }

    /// <summary>v1 supports OR only when it collapses to "in" on one column and the descriptor allows it (descriptor spec 5).</summary>
    private static void CheckOr(OperationDescriptor descriptor, FilterGroup group)
    {
        if (descriptor.Filters?.AllowOr != true)
        {
            throw new UnsupportedQueryException(group.Conditions.FirstOrDefault()?.Column ?? "", "or", "OR filters are not allowed by this descriptor");
        }

        var columns = group.Conditions.Select(c => c.Column).Distinct(StringComparer.Ordinal).ToList();
        if (group.Groups.Count > 0 || columns.Count != 1 || group.Conditions.Any(c => c.Operator != FilterOperators.Eq))
        {
            throw new UnsupportedQueryException(columns.FirstOrDefault() ?? "", "or", "only OR of equalities on a single column is supported");
        }
    }

    private static IEnumerable<ValueBinding> AllBindings(OperationDescriptor d) =>
        (d.Request.Query?.Values ?? Enumerable.Empty<ValueBinding>())
            .Concat(d.Request.PathParams?.Values ?? Enumerable.Empty<ValueBinding>());
}
