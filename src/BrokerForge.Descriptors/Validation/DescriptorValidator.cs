using System.Text.RegularExpressions;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;

namespace BrokerForge.Descriptors.Validation;

/// <summary>A validation failure. <see cref="Rule"/> is the descriptor spec 12 rule number.</summary>
public sealed record DescriptorError(int Rule, string YamlPath, string Message)
{
    public override string ToString() => $"rule {Rule}: {YamlPath}: {Message}";
}

/// <summary>
/// Whole-descriptor rules (descriptor spec 12). Rule 1 is enforced by the parser; rules 10 and 11
/// cover features the parser rejects in phase 1; rule 7 needs the table schema and starts in phase 2.
/// </summary>
public static partial class DescriptorValidator
{
    private enum Location { Path, Query, Header, Body }

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex PathPlaceholderRegex();

    public static IReadOnlyList<DescriptorError> Validate(OperationDescriptor d, ReservedNames reserved)
    {
        var errors = new List<DescriptorError>();
        var r = d.Request;

        foreach (var (name, binding) in r.PathParams)
            CheckBinding(d, binding, Location.Path, $"request.pathParams.{name}", inTemplate: false, errors);
        foreach (var (name, binding) in r.Query)
        {
            CheckBinding(d, binding, Location.Query, $"request.query.{name}", inTemplate: false, errors);
            if (reserved.ContainsQueryParam(name))
                errors.Add(new(4, $"request.query.{name}", $"`{name}` is reserved: the service instance's authentication mode sends it"));
        }
        foreach (var (where, param) in new[] { ("paging.pageSize.param", d.Paging?.PageSize?.Param), ("paging.token.param", d.Paging?.Token?.Param),
                     ("paging.offset.param", d.Paging?.Offset?.Param), ("paging.page.param", d.Paging?.Page?.Param) })
            if (param != null && reserved.ContainsQueryParam(param))
                errors.Add(new(4, where, $"`{param}` is reserved: the service instance's authentication mode sends it"));
        foreach (var (name, binding) in r.Headers)
        {
            CheckBinding(d, binding, Location.Header, $"request.headers.{name}", inTemplate: false, errors);
            if (reserved.Contains(name))
                errors.Add(new(4, $"request.headers.{name}", $"`{name}` is reserved: it is set by the service instance's authentication mode"));
        }
        if (r.Body is { } body)
        {
            if (body.Template != null)
                WalkTemplate(d, body.Template, "request.body.template", errors);
            foreach (var placeholder in body.RawPlaceholders)
                CheckBinding(d, placeholder.Binding, Location.Body, "request.body.raw", inTemplate: false, errors);
            if (r.Method is "GET" or "DELETE")
                errors.Add(new(5, "request.body", $"{r.Method} must not have a body"));
        }

        CheckPathParams(r, errors);
        CheckReadsAndPaging(d, errors);

        if (d.Errors?.Default is { } def && def != "Fail")
            errors.Add(new(8, "errors.default", $"errors.default must be Fail; got `{def}`"));

        if (d.Retry is { } retry)
        {
            if (!retry.Idempotent)
                errors.Add(new(9, "retry", "retry needs `idempotent: true`"));
            if (d.Binds == Messages.Create && !r.Headers.Any(h =>
                    string.Equals(h.Key, "Idempotency-Key", StringComparison.OrdinalIgnoreCase) && h.Value is ComputedBinding { Kind: "uuid" }))
                errors.Add(new(9, "retry", "Create can only retry with an `Idempotency-Key: { computed: uuid }` header"));
        }
        return errors;
    }

    private static void WalkTemplate(OperationDescriptor d, BodyNode node, string path, List<DescriptorError> errors)
    {
        switch (node)
        {
            case BodyObject obj:
                foreach (var (name, child) in obj.Members)
                    WalkTemplate(d, child, $"{path}.{name}", errors);
                break;
            case BodyArray arr:
                for (var i = 0; i < arr.Items.Count; i++)
                    WalkTemplate(d, arr.Items[i], $"{path}[{i}]", errors);
                break;
            case BodyLeaf leaf:
                CheckBinding(d, leaf.Binding, Location.Body, path, inTemplate: true, errors);
                break;
        }
    }

