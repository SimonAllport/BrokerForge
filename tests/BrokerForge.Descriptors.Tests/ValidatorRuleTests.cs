using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;

namespace BrokerForge.Descriptors.Tests;

/// <summary>One test per rule in descriptor spec 12. Each starts from a valid descriptor and breaks one thing.</summary>
public class ValidatorRuleTests
{
    private const string ValidList = """
        operation: listThings
        version: 1
        binds: RetrieveMultiple
        table: ext_thing
        request:
          method: GET
          path: /things
          query:
            status: { filter: ext_status, ops: [eq] }
        paging:
          strategy: cursor
          pageSize: { param: limit, max: 50 }
          token: { param: cursor, next: $.next }
        response:
          rows: $.items[*]
          key: $.id
          map:
            ext_name: $.name
            ext_status: $.status
        """;

    private const string ValidCreate = """
        operation: createThing
        version: 1
        binds: Create
        table: ext_thing
        request:
          method: POST
          path: /things
          body:
            template:
              name: { col: ext_name }
        response:
          key: $.id
        """;

    [Fact]
    public void Baselines_are_valid()
    {
        AssertValid(ValidList);
        AssertValid(ValidCreate);
    }

    [Fact]
    public void Rule1_unknown_key_fails_at_parse()
    {
        var ex = Assert.Throws<BrokerConfigurationException>(() => Parse(ValidList + "\nsurprise: true"));
        Assert.Contains("surprise", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rule1_binding_with_two_sources() =>
        AssertRule(1, ValidList.Replace("{ filter: ext_status, ops: [eq] }", "{ filter: ext_status, static: x, ops: [eq] }"));

    [Fact]
    public void Rule2_col_binding_is_not_available_on_RetrieveMultiple() =>
        AssertRule(2, ValidList.Replace("{ filter: ext_status, ops: [eq] }", "{ col: ext_status }"));

    [Fact]
    public void Rule2_filter_needs_ops() =>
        AssertRule(2, ValidList.Replace("{ filter: ext_status, ops: [eq] }", "{ filter: ext_status }"));

    [Fact]
    public void Rule3_path_placeholder_without_pathParams() =>
        AssertRule(3, ValidList.Replace("path: /things", "path: /things/{tenant}"));

    [Fact]
    public void Rule4_authorization_header_is_reserved() =>
        AssertRule(4, ValidList.Replace("  query:", "  headers:\n    Authorization: { static: abc }\n  query:"));

    [Fact]
    public void Rule5_get_must_not_have_a_body() =>
        AssertRule(5, ValidList.Replace("  query:", "  body:\n    template: { a: 1 }\n  query:"));

    [Fact]
    public void Rule6_RetrieveMultiple_needs_paging() =>
        AssertRule(6, ValidList.Replace("paging:\n  strategy: cursor\n  pageSize: { param: limit, max: 50 }\n  token: { param: cursor, next: $.next }\n", ""));

    [Fact]
    public void Rule6_Create_needs_a_key() =>
        AssertRule(6, ValidCreate.Replace("response:\n  key: $.id", "response:\n  unmapped: []"));

    [Fact]
    public void Rule7_unmapped_schema_column()
    {
        var schema = new TableSchema
        {
            LogicalName = "ext_thing",
            Columns = new Dictionary<string, FieldType> { ["ext_name"] = FieldType.String, ["ext_status"] = FieldType.String, ["ext_colour"] = FieldType.String },
        };
        var result = DescriptorValidator.Validate(Parse(ValidList), schema);
        Assert.Contains(result.Issues, i => i.Rule == 7 && i.Path.EndsWith("ext_colour", StringComparison.Ordinal));
    }

    [Fact]
    public void Rule8_default_must_be_Fail() =>
        AssertRule(8, ValidList + "\nerrors:\n  404: NotFound\n  default: NotFound");

    [Fact]
    public void Rule9_retry_needs_idempotent() =>
        AssertRule(9, ValidList + "\nretry:\n  idempotent: false\n  attempts: 3\n  on: [\"503\"]");

    [Fact]
    public void Rule9_create_retry_needs_idempotency_key() =>
        AssertRule(9, ValidCreate + "\nretry:\n  idempotent: true\n  attempts: 3\n  on: [\"503\"]");

    [Fact]
    public void Rule10_hook_needs_a_reason() =>
        AssertRule(10, ValidList + "\nhook:\n  pre: Some.Hook");

    [Fact]
    public void Rule11_too_many_steps() =>
        AssertRule(11, ValidList + "\nsteps:\n  - name: a\n  - name: b\n  - name: main\n  - name: c");

    [Fact]
    public void Rule12_secret_in_body() =>
        AssertRule(12, ValidCreate.Replace("{ col: ext_name }", "{ secret: apiKey }"));

    [Fact]
    public void Rule12_secret_in_path() =>
        AssertRule(12, ValidList.Replace("path: /things", "path: /things/{key}\n  pathParams:\n    key: { secret: apiKey }"));

    private static Model.OperationDescriptor Parse(string yaml) => DescriptorParser.Parse(yaml, "test");

    private static void AssertValid(string yaml)
    {
        var result = DescriptorValidator.Validate(Parse(yaml));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    private static void AssertRule(int rule, string yaml)
    {
        var result = DescriptorValidator.Validate(Parse(yaml));
        Assert.True(result.Issues.Any(i => i.Rule == rule), $"Expected rule {rule}; got: {string.Join("; ", result.Issues)}");
    }
}
