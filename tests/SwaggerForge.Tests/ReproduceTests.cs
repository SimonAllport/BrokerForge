using System.Text.Json;
using BrokerForge.SwaggerForge;
using YamlDotNet.RepresentationModel;

namespace SwaggerForge.Tests;

/// <summary>Today's sources, re-drafted from their OpenAPI files plus answers, against the committed descriptors and schemas.</summary>
public class ReproduceTests
{
    private static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static Dictionary<string, string> AnswersFile(string name) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(DrafterTests.Fixture(name)))!;

    /// <summary>
    /// The YAML as a tree with sorted keys and scalars as text, so quoting and flow style do not matter. Option set map names
    /// are replaced by their position, because a descriptor's map name is internal to it and changes no behaviour.
    /// </summary>
    internal static string Canonical(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var root = stream.Documents[0].RootNode;
        var names = root is YamlMappingNode m && m.Children.TryGetValue(new YamlScalarNode("optionsets"), out var sets) && sets is YamlMappingNode setMap
            ? setMap.Children.Keys.Select((k, i) => (((YamlScalarNode)k).Value!, $"set{i}")).ToDictionary(x => x.Item1, x => x.Item2)
            : new Dictionary<string, string>();
        return JsonSerializer.Serialize(Tree(root, names), new JsonSerializerOptions { WriteIndented = true });
    }

    private static object? Tree(YamlNode node, Dictionary<string, string> sets, string? parentKey = null) => node switch
    {
        YamlMappingNode map => new SortedDictionary<string, object?>(map.Children.ToDictionary(
            c => parentKey == "optionsets" && sets.TryGetValue(((YamlScalarNode)c.Key).Value!, out var n) ? n : ((YamlScalarNode)c.Key).Value!,
            c => Tree(c.Value, sets, ((YamlScalarNode)c.Key).Value)), StringComparer.Ordinal),
        YamlSequenceNode seq => seq.Children.Select(c => Tree(c, sets)).ToList(),
        YamlScalarNode s when s.Value is { } v && v.StartsWith("optionset:", StringComparison.Ordinal) && sets.TryGetValue(v["optionset:".Length..], out var n) => "optionset:" + n,
        YamlScalarNode s when parentKey == "optionset" && s.Value is { } v && sets.TryGetValue(v, out var n) => n,
        YamlScalarNode s => s.Value,
        _ => null,
    };

    private static DraftResult Draft(string openApi, string answers) =>
        DrafterTests.Ready(DraftService.Review(File.ReadAllText(openApi), new DraftOptions("X", "ext"), AnswersFile(answers)));

    [Fact]
    public void Reproduce_Inventory_ExactlyTheCommittedDescriptors()
    {
        var r = Draft(Path.Combine(Repo, "tools", "mock-api", "inventory-openapi.yaml"), "inventory-answers.json");
        var committed = Directory.GetFiles(Path.Combine(Repo, "descriptors", "InventoryAPI"), "*.yaml").ToDictionary(f => Path.GetFileNameWithoutExtension(f), File.ReadAllText);
        Assert.Equal(committed.Keys.Order(), r.Descriptors.Keys.Order());
        foreach (var (name, yaml) in committed)
            Assert.Equal(Canonical(yaml), Canonical(r.Descriptors[name]));
        Assert.Equal(Canonical(File.ReadAllText(Path.Combine(Repo, "schemas", "InventoryAPI.yaml"))), Canonical(r.SchemaYaml));
    }

    [Fact]
    public void Reproduce_Orders_ListAndGetExactly_RestApartFromWhatTheDefinitionOmits()
    {
        var r = Draft(DrafterTests.Fixture("orders-openapi.yaml"), "orders-answers.json");
        string Committed(string op) => File.ReadAllText(Path.Combine(Repo, "descriptors", "OrdersAPI", op + ".yaml"));
        foreach (var op in new[] { "listOrders", "getOrder" })
            Assert.Equal(Canonical(Committed(op)), Canonical(r.Descriptors[op]));
        // deleteOrder differs only by its X-Correlation header, which the definition does not mention.
        Assert.Equal(Canonical(Committed("deleteOrder").Replace("  headers:\n    X-Correlation: { context: correlationId }\n", "")), Canonical(r.Descriptors["deleteOrder"]));

        // The fixture has no headers, placedBy or retry, and createOrder's account is a fixed value, not instance config.
        foreach (var op in new[] { "createOrder", "updateOrder" })
        {
            var mine = JsonDocument.Parse(Canonical(r.Descriptors[op])).RootElement;
            var theirs = JsonDocument.Parse(Canonical(Committed(op))).RootElement;
            Assert.Equal(theirs.GetProperty("optionsets").GetRawText(), mine.GetProperty("optionsets").GetRawText());
            var template = theirs.GetProperty("request").GetProperty("body").GetProperty("template");
            foreach (var p in template.EnumerateObject().Where(p => p.Name != "placedBy"))
                Assert.Equal(p.Value.GetRawText(), mine.GetProperty("request").GetProperty("body").GetProperty("template").GetProperty(p.Name).GetRawText());
            foreach (var e in mine.GetProperty("errors").EnumerateObject())
                Assert.Equal(theirs.GetProperty("errors").GetProperty(e.Name).GetRawText(), e.Value.GetRawText());
        }
    }

    [Fact]
    public void Review_SameInput_SameOutputAndQuestionOrder()
    {
        var text = File.ReadAllText(DrafterTests.Fixture("orders-openapi.yaml"));
        var a = DraftService.Review(text, DraftServiceTests.Options, AnswersFile("orders-answers.json")).Result!;
        var b = DraftService.Review(text, DraftServiceTests.Options, AnswersFile("orders-answers.json")).Result!;
        Assert.Equal(a.SchemaYaml, b.SchemaYaml);
        Assert.Equal(a.Descriptors, b.Descriptors);

        IEnumerable<string> Keys(DraftReview r) => r.Table!.Questions.Concat(r.Endpoints.SelectMany(e => e.Questions)).Select(q => q.Key);
        var empty = new Dictionary<string, string>();
        Assert.Equal(Keys(DraftService.Review(text, DraftServiceTests.Options, empty)), Keys(DraftService.Review(text, DraftServiceTests.Options, empty)));
    }
}
