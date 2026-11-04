using BrokerForge.SwaggerForge;

namespace BrokerForge.Cli;

/// <summary>
/// <c>bf draft &lt;openapi&gt; --instance &lt;name&gt; --prefix &lt;p&gt; --table &lt;logical&gt; --out &lt;dir&gt; [--root &lt;dir&gt;]</c>:
/// writes <c>schemas/&lt;instance&gt;.yaml</c> and <c>descriptors/&lt;instance&gt;/*.yaml</c> under the root (default:
/// the current directory) and <c>&lt;out&gt;/&lt;instance&gt;-draft-report.md</c>. Refuses to overwrite any existing file.
/// </summary>
public static class DraftCommand
{
    private const string Usage = "usage: bf draft <openapi> --instance <name> --prefix <p> --table <logical> --out <dir> [--root <dir>]";

    public static int Run(string[] args, TextWriter output)
    {
        if (args.Length < 1 || (args.Length - 1) % 2 != 0)
        {
            output.WriteLine(Usage);
            return 2;
        }
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            options[args[i]] = args[i + 1];
        if (!options.TryGetValue("--instance", out var instance) || !options.TryGetValue("--prefix", out var prefix)
            || !options.TryGetValue("--table", out var table) || !options.TryGetValue("--out", out var outDir)
            || options.Keys.Except(new[] { "--instance", "--prefix", "--table", "--out", "--root" }).Any())
        {
            output.WriteLine(Usage);
            return 2;
        }
        if (!table.StartsWith(prefix + "_", StringComparison.Ordinal))
        {
            output.WriteLine($"--table {table} must start with the prefix {prefix}_");
            return 2;
        }
        var root = Path.GetFullPath(options.GetValueOrDefault("--root") ?? ".");

        var review = DraftService.Review(File.ReadAllText(args[0]), new DraftOptions(instance, prefix), new Dictionary<string, string>());
        foreach (var problem in review.Problems)
            output.WriteLine($"{args[0]}: {problem}");
        if (review.Problems.Count > 0)
            return 1;
        if (review.Table != null && review.Table.LogicalName != table)
        {
            output.WriteLine($"{args[0]}: the definition's table is {review.Table.LogicalName}, not {table}.");
            return 1;
        }
        if (review.Result is not { } draft)
        {
            output.WriteLine($"{args[0]}: the draft is not ready:");
            foreach (var e in review.Endpoints)
                output.WriteLine($"  {e.Method} {e.Path}: {e.Status}{(e.Reason != null ? ": " + e.Reason : "")}");
            return 1;
        }
        if (draft.SchemaYaml.Length == 0)
        {
            output.WriteLine($"{args[0]}: nothing could be drafted; see the skipped operations below.");
            foreach (var s in draft.Skipped)
                output.WriteLine($"  skipped {s.Method} {s.Path}: {s.Reason}");
            return 1;
        }

        var files = new Dictionary<string, string>
        {
            [Path.Combine(root, "schemas", instance + ".yaml")] = draft.SchemaYaml,
            [Path.Combine(Path.IsPathRooted(outDir) ? outDir : Path.Combine(root, outDir), instance + "-draft-report.md")] = Report(args[0], instance, draft),
        };
        foreach (var (operation, yaml) in draft.Descriptors)
            files[Path.Combine(root, "descriptors", instance, operation + ".yaml")] = yaml;

        var existing = files.Keys.Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            foreach (var file in existing)
                output.WriteLine($"{Path.GetRelativePath(root, file)} exists; bf draft does not overwrite files. Move it away or edit it by hand.");
            return 1;
        }
        foreach (var (file, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            output.WriteLine($"wrote {Path.GetRelativePath(root, file)}");
        }
        output.WriteLine($"{draft.Descriptors.Count} descriptors drafted, {draft.Skipped.Count} operations skipped, {draft.Notes.Count} notes to review.");
        return 0;
    }

    private static string Report(string openApi, string instance, DraftResult draft)
    {
        var lines = new List<string>
        {
            $"# {instance} draft report",
            "",
            $"Drafted by `bf draft` from `{Path.GetFileName(openApi)}`. Review every note before the descriptors are used.",
            "",
            "## Drafted",
            "",
        };
        lines.AddRange(draft.Descriptors.Keys.Select(d => $"- `{d}`"));
        lines.AddRange(new[] { "", "## Skipped", "" });
        lines.AddRange(draft.Skipped.Count == 0 ? new[] { "Nothing was skipped." } : draft.Skipped.Select(s => $"- `{s.Method} {s.Path}`: {s.Reason}"));
        lines.AddRange(new[] { "", "## To review", "" });
        lines.AddRange(draft.Notes.Count == 0 ? new[] { "No notes." } : draft.Notes.Select(n => $"- {n}"));
        return string.Join("\n", lines) + "\n";
    }
}
