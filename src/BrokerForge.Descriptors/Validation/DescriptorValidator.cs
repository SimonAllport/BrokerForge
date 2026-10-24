using System.Text.RegularExpressions;
using BrokerForge.Abstractions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Validation;

public sealed record ValidationIssue(int Rule, string Path, string Message)
{
    public override string ToString() => $"rule {Rule} at {Path}: {Message}";
}

public sealed class ValidationResult
{
    public ValidationResult(IReadOnlyList<ValidationIssue> issues) => Issues = issues;

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;

    /// <summary>Throws <see cref="BrokerConfigurationException"/> listing every issue.</summary>
    public void ThrowIfInvalid(string operation)
    {
        if (!IsValid)
        {
            throw new BrokerConfigurationException(
                $"Descriptor '{operation}' is invalid: " + string.Join("; ", Issues.Select(i => i.ToString())));
        }
    }
}

/// <summary>
/// Implements the validation rules in descriptor spec 12. Rule 1's unknown-key half is enforced by the parser;
/// rule 7 needs the generated table schema and is skipped when none is supplied.
/// </summary>
public static partial class DescriptorValidator
{
    private const int HostLimitSeconds = 90;
    private const int MinimumHeadroomSeconds = 20;

    private static readonly HashSet<string> VirtualTableMessages = new(StringComparer.Ordinal)
    {
        "RetrieveMultiple", "Retrieve", "Create", "Update", "Delete",
    };

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "GET", "POST", "PUT", "PATCH", "DELETE" };

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Cookie",
    };

    private static readonly HashSet<string> PagingStrategies = new(StringComparer.Ordinal) { "cursor", "offset", "pageNumber", "link", "none" };

    private static readonly HashSet<string> ErrorExceptions = new(StringComparer.Ordinal) { "NotFound", "Conflict", "Forbidden", "Validation", "Fail" };

    private static readonly HashSet<string> RetryOn = new(StringComparer.Ordinal) { "429", "502", "503", "504", "network" };

    public static ValidationResult Validate(OperationDescriptor d, TableSchema? schema = null)
    {
        var issues = new List<ValidationIssue>();
        void Add(int rule, string path, string message) => issues.Add(new ValidationIssue(rule, path, message));

        // Header fields (spec 1, 2).
        if (string.IsNullOrWhiteSpace(d.Operation) || !CamelCase().IsMatch(d.Operation))
        {
            Add(1, "operation", "Required, camelCase.");
        }

        if (d.Version < 1)
        {
            Add(1, "version", "Required, integer of 1 or more.");
        }

        var isCustomApi = d.Binds.StartsWith("customapi:", StringComparison.Ordinal);
        var message = isCustomApi ? "CustomApi" : d.Binds;
        if (!isCustomApi && !VirtualTableMessages.Contains(d.Binds))
        {
            Add(2, "binds", $"'{d.Binds}' is not a message. Use RetrieveMultiple, Retrieve, Create, Update, Delete or customapi:<UniqueName>.");
        }

        if (isCustomApi && d.Binds.Length <= "customapi:".Length)
        {
            Add(2, "binds", "customapi: needs the Custom API unique name.");
        }

        if (!isCustomApi && string.IsNullOrWhiteSpace(d.Table))
        {
            Add(2, "table", "Required for virtual table messages.");
        }

        if (isCustomApi && d.Table is not null)
        {
            Add(2, "table", "Custom API descriptors have no table.");
        }

        // Request (spec 4) and bindings (spec 3).
        ValidateRequest(d.Request, "request", message, Add);

        // Rule 6: paging and keys.
        if (message == "RetrieveMultiple" && d.Paging is null)
        {
            Add(6, "paging", "RetrieveMultiple requires a paging block.");
        }

        if (d.Paging is not null)
        {
            ValidatePaging(d.Paging, Add);
        }

        if (message is "RetrieveMultiple" or "Retrieve" or "Create" && d.Response?.Key is null)
        {
            Add(6, "response.key", $"{message} must return a key.");
        }

        if (message is "RetrieveMultiple" or "Retrieve" && string.IsNullOrWhiteSpace(d.Response?.Rows))
        {
            Add(6, "response.rows", $"{message} must declare where the rows are.");
        }

        if (message == "CustomApi" && d.Response?.Map is not null)
        {
            Add(6, "response.map", "Custom APIs return outputs, not mapped columns.");
        }

        ValidateOptionSetReferences(d, Add);

        // Rule 7: every column mapped or listed as unmapped.
        if (schema is not null && d.Response is not null && message is "RetrieveMultiple" or "Retrieve")
        {
            var mapped = new HashSet<string>(d.Response.Map?.Keys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            mapped.UnionWith(d.Response.Unmapped ?? Enumerable.Empty<string>());
            foreach (var column in schema.Columns.Keys.Where(c => !mapped.Contains(c)))
            {
                Add(7, $"response.map.{column}", "Column is neither mapped nor listed in response.unmapped.");
            }
        }

        // Rule 8: errors.
        if (d.Errors is not null)
        {
            if (!d.Errors.TryGetValue("default", out var fallback) || fallback.Exception != "Fail")
            {
                Add(8, "errors.default", "Must be Fail.");
            }

            foreach (var (status, mapping) in d.Errors)
            {
                if (status != "default" && !(int.TryParse(status, out var code) && code is >= 400 and <= 599))
                {
                    Add(8, $"errors.{status}", "Key must be a 4xx/5xx status or 'default'.");
                }

                if (!ErrorExceptions.Contains(mapping.Exception))
                {
                    Add(8, $"errors.{status}", $"'{mapping.Exception}' is not NotFound, Conflict, Forbidden, Validation or Fail.");
                }
            }
        }

        // Rule 9: retry.
        if (d.Retry is not null)
        {
            if (!d.Retry.Idempotent)
            {
                Add(9, "retry.idempotent", "Automatic retry requires idempotent: true.");
            }

            if (d.Retry.Attempts is < 1 or > 5)
            {
                Add(9, "retry.attempts", "Must be between 1 and 5.");
            }

            foreach (var on in d.Retry.On ?? new List<string>())
            {
                if (!RetryOn.Contains(on))
                {
                    Add(9, "retry.on", $"'{on}' is not a transient failure. Use 429, 502, 503, 504 or network.");
                }
            }

            if (message == "Create" && !(d.Request.Headers ?? new()).Any(h =>
                    string.Equals(h.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase) && h.Value.Computed == "uuid"))
            {
                Add(9, "retry", "Create can only retry with an Idempotency-Key header bound to { computed: uuid }.");
            }
        }

        // Rule 10: hooks need a reason.
        if (d.Hook is not null)
        {
            if (string.IsNullOrWhiteSpace(d.Hook.Reason))
            {
                Add(10, "hook.reason", "Say why the descriptor cannot express this.");
            }

            if (d.Hook.Pre is null && d.Hook.Post is null)
            {
                Add(10, "hook", "Declare pre, post or both.");
            }
        }

        // Rule 11: steps.
        if (d.Steps is not null)
        {
            ValidateSteps(d.Steps, message, Add);
        }

        return new ValidationResult(issues);
    }

    private static void ValidateRequest(RequestSpec r, string path, string message, Action<int, string, string> add)
    {
        if (!Methods.Contains(r.Method))
        {
            add(4, $"{path}.method", $"'{r.Method}' is not GET, POST, PUT, PATCH or DELETE.");
        }

        if (string.IsNullOrWhiteSpace(r.Path) || !(r.Path.StartsWith('/') || r.Path.StartsWith('{')))
        {
            add(4, $"{path}.path", "Required, starting with '/' (or '{location}' in a poll step).");
        }

        // Rule 3: placeholders and pathParams match one to one.
        var placeholders = Placeholder().Matches(r.Path).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var declared = r.PathParams?.Keys.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var missing in placeholders.Except(declared))
        {
            if (missing != "location")
            {
                add(3, $"{path}.path", $"Placeholder {{{missing}}} has no pathParams entry.");
            }
        }

        foreach (var unused in declared.Except(placeholders))
        {
            add(3, $"{path}.pathParams.{unused}", "Declared but not used in the path.");
        }

        // Rule 4: reserved headers.
        foreach (var header in r.Headers?.Keys ?? Enumerable.Empty<string>())
        {
            if (ReservedHeaders.Contains(header))
            {
                add(4, $"{path}.headers.{header}", "Reserved for the service instance's authentication mode.");
            }
        }

        // Rule 5: no body on GET or DELETE.
        if (r.Body is not null && r.Method is "GET" or "DELETE")
        {
            add(5, $"{path}.body", $"{r.Method} must not have a body.");
        }

        if (r.Body is not null && (r.Body.Template is null) == (r.Body.Raw is null))
        {
            add(4, $"{path}.body", "Declare exactly one of template or raw.");
        }

        // Rules 1, 2 and 12 for each binding.
        ValidateBindings(r.PathParams, $"{path}.pathParams", "path", message, add);
        ValidateBindings(r.Query, $"{path}.query", "query", message, add);
        ValidateBindings(r.Headers, $"{path}.headers", "headers", message, add);
        if (r.Body?.Template is not null)
        {
            WalkTemplate(r.Body.Template, $"{path}.body.template", message, add);
        }

        if (r.Body?.Raw is not null && r.Body.Raw.Contains("{{ secret.", StringComparison.Ordinal))
        {
            add(12, $"{path}.body.raw", "secret bindings are not allowed in a body.");
        }
    }

    private static void ValidateBindings(Dictionary<string, ValueBinding>? bindings, string path, string location, string message, Action<int, string, string> add)
    {
        foreach (var (name, binding) in bindings ?? new Dictionary<string, ValueBinding>())
        {
            ValidateBinding(binding, $"{path}.{name}", location, message, add);
        }
    }

    internal static void ValidateBinding(ValueBinding b, string path, string location, string message, Action<int, string, string> add)
    {
        if (b.IsLiteral)
        {
            return;
        }

        var sources = b.PresentSources();
        if (sources.Count != 1)
        {
            add(1, path, sources.Count == 0
                ? "Binding has no source key. Use one of: " + string.Join(", ", ValueBinding.SourceKeys) + "."
                : "Binding has more than one source key: " + string.Join(", ", sources) + ".");
            return;
        }

        var source = sources[0];

        // Rule 12: secrets only in headers and query.
        if (source == "secret" && location is "path" or "body")
        {
            add(12, path, "secret bindings are not allowed in path or body.");
        }

        // Rule 2: source allowed for this message.
        var allowed = source switch
        {
            "col" => message is "Create" or "Update",
            "id" => message is "Retrieve" or "Update" or "Delete" or "CustomApi",
            "filter" or "sort" => message == "RetrieveMultiple" && location is "query" or "path",
            "param" => message == "CustomApi",
            _ => true,
        };

        if (!allowed)
        {
            add(2, path, $"'{source}' bindings are not available for {message} in {location}.");
        }

        if (source == "sort" && location == "path")
        {
            add(2, path, "sort bindings belong in the query.");
        }

        if (source == "context" && !ValueBinding.ContextNames.Contains(b.Context!))
        {
            add(2, path, $"Unknown context '{b.Context}'. Use: {string.Join(", ", ValueBinding.ContextNames)}.");
        }

        if (source == "computed" && !ValueBinding.ComputedNames.Contains(b.Computed!))
        {
            add(2, path, $"Unknown computed value '{b.Computed}'. Use: {string.Join(", ", ValueBinding.ComputedNames)}.");
        }

        if (source == "filter")
        {
            if (b.Ops is null || b.Ops.Count == 0)
            {
                add(2, path, "filter bindings must declare ops.");
            }

            foreach (var op in b.Ops ?? Array.Empty<string>())
            {
                if (!FilterOperators.All.Contains(op))
                {
                    add(2, path, $"'{op}' is not a filter operator.");
                }
            }
        }

        if (b.Format is not null && !ValueBinding.Formats.Contains(b.Format))
        {
            add(2, path, $"Unknown format '{b.Format}'.");
        }
    }

    private static void WalkTemplate(object node, string path, string message, Action<int, string, string> add)
    {
        switch (node)
        {
            case IDictionary<object, object> map when map.Keys.Any(k => ValueBinding.SourceKeys.Contains(k.ToString()!)):
                ValidateBinding(TemplateBinding.From(map), path, "body", message, add);
                break;
            case IDictionary<object, object> map:
                foreach (var (key, child) in map)
                {
                    if (child is not null)
                    {
                        WalkTemplate(child, $"{path}.{key}", message, add);
                    }
                }

                break;
            case IList<object> list:
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is not null)
                    {
                        WalkTemplate(list[i], $"{path}[{i}]", message, add);
                    }
                }

                break;
        }
    }

    private static void ValidatePaging(PagingSpec p, Action<int, string, string> add)
    {
        if (!PagingStrategies.Contains(p.Strategy))
        {
            add(6, "paging.strategy", $"'{p.Strategy}' is not cursor, offset, pageNumber, link or none.");
            return;
        }

        switch (p.Strategy)
        {
            case "cursor" when p.Token?.Param is null || p.Token.Next is null:
                add(6, "paging.token", "cursor paging needs token.param and token.next.");
                break;
            case "offset" when p.Offset?.Param is null:
                add(6, "paging.offset", "offset paging needs offset.param.");
                break;
            case "pageNumber" when p.Page?.Param is null || p.Page.Base is not (0 or 1):
                add(6, "paging.page", "pageNumber paging needs page.param and page.base of 0 or 1.");
                break;
        }

        if (p.Strategy != "none" && p.PageSize?.Param is null && p.Strategy != "link")
        {
            add(6, "paging.pageSize", "Declare the source's page size parameter.");
        }

        if (p.MaxCallsPerPage is < 1 or > 10)
        {
            add(6, "paging.maxCallsPerPage", "Must be between 1 and 10.");
        }
    }

    private static void ValidateOptionSetReferences(OperationDescriptor d, Action<int, string, string> add)
    {
        var all = (d.Response?.Map ?? new()).Concat(d.Response?.Outputs ?? new());
        foreach (var (column, mapping) in all)
        {
            if (mapping.Type?.StartsWith("optionset:", StringComparison.Ordinal) == true)
            {
                var name = mapping.Type["optionset:".Length..];
                if (d.Optionsets is null || !d.Optionsets.ContainsKey(name))
                {
                    add(6, $"response.map.{column}", $"Option set '{name}' is not declared under optionsets.");
                }
            }

            if ((mapping.Path is null) == (mapping.Jsonata is null))
            {
                add(6, $"response.map.{column}", "Declare exactly one of path or jsonata.");
            }
        }
    }

    private static void ValidateSteps(List<StepSpec> steps, string message, Action<int, string, string> add)
    {
        if (steps.Count > 3)
        {
            add(11, "steps", "At most 3 steps in v1.");
        }

        if (steps.Count(s => s.Name == "main") != 1)
        {
            add(11, "steps", "Exactly one step must be named 'main' (the descriptor's own request).");
        }

        var budget = 0;
        foreach (var step in steps)
        {
            if (step.Name != "main" && step.Request is not null)
            {
                ValidateRequest(step.Request, $"steps.{step.Name}.request", message, add);
            }

            if (step.Timeout is not null)
            {
                if (!TryParseSeconds(step.Timeout, out var seconds))
                {
                    add(11, $"steps.{step.Name}.timeout", "Use a duration such as 60s.");
                }
                else
                {
                    budget += seconds;
                }
            }
        }

        if (budget > HostLimitSeconds - MinimumHeadroomSeconds)
        {
            add(11, "steps", $"Step timeouts total {budget}s; keep at least {MinimumHeadroomSeconds}s of the {HostLimitSeconds}s host limit.");
        }
    }

    private static bool TryParseSeconds(string value, out int seconds)
    {
        seconds = 0;
        return value.EndsWith('s') && int.TryParse(value[..^1], out seconds) && seconds > 0;
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"\{([A-Za-z0-9_]+)\}")]
    private static partial Regex Placeholder();
}
