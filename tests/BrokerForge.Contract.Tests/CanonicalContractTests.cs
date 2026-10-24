using BrokerForge.Contracts;

namespace BrokerForge.Contract.Tests;

/// <summary>The wire format both sides depend on (HLD 7.1).</summary>
public class CanonicalContractTests
{
    [Fact]
    public void Request_round_trips_with_camelCase_names()
    {
        var request = new CanonicalRequest
        {
            CorrelationId = "c-1",
            EnvironmentId = "local-dev",
            ServiceInstance = "OrdersAPI",
            Table = "ext_order",
            Message = BrokerMessage.RetrieveMultiple,
            Caller = new CallerContext { UserObjectId = "u-1", Async = true },
            Query = new QuerySpec
            {
                Columns = { "ext_name" },
                Filter = new FilterGroup { Conditions = { new FilterCondition { Column = "ext_status", Operator = "in", Values = { "1", "2" } } } },
                OrderBy = { new SortSpec { Column = "ext_name", Descending = true } },
                Page = new PageSpec { Size = 50, Token = "abc" },
            },
        };

        var json = CanonicalJson.Serialize(request);
        Assert.Contains("\"serviceInstance\":\"OrdersAPI\"", json);
        Assert.Contains("\"message\":\"RetrieveMultiple\"", json);
        Assert.DoesNotContain("\"record\"", json);

        var back = CanonicalJson.Deserialize<CanonicalRequest>(json);
        Assert.Equal(BrokerMessage.RetrieveMultiple, back.Message);
        Assert.True(back.Caller.Async);
        Assert.Equal(new[] { "1", "2" }, back.Query!.Filter!.Conditions[0].Values);
        Assert.Equal("abc", back.Query.Page.Token);
    }

    [Fact]
    public void Response_with_rows_round_trips()
    {
        var response = new CanonicalResponse
        {
            CorrelationId = "c-2",
            Rows = new List<CanonicalRow>
            {
                new() { Id = Guid.NewGuid().ToString("D"), Fields = { new FieldValue("ext_name", FieldType.String, "ORD-0001"), new FieldValue("ext_total", FieldType.Decimal, "12.50") } },
            },
            Next = "token-2",
        };

        var back = CanonicalJson.Deserialize<CanonicalResponse>(CanonicalJson.Serialize(response));
        Assert.Equal(FieldType.Decimal, back.Rows![0].Fields[1].Type);
        Assert.Equal("token-2", back.Next);
        Assert.Null(back.Error);
    }

    public static IEnumerable<object[]> TypedErrors() => new[]
    {
        new object[] { new UnsupportedQueryException("ext_name", "ne", "not declared"), typeof(UnsupportedQueryException) },
        new object[] { new ConnectionRequiredException("connect first", "https://host/connect/x"), typeof(ConnectionRequiredException) },
        new object[] { new SourceException(500, "boom"), typeof(SourceException) },
        new object[] { new SourceTimeoutException("slow"), typeof(SourceTimeoutException) },
        new object[] { new MappingException("$.id", "missing"), typeof(MappingException) },
        new object[] { new BrokerUnavailableException("down"), typeof(BrokerUnavailableException) },
        new object[] { new BrokerConfigurationException("bad"), typeof(BrokerConfigurationException) },
        new object[] { new SourceMappedException(ErrorCode.NotFound, "gone"), typeof(SourceMappedException) },
    };

    [Theory]
    [MemberData(nameof(TypedErrors))]
    public void Every_error_survives_the_hop_typed(BrokerForgeException original, Type expected)
    {
        original.CorrelationId = "c-3";
        var envelope = CanonicalResponse.Failure("c-3", original.ToError());
        var back = CanonicalJson.Deserialize<CanonicalResponse>(CanonicalJson.Serialize(envelope));
        var rebuilt = BrokerForgeException.FromError(back.Error!);

        Assert.IsType(expected, rebuilt);
        Assert.Equal(original.Code, rebuilt.Code);
        Assert.Equal("c-3", rebuilt.CorrelationId);
    }

    [Fact]
    public void Unsupported_query_keeps_column_and_operator()
    {
        var error = new UnsupportedQueryException("ext_name", "ne", "not declared").ToError();
        var rebuilt = (UnsupportedQueryException)BrokerForgeException.FromError(error);
        Assert.Equal("ext_name", rebuilt.Column);
        Assert.Equal("ne", rebuilt.Operator);
    }

    [Fact]
    public void Unknown_message_name_fails_rather_than_guessing()
    {
        var request = CanonicalJson.Deserialize<CanonicalRequest>("{\"contractVersion\":\"1\",\"message\":\"Upsert\"}");
        Assert.Throws<ArgumentException>(() => request.Message);
    }
}
