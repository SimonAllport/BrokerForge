using Azure.Core;
using Azure.Identity;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Generator;
using BrokerForge.Schema;

namespace BrokerForge.Cli;

/// <summary>
/// <c>bf generate --org &lt;url&gt; --schemas &lt;dir&gt; --descriptors &lt;dir&gt; --instance &lt;name&gt; --solution &lt;unique&gt;
/// --publisher &lt;unique&gt; --host-url &lt;url&gt; --scope &lt;scope&gt; [--dry-run]</c>. Validates the instance's descriptors
/// against its schema, then runs the generator as the az CLI user (falling back to DefaultAzureCredential).
/// </summary>
public static class GenerateCommand
{
    private const string Usage = "usage: bf generate --org <url> --schemas <dir> --descriptors <dir> --instance <name> --solution <unique> --publisher <unique> --host-url <url> --scope <scope> [--dry-run]";
    private static readonly string[] Required = { "--org", "--schemas", "--descriptors", "--instance", "--solution", "--publisher", "--host-url", "--scope" };

    public static async Task<int> RunAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var dryRun = args.Contains("--dry-run");
        var rest = args.Where(a => a != "--dry-run").ToArray();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < rest.Length; i += 2)
            options[rest[i]] = rest[i + 1];
        if (rest.Length % 2 != 0 || Required.Any(r => !options.ContainsKey(r)) || options.Keys.Except(Required).Any())
        {
            output.WriteLine(Usage);
            return 2;
        }
        var instance = options["--instance"];
        var schemaFile = Path.Combine(options["--schemas"], instance + ".yaml");
        SourceSchema schema;
        try
        {
            schema = SchemaParser.Parse(File.ReadAllText(schemaFile), schemaFile);
        }
        catch (Exception ex) when (ex is SchemaException or IOException)
        {
            output.WriteLine(ex.Message);
            return 1;
        }

        // The host refuses to start on a descriptor that disagrees with the schema, so do not create metadata for one.
        var problems = new List<string>();
        var bound = new HashSet<string>();
        var dir = Path.Combine(options["--descriptors"], instance);
        foreach (var file in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            try
            {
                var d = DescriptorParser.Parse(File.ReadAllText(file), Path.GetFileName(file));
                problems.AddRange(DescriptorValidator.Validate(d, ReservedNames.For(Array.Empty<string>()), schema).Select(e => $"{Path.GetFileName(file)}: {e}"));
                if (d.CustomApi != null)
                    bound.Add(d.CustomApi);
            }
            catch (DescriptorException ex)
            {
                problems.Add(ex.Message);
            }
        }
        problems.AddRange(schema.CustomApis.Keys.Where(a => !bound.Contains(a)).Select(a => $"Custom API {a} is in the schema but no descriptor binds it"));
        if (problems.Count > 0)
        {
            output.WriteLine($"{instance}: descriptors do not match the schema; nothing was generated:");
            foreach (var p in problems)
                output.WriteLine(" - " + p);
            return 1;
        }

        var org = options["--org"].TrimEnd('/');
        var credential = new ChainedTokenCredential(new AzureCliCredential(), new DefaultAzureCredential());
        var context = new TokenRequestContext(new[] { org + "/.default" });
        using var http = new HttpClient { BaseAddress = new Uri(org + "/api/data/v9.2/"), Timeout = TimeSpan.FromMinutes(5) };
        var client = new DataverseClient(http, async c => (await credential.GetTokenAsync(context, c)).Token, dryRun);
        try
        {
            var result = await Generator.Generator.RunAsync(client, schema,
                new GenerateOptions(instance, options["--solution"], options["--publisher"], options["--host-url"], options["--scope"], dryRun), ct);
            foreach (var write in result.Writes)
                output.WriteLine((dryRun ? "would " : "") + write);
            output.WriteLine(result.Writes.Count == 0 ? $"{instance}: nothing to change" : $"{instance}: {result.Writes.Count} write(s){(dryRun ? " planned" : "")}");
            return 0;
        }
        catch (Exception ex) when (ex is GenerationRefusedException or DataverseException)
        {
            output.WriteLine(ex.Message);
            return 1;
        }
    }
}
