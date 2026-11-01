using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Schema;
using BrokerForge.SwaggerForge;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace SwaggerForge.Tests;

public class DrafterTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    internal static OpenApiDocument Read(string name, Func<string, string>? edit = null)
    {
        var text = File.ReadAllText(Fixture(name));
        if (edit != null)
        {
            var edited = edit(text);
            Assert.NotEqual(text, edited);
            text = edited;
        }
        var doc = new OpenApiStringReader().Read(text, out var diagnostic);
        Assert.Empty(diagnostic.Errors);
        return doc;
    }

    private static DraftResult Orders(Func<string, string>? edit = null) =>
        Drafter.Draft(Read("orders-openapi.yaml", edit), new DraftOptions("OrdersAPI", "ext", "ext_order"));

    private static DraftResult Widgets() => Drafter.Draft(Read("unsupported.yaml"), new DraftOptions("WidgetsAPI", "ext", "ext_widget"));

    private static OperationDescriptor Descriptor(DraftResult r, string operation) => DescriptorParser.Parse(r.Descriptors[operation], operation + ".yaml");

    [Fact]
    public void Draft_OrdersOpenApi_EmitsFiveCrudDescriptorsThatValidate()
    {
        var r = Orders();
        Assert.Equal(new[] { "createOrder", "deleteOrder", "getOrder", "listOrders", "updateOrder" }, r.Descriptors.Keys.Order());
        var schema = SchemaParser.Parse(r.SchemaYaml, "OrdersAPI.yaml");
        foreach (var (operation, yaml) in r.Descriptors)
        {
            var d = DescriptorParser.Parse(yaml, operation + ".yaml");
            Assert.Empty(DescriptorValidator.Validate(d, ReservedNames.For(new[] { "X-Api-Key" }), schema));
        }
        Assert.Equal("RetrieveMultiple", Descriptor(r, "listOrders").Binds);
        Assert.Equal("Create", Descriptor(r, "createOrder").Binds);
        Assert.Equal("Update", Descriptor(r, "updateOrder").Binds);
        Assert.Equal("Delete", Descriptor(r, "deleteOrder").Binds);
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void Draft_OrdersOpenApi_SchemaMatchesOrdersColumns()
    {
        var r = Orders();
        var schema = SchemaParser.Parse(r.SchemaYaml, "OrdersAPI.yaml");
        Assert.Equal("ext_order", schema.Table);
        var list = Descriptor(r, "listOrders");
        // The same source fields and types as the hand-written Orders descriptors and schemas/OrdersAPI.yaml.
        var byPath = list.Response!.Map.ToDictionary(m => m.Value.Path, m => (Column: m.Key, schema.Columns[m.Key].Type));
        Assert.Equal(new[] { "$.customer.ref", "$.lines", "$.placedAt", "$.reference", "$.state.code", "$.totals.gross" }, byPath.Keys.Order());
        Assert.Equal(("ext_reference", "string"), byPath["$.reference"]);
        Assert.Equal(("ext_statecode", "optionset"), byPath["$.state.code"]);
        Assert.Equal(("ext_customerref", "string"), byPath["$.customer.ref"]);
        Assert.Equal(("ext_placedat", "datetime"), byPath["$.placedAt"]);
        Assert.Equal(("ext_lines", "memo"), byPath["$.lines"]);
        Assert.Equal(("ext_totalsgross", "decimal"), byPath["$.totals.gross"]);
        Assert.Equal("json", list.Response.Map["ext_lines"].Type);
        Assert.Equal(new Dictionary<string, int> { ["open"] = 100000000, ["shipped"] = 100000001, ["cancelled"] = 100000002 }, schema.Columns["ext_statecode"].Options);
        Assert.Equal(20, schema.Columns["ext_reference"].MaxLength);
        Assert.Equal("ext_reference", schema.PrimaryName);
        Assert.Contains(r.Notes, n => n.Contains("primaryName") && n.Contains("ext_reference"));

        var create = Descriptor(r, "createOrder");
        Assert.Equal(new InstanceBinding("accountId", BindingModifiers.None), create.Request.PathParams["accountId"]);
        Assert.Contains(r.Notes, n => n.Contains("accountId") && n.Contains("instance"));
        Assert.Equal(new PathKey("$.id"), create.Response!.Key);
    }

    [Fact]
    public void Draft_FiltersFromQueryParameters()
    {
        var list = Descriptor(Orders(), "listOrders");
        var filters = list.Request.Query.Where(q => q.Value is FilterBinding).ToDictionary(q => q.Key, q => (FilterBinding)q.Value);
        Assert.Equal("ext_statecode", filters["status"].Column);
        Assert.Equal("ext_customerref", filters["customer"].Column);
        Assert.Equal(new[] { "eq" }, filters["status"].Ops);
        Assert.DoesNotContain(list.Request.Query, q => q.Key == "sort");
        Assert.False(list.SortStable);
    }

    [Fact]
    public void Draft_CursorPagingIdentified()
    {
        var paging = Descriptor(Orders(), "listOrders").Paging!;
        Assert.Equal("cursor", paging.Strategy);
        Assert.Equal(new PageSizeSpec("limit", 100), paging.PageSize);
        Assert.Equal(new TokenSpec("cursor", "$.meta.nextCursor"), paging.Token);
    }

    [Fact]
    public void Draft_OffsetPagingWithTotalIdentified()
    {
        var r = Widgets();
        var paging = Descriptor(r, "listWidgets").Paging!;
        Assert.Equal("offset", paging.Strategy);
        Assert.Equal(new OffsetSpec("offset", "$.total"), paging.Offset);
        Assert.Equal(new PageSizeSpec("limit", 50), paging.PageSize);
        Assert.Equal("$.items[*]", Descriptor(r, "listWidgets").Response!.Rows);
    }

    [Fact]
    public void Draft_PagingUnidentifiable_ListSkippedWithReason()
    {
        var r = Orders(t => t.Replace("        - { name: cursor, in: query, schema: { type: string } }\n", ""));
        Assert.DoesNotContain("listOrders", r.Descriptors.Keys);
        var skipped = Assert.Single(r.Skipped);
        Assert.Equal(("GET", "/v2/orders"), (skipped.Method, skipped.Path));
        Assert.Contains("paging not identifiable", skipped.Reason);
        Assert.Contains("getOrder", r.Descriptors.Keys);
    }

    [Fact]
    public void Draft_UnsupportedShapes_SkippedWithReasons()
    {
        var r = Widgets();
        string Reason(string path) => Assert.Single(r.Skipped, s => s.Path == path).Reason;
        Assert.Contains("oneOf", Reason("/widgets/{widgetId}/variant"));
        Assert.Contains("refers to itself", Reason("/widgets/{widgetId}/tree"));
        Assert.Contains("operationId", Reason("/widgets/{widgetId}/ping"));
        Assert.Contains("response schema", Reason("/widgets/{widgetId}/touch"));
        Assert.Equal(4, r.Skipped.Count);
        Assert.Equal(new[] { "adjustWidget", "getWidget", "listWidgets" }, r.Descriptors.Keys.Order());
    }

    [Fact]
    public void Draft_ActionBecomesCustomApi()
    {
        var r = Widgets();
        var d = Descriptor(r, "adjustWidget");
        Assert.Equal("customapi:ext_AdjustWidget", d.Binds);
        Assert.Equal(new ParamBinding("WidgetId", BindingModifiers.None), d.Request.PathParams["widgetId"]);
        var api = SchemaParser.Parse(r.SchemaYaml, "WidgetsAPI.yaml").CustomApis["ext_AdjustWidget"];
        Assert.Equal(new ParamSchema("int", true), api.Inputs["WidgetId"]);
        Assert.Equal(new ParamSchema("int", true), api.Inputs["Delta"]);
        Assert.Equal(new ParamSchema("string", false), api.Inputs["Reason"]);
        Assert.Equal(new Dictionary<string, string> { ["OnHand"] = "int", ["AdjustedAt"] = "datetime" }, api.Outputs);
        Assert.Equal(new MapEntry("$.adjustedAt", "datetime", true), d.Response!.Outputs["AdjustedAt"]);
    }

    [Fact]
    public void Draft_IntegerKeyNotAColumn()
    {
        var r = Widgets();
        var schema = SchemaParser.Parse(r.SchemaYaml, "WidgetsAPI.yaml");
        Assert.Equal(new[] { "ext_name", "ext_onhand" }, schema.Columns.Keys);
        Assert.Equal("ext_name", schema.PrimaryName);
        Assert.Equal(new PathKey("$.id"), Descriptor(r, "getWidget").Response!.Key);
    }
}
