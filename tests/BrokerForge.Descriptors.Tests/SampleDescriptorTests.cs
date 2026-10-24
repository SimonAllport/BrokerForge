using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Storage;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Descriptors.Tests;

/// <summary>Every descriptor committed under descriptors/ must parse and validate. This is the CI validator gate.</summary>
public class SampleDescriptorTests
{
    public static IEnumerable<object[]> DescriptorFiles() =>
        Directory.EnumerateFiles(RepoPaths.Descriptors, "*.yaml", SearchOption.AllDirectories)
            .Select(f => new object[] { Path.GetRelativePath(RepoPaths.Root, f) });

    [Theory]
    [MemberData(nameof(DescriptorFiles))]
    public void Descriptor_parses_and_validates(string relativePath)
    {
        var descriptor = DescriptorParser.ParseFile(Path.Combine(RepoPaths.Root, relativePath));
        var result = DescriptorValidator.Validate(descriptor);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void File_store_loads_every_orders_operation()
    {
        var store = new FileDescriptorStore(RepoPaths.Descriptors);
        foreach (var op in new[] { "listOrders", "getOrder", "createOrder", "updateOrder", "deleteOrder", "submitOrder" })
        {
            Assert.Equal(op, store.GetAsync("OrdersAPI", op, CancellationToken.None).Result.Operation);
        }
    }
}
