using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

[Collection(MockApiCollection.Name)]
public class OrdersEndToEndTests(MockApiFixture mock) : HostTestBase(mock)
{
    [Fact]
    public async Task List_StatusFilterAndCursorPaging()
    {
        var names = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var (status, response, error) = await Host.Execute(List("ext_order", 25, Where("ext_status", Ops.Eq, "100000000"), token));
            Assert.True(status == 200, error?.Message);
            Assert.All(response!.Rows, r => Assert.Equal("100000000", Value(r, "ext_status")));
            names.AddRange(response.Rows.Select(r => Value(r, "ext_name")!));
            token = response.Next;
            pages++;
        } while (token != null);
        Assert.Equal(40, names.Count);
        Assert.Equal(40, names.Distinct().Count());
        Assert.Equal(2, pages);
    }

    [Fact]
    public async Task List_RowIdsAreReversibleKeys()
    {
        var (_, response, _) = await Host.Execute(List("ext_order", 3));
        Assert.Equal(Id("ord_0001"), response!.Rows[0].Id);
    }

    [Fact]
    public async Task Retrieve_ByReversibleKey()
    {
        var request = HostFactory.Request("ext_order", Messages.Retrieve);
        request.Id = Id("ord_0005");
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal(request.Id, response!.Record.Id);
        Assert.Equal("REF-0005", Value(response.Record, "ext_name"));
        Assert.Equal("CUST-05", Value(response.Record, "ext_customerref"));
    }

    [Fact]
    public async Task Create_ReturnsEncodedKey()
    {
        var request = HostFactory.Request("ext_order", Messages.Create);
        request.Record = new() { Cell("ext_customerref", "CUST-77"), Cell("ext_lines_json", """[{"sku":"A","qty":1}]""") };
        var (status, response, error) = await Host.Execute(request);
        Assert.True(status == 200, error?.Message);
        Assert.Equal(Id("ord_0121"), response!.Id);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = response.Id;
        var (_, created, _) = await Host.Execute(get);
        Assert.Equal("CUST-77", Value(created!.Record, "ext_customerref"));
    }

    [Fact]
    public async Task Create_MissingCustomer_MappedValidation400()
    {
        var request = HostFactory.Request("ext_order", Messages.Create);
        request.Record = new() { Cell("ext_lines_json", "[]") };
        var (status, _, error) = await Host.Execute(request);
        Assert.Equal(400, status);
        Assert.Equal("Validation", error!.Error);
        Assert.Contains("customer.ref is required", error.Message);
    }

    [Fact]
    public async Task Update_SendsChangedColumnsOnly()
    {
        var update = HostFactory.Request("ext_order", Messages.Update);
        update.Id = Id("ord_0002");
        update.Record = new() { Cell("ext_customerref", "CUST-99") };
        var (status, _, error) = await Host.Execute(update);
        Assert.True(status == 200, error?.Message);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = update.Id;
        var (_, after, _) = await Host.Execute(get);
        Assert.Equal("CUST-99", Value(after!.Record, "ext_customerref"));
        Assert.Equal("REF-0002", Value(after.Record, "ext_name"));
    }

    [Fact]
    public async Task Delete_ThenRetrieve404()
    {
        var delete = HostFactory.Request("ext_order", Messages.Delete);
        delete.Id = Id("ord_0003");
        Assert.Equal(200, (await Host.Execute(delete)).Status);

        var get = HostFactory.Request("ext_order", Messages.Retrieve);
        get.Id = delete.Id;
        var (status, _, error) = await Host.Execute(get);
        Assert.Equal(404, status);
        Assert.Equal("NotFound", error!.Error);
    }
}
