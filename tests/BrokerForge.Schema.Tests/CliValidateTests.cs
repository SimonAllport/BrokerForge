using BrokerForge.Cli;

namespace BrokerForge.Schema.Tests;

public class CliValidateTests
{
    private static (int Code, string Output) Run(params string[] args)
    {
        var output = new StringWriter();
        var code = ValidateCommand.Run(args, output);
        return (code, output.ToString());
    }

    [Fact]
    public void Cli_Validate_RepoDescriptors_ExitsZero()
    {
        var root = SchemaParserTests.RepoRoot;
        var (code, output) = Run(Path.Combine(root, "descriptors"), "--schemas", Path.Combine(root, "schemas"));
        Assert.True(code == 0, output);
    }

    [Fact]
    public void Cli_Validate_BrokenDescriptor_ExitsOneWithFileAndRule()
    {
        var root = SchemaParserTests.RepoRoot;
        var temp = Directory.CreateTempSubdirectory("bf-validate-");
        try
        {
            var descriptors = Directory.CreateDirectory(Path.Combine(temp.FullName, "descriptors", "OrdersAPI"));
            var schemas = Directory.CreateDirectory(Path.Combine(temp.FullName, "schemas"));
            File.Copy(Path.Combine(root, "schemas", "OrdersAPI.yaml"), Path.Combine(schemas.FullName, "OrdersAPI.yaml"));
            var list = File.ReadAllText(Path.Combine(root, "descriptors", "OrdersAPI", "listOrders.yaml"));
            Assert.Contains("    ext_total:       { path: $.totals.gross, type: decimal }\n", list);
            File.WriteAllText(Path.Combine(descriptors.FullName, "listOrders.yaml"), list.Replace("    ext_total:       { path: $.totals.gross, type: decimal }\n", ""));
            File.WriteAllText(Path.Combine(descriptors.FullName, "getOrder.yaml"), "operation: getOrder\ncolour: red\n");

            var (code, output) = Run(Path.Combine(temp.FullName, "descriptors"), "--schemas", schemas.FullName);
            Assert.Equal(1, code);
            Assert.Contains("listOrders.yaml: rule 7 response.map: ", output);
            Assert.Contains("ext_total", output);
            Assert.Contains("getOrder.yaml: rule 1 ", output);
        }
        finally
        {
            temp.Delete(true);
        }
    }

    [Fact]
    public void Cli_Validate_MissingSchema_ExitsOne()
    {
        var root = SchemaParserTests.RepoRoot;
        var temp = Directory.CreateTempSubdirectory("bf-validate-");
        try
        {
            var (code, output) = Run(Path.Combine(root, "descriptors"), "--schemas", temp.FullName);
            Assert.Equal(1, code);
            Assert.Contains("OrdersAPI.yaml", output);
        }
        finally
        {
            temp.Delete(true);
        }
    }
}
