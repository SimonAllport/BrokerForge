using System;
using System.Collections;
using System.Collections.Generic;
using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk.Query;

namespace BrokerForge.Plugin;

/// <summary>
/// QueryExpression → canonical <see cref="QuerySpec"/>. Anything the contract cannot express throws
/// <see cref="UnsupportedQueryException"/>; nothing is dropped. Whether the descriptor supports what remains is the host's check.
/// </summary>
public static class QueryTranslator
{
    public static QuerySpec Translate(QueryExpression query)
    {
        if (query.LinkEntities.Count > 0)
        {
            throw new UnsupportedQueryException(query.LinkEntities[0].LinkToEntityName, "link", "joins (link-entities) are not supported in v1");
        }

        if (query.Distinct)
        {
            throw new UnsupportedQueryException("", "distinct", "distinct queries are not supported");
        }

        var spec = new QuerySpec { AllColumns = query.ColumnSet.AllColumns };
        spec.Columns.AddRange(query.ColumnSet.Columns);

        if (query.Criteria != null && (query.Criteria.Conditions.Count > 0 || query.Criteria.Filters.Count > 0))
        {
            spec.Filter = TranslateFilter(query.Criteria);
        }

        foreach (var order in query.Orders)
        {
            if (!string.IsNullOrEmpty(order.Alias) || !string.IsNullOrEmpty(order.EntityName))
            {
                throw new UnsupportedQueryException(order.AttributeName, "sort", "sorting on linked or aliased columns is not supported");
            }

            spec.OrderBy.Add(new SortSpec { Column = order.AttributeName, Descending = order.OrderType == OrderType.Descending });
        }

        if (query.TopCount.HasValue)
        {
            spec.Page.Size = query.TopCount.Value;
        }
        else if (query.PageInfo != null)
        {
            if (query.PageInfo.Count > 0)
            {
                spec.Page.Size = query.PageInfo.Count;
            }

            if (query.PageInfo.PageNumber > 0)
            {
                spec.Page.Number = query.PageInfo.PageNumber;
            }

            if (!string.IsNullOrEmpty(query.PageInfo.PagingCookie))
            {
                spec.Page.Token = query.PageInfo.PagingCookie;
            }

            if (query.PageInfo.ReturnTotalRecordCount)
            {
                throw new UnsupportedQueryException("", "count", "total record counts are not supported");
            }
        }

        return spec;
    }

    private static FilterGroup TranslateFilter(FilterExpression filter)
    {
        var group = new FilterGroup { Op = filter.FilterOperator == LogicalOperator.Or ? "or" : "and" };

        foreach (var condition in filter.Conditions)
        {
            group.Conditions.Add(TranslateCondition(condition));
        }

        foreach (var child in filter.Filters)
        {
            if (child.Conditions.Count > 0 || child.Filters.Count > 0)
            {
                group.Groups.Add(TranslateFilter(child));
            }
        }

        return group;
    }

    private static FilterCondition TranslateCondition(ConditionExpression condition)
    {
        if (!string.IsNullOrEmpty(condition.EntityName))
        {
            throw new UnsupportedQueryException(condition.AttributeName, condition.Operator.ToString(), "conditions on linked tables are not supported");
        }

        var result = new FilterCondition { Column = condition.AttributeName };
        var values = Flatten(condition.Values);

        switch (condition.Operator)
        {
            case ConditionOperator.Equal: result.Operator = FilterOperators.Eq; break;
            case ConditionOperator.NotEqual: result.Operator = FilterOperators.Ne; break;
            case ConditionOperator.GreaterThan: result.Operator = FilterOperators.Gt; break;
            case ConditionOperator.GreaterEqual: result.Operator = FilterOperators.Ge; break;
            case ConditionOperator.LessThan: result.Operator = FilterOperators.Lt; break;
            case ConditionOperator.LessEqual: result.Operator = FilterOperators.Le; break;
            case ConditionOperator.In: result.Operator = FilterOperators.In; break;
            case ConditionOperator.Null: result.Operator = FilterOperators.Null; break;
            case ConditionOperator.NotNull: result.Operator = FilterOperators.NotNull; break;
            case ConditionOperator.BeginsWith:
                result.Operator = FilterOperators.Like;
                break;
            case ConditionOperator.Like:
                // Only "begins with" (text%) is supported; any other wildcard would need client-side filtering.
                var pattern = values.Count == 1 ? values[0] as string : null;
                if (pattern == null || !pattern.EndsWith("%", StringComparison.Ordinal) ||
                    pattern.IndexOf('%') != pattern.Length - 1 || pattern.IndexOf('_') >= 0 || pattern.IndexOf('[') >= 0)
                {
                    throw new UnsupportedQueryException(condition.AttributeName, "like", "only 'begins with' patterns (text%) are supported");
                }

                result.Operator = FilterOperators.Like;
                values = new List<object> { pattern.Substring(0, pattern.Length - 1) };
                break;
            default:
                throw new UnsupportedQueryException(condition.AttributeName, condition.Operator.ToString(), "this operator is not supported");
        }

        foreach (var value in values)
        {
            result.Values.Add(EntityMapper.ToField(condition.AttributeName, value).Value ?? "");
        }

        return result;
    }

    private static List<object> Flatten(IEnumerable values)
    {
        var flat = new List<object>();
        foreach (var value in values)
        {
            if (value is IEnumerable nested && !(value is string))
            {
                foreach (var inner in nested)
                {
                    flat.Add(inner);
                }
            }
            else
            {
                flat.Add(value);
            }
        }

        return flat;
    }
}
