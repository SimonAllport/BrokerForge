using BrokerForge.Contracts;
using BrokerForge.Descriptors.Execution;
using BrokerForge.Descriptors.Parsing;

namespace BrokerForge.Descriptors.Tests;

/// <summary>Fail loudly: anything the descriptor does not declare is rejected before the source is called.</summary>
public class QueryCapabilityCheckTests
{
    private static readonly Model.OperationDescriptor ListOrders =
        DescriptorParser.ParseFile(Path.Combine(RepoPaths.Descriptors, "OrdersAPI", "listOrders.yaml"));

    [Fact]
    public void Declared_filter_and_sort_pass()
    {
        var query = Query(Condition("ext_status", FilterOperators.Eq, "100000000"));
        query.OrderBy.Add(new SortSpec { Column = "ext_name" });
        QueryCapabilityCheck.Ensure(ListOrders, query);
    }

    [Fact]
    public void Undeclared_column_names_the_column()
    {
        var ex = Assert.Throws<UnsupportedQueryException>(() =>
            QueryCapabilityCheck.Ensure(ListOrders, Query(Condition("ext_name", FilterOperators.Eq, "ORD-0001"))));
        Assert.Equal("ext_name", ex.Column);
        Assert.Equal("eq", ex.Operator);
    }

    [Fact]
    public void Undeclared_operator_on_declared_column_is_rejected() =>
        Assert.Throws<UnsupportedQueryException>(() =>
            QueryCapabilityCheck.Ensure(ListOrders, Query(Condition("ext_customerref", FilterOperators.Ne, "CUST-1"))));

    [Fact]
    public void Or_is_rejected_unless_the_descriptor_allows_it()
    {
        var query = Query(Condition("ext_status", FilterOperators.Eq, "1"), Condition("ext_status", FilterOperators.Eq, "2"));
        query.Filter!.Op = "or";
        var ex = Assert.Throws<UnsupportedQueryException>(() => QueryCapabilityCheck.Ensure(ListOrders, query));
        Assert.Equal("or", ex.Operator);
    }

    [Fact]
    public void Undeclared_sort_is_rejected()
    {
        var query = Query();
        query.OrderBy.Add(new SortSpec { Column = "ext_customerref", Descending = true });
        Assert.Throws<UnsupportedQueryException>(() => QueryCapabilityCheck.Ensure(ListOrders, query));
    }

    [Fact]
    public void Cursor_paging_cannot_jump_pages_without_a_token()
    {
        var query = Query();
        query.Page.Number = 3;
        Assert.Throws<UnsupportedQueryException>(() => QueryCapabilityCheck.Ensure(ListOrders, query));
    }

    private static QuerySpec Query(params FilterCondition[] conditions)
    {
        var query = new QuerySpec();
        if (conditions.Length > 0)
        {
            query.Filter = new FilterGroup();
            query.Filter.Conditions.AddRange(conditions);
        }

        return query;
    }

    private static FilterCondition Condition(string column, string op, params string[] values)
    {
        var condition = new FilterCondition { Column = column, Operator = op };
        condition.Values.AddRange(values);
        return condition;
    }
}