    private static void CheckBinding(OperationDescriptor d, Binding binding, Location location, string path, bool inTemplate, List<DescriptorError> errors)
    {
        void Rule2(string message) => errors.Add(new(2, path, message));
        var rm = d.Binds == Messages.RetrieveMultiple;

        switch (binding)
        {
            case SecretBinding when location is Location.Path or Location.Body:
                errors.Add(new(12, path, "`secret` is not allowed in the path or body, where a source could echo it back"));
                break;
            case ContextBinding c when !ContextBinding.Names.Contains(c.Name):
                Rule2($"unknown context `{c.Name}`; use {string.Join(", ", ContextBinding.Names)}");
                break;
            case ComputedBinding c when !ComputedBinding.Kinds.Contains(c.Kind):
                Rule2($"unknown computed value `{c.Kind}`; use {string.Join(", ", ComputedBinding.Kinds)}");
                break;
            case ColBinding when d.Binds is not (Messages.Create or Messages.Update):
                Rule2($"`col` is only allowed on Create and Update, not {d.Binds}");
                break;
            case IdBinding when d.Binds is not (Messages.Retrieve or Messages.Update or Messages.Delete):
                Rule2($"`id` is only allowed on Retrieve, Update and Delete, not {d.Binds}");
                break;
            case FilterBinding when !rm || location is not (Location.Query or Location.Path):
                Rule2("`filter` is only allowed in the query or path of a RetrieveMultiple");
                break;
            case SortBinding when !rm || location != Location.Query:
                Rule2("`sort` is only allowed in the query of a RetrieveMultiple");
                break;
            case SortBinding { DescFormat: { } fmt } when !fmt.Contains("{field}"):
                Rule2("descFormat must contain `{field}`");
                break;
            case PagingBinding p when !rm || location != Location.Query || !PagingBinding.Kinds.Contains(p.Kind):
                Rule2($"`paging` must be one of {string.Join(", ", PagingBinding.Kinds)}, in the query of a RetrieveMultiple");
                break;
        }

        if (binding.Modifiers.Format == "json" && !inTemplate)
            Rule2("`format: json` is only allowed on body template leaves");
        if (binding is FilterBinding f)
        {
            if (d.Response?.Map.ContainsKey(f.Column) == false && d.Response.Unmapped.Contains(f.Column) == false)
                Rule2($"filtered column `{f.Column}` is not in response.map");
            if (f.Ops.Contains(Ops.In) && binding.Modifiers.Format != "csv")
                Rule2("a filter that accepts `in` needs `format: csv` so the values have a defined form");
        }
    }

    private static void CheckPathParams(RequestSpec r, List<DescriptorError> errors)
    {
        var used = PathPlaceholderRegex().Matches(r.Path).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var name in used.Where(n => !r.PathParams.ContainsKey(n)))
            errors.Add(new(3, "request.path", $"placeholder `{{{name}}}` has no pathParams entry"));
        foreach (var name in r.PathParams.Keys.Where(n => !used.Contains(n)))
            errors.Add(new(3, $"request.pathParams.{name}", $"pathParams entry `{name}` is not used in the path"));
    }

    private static void CheckReadsAndPaging(OperationDescriptor d, List<DescriptorError> errors)
    {
        var resp = d.Response;
        if (d.IsRead && resp?.Rows == null)
            errors.Add(new(6, "response.rows", $"{d.Binds} needs response.rows"));
        if (d.Binds is Messages.Retrieve or Messages.RetrieveMultiple or Messages.Create && resp?.Key == null)
            errors.Add(new(6, "response.key", $"{d.Binds} needs response.key"));
        if (resp?.Key is HeaderKey && d.Binds != Messages.Create)
            errors.Add(new(6, "response.key", "a header key is only meaningful on Create"));

        if (resp != null)
            foreach (var (col, entry) in resp.Map)
                if (entry.OptionSetName is { } set && !d.Optionsets.ContainsKey(set))
                    errors.Add(new(6, $"response.map.{col}", $"option set map `{set}` is not declared under optionsets"));

        if (d.Binds != Messages.RetrieveMultiple)
        {
            if (d.Paging != null)
                errors.Add(new(6, "paging", "paging is only allowed on RetrieveMultiple"));
            return;
        }
        if (d.SortStable == null)
            errors.Add(new(6, "sortStable", "RetrieveMultiple must declare sortStable: true or false so paging behaviour is known"));
        if (d.Paging is not { } paging)
        {
            errors.Add(new(6, "paging", "RetrieveMultiple needs paging"));
            return;
        }
        switch (paging.Strategy)
        {
            case "cursor" when paging.Token == null:
                errors.Add(new(6, "paging.token", "cursor paging needs token: { param, next }"));
                break;
            case "offset" when paging.Offset == null:
                errors.Add(new(6, "paging.offset", "offset paging needs offset: { param }"));
                break;
            case "pageNumber" when paging.Page == null:
                errors.Add(new(6, "paging.page", "pageNumber paging needs page: { param, base }"));
                break;
        }
        if (paging.Strategy is "offset" or "pageNumber" && paging.PageSize == null)
            errors.Add(new(6, "paging.pageSize", $"{paging.Strategy} paging needs pageSize to know where the next page starts"));
    }
}
