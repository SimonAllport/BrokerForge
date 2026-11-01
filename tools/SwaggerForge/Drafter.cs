using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Schema;
using Microsoft.OpenApi.Models;

namespace BrokerForge.SwaggerForge;

/// <summary>
/// Drafts a table schema file and operation descriptors from an OpenAPI 3 document (HLD 5.3). Each
/// operation either matches one of the mapping rules or is skipped with the reason; nothing is guessed.
/// Defaults a person must review (string lengths, decimal precision, the primary name) are listed in
/// <see cref="DraftResult.Notes"/>. Every emitted descriptor is validated against the emitted schema.
/// </summary>
public static class Drafter
{
    private sealed record Field(string Column, string SourcePath, string DisplayName, string Type, int? MaxLength, int? Precision,
        IReadOnlyList<string>? Options, bool Required, string? Parent, string Property);

    private sealed record Resource(string Collection, string Item, string IdParam);

    public static DraftResult Draft(OpenApiDocument doc, DraftOptions o)
    {
        var run = new Run(doc, o);
        return run.Execute();
    }

    private sealed class Run(OpenApiDocument doc, DraftOptions o)
    {
        private readonly List<SkippedOperation> _skipped = new();
        private readonly List<string> _notes = new();
        private readonly SortedDictionary<string, string> _descriptors = new(StringComparer.Ordinal);
        private readonly YamlMap _customApis = new();
        private List<Field>? _fields;
        private string? _keyPath;
        private string? _primaryName;

        public DraftResult Execute()
        {
            var resource = FindResource();
            var ops = doc.Paths.SelectMany(p => p.Value.Operations.Select(op => (Path: p.Key, Item: p.Value, Method: op.Key.ToString().ToUpperInvariant(), Op: op.Value)))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal).ToList();

            foreach (var (path, item, method, op) in ops)
            {
                try
                {
                    if (string.IsNullOrEmpty(op.OperationId))
                        throw new UnsupportedShapeException("no operationId, so the operation has no name");
                    var parameters = item.Parameters.Concat(op.Parameters).ToList();
                    foreach (var p in parameters)
                        Heuristics.Check(p.Schema, $"parameter {p.Name}");
                    Heuristics.Check(RequestSchema(op), "request body");
                    foreach (var (status, response) in op.Responses)
                        Heuristics.Check(JsonSchema(response.Content), $"response {status}");

                    var name = char.ToLowerInvariant(op.OperationId[0]) + op.OperationId[1..];
                    var role = Role(resource, path, method);
                    var yaml = role switch
                    {
                        "RetrieveMultiple" => List(name, path, op, parameters),
                        "Retrieve" => Retrieve(name, path, op, parameters, resource!),
                        "Create" => Write("Create", name, path, op, parameters, resource!),
                        "Update" => Write("Update", name, path, op, parameters, resource!),
                        "Delete" => Delete(name, path, op, parameters, resource!),
                        "CustomApi" => CustomApi(name, path, op, parameters),
                        _ => throw new UnsupportedShapeException(role),
                    };
                    _descriptors[name] = yaml;
                }
                catch (UnsupportedShapeException ex)
                {
                    _skipped.Add(new SkippedOperation(method, path, ex.Message));
                }
            }

            var schemaYaml = SchemaYaml();
            Verify(schemaYaml);
            return new DraftResult(schemaYaml, _descriptors, _skipped, _notes);
        }

        // ---- resource and roles ----

        /// <summary>The collection whose name, singular, is the table name without its prefix (orders for ext_order); or the only one.</summary>
        private Resource? FindResource()
        {
            var candidates = new List<Resource>();
            foreach (var path in doc.Paths.Keys)
            {
                var segments = path.TrimEnd('/').Split('/');
                var last = segments[^1];
                if (last.StartsWith('{') && last.EndsWith('}') && segments.Length > 1 && !segments[^2].StartsWith('{'))
                    candidates.Add(new Resource(string.Join("/", segments[..^1]), path, last[1..^1]));
            }
            var wanted = o.Table[(o.Prefix.Length + 1)..];
            var named = candidates.Where(c => Singular(c.Collection.Split('/')[^1]) == wanted).ToList();
            if (named.Count == 1)
                return named[0];
            if (candidates.Count == 1)
            {
                _notes.Add($"Table {o.Table} is drafted from {candidates[0].Collection}, the only collection with an item path.");
                return candidates[0];
            }
            _notes.Add($"No single collection matches table {o.Table}, so no virtual table operations were drafted.");
            return null;
        }

        private static string Singular(string name) =>
            name.EndsWith("ies", StringComparison.Ordinal) ? name[..^3] + "y" : name.EndsWith('s') ? name[..^1] : name;

        private static string Role(Resource? r, string path, string method)
        {
            var lastLiteral = path.TrimEnd('/').Split('/')[^1];
            return (method, r) switch
            {
                ("GET", not null) when path == r.Collection => "RetrieveMultiple",
                ("GET", not null) when path == r.Item => "Retrieve",
                ("PATCH" or "PUT", not null) when path == r.Item => "Update",
                ("DELETE", not null) when path == r.Item => "Delete",
                ("POST", not null) when !lastLiteral.StartsWith('{') && lastLiteral == r.Collection.Split('/')[^1] => "Create",
                ("POST", _) => "CustomApi",
                _ => $"{method} {path} is not a list, retrieve, create, update or delete of the table's resource, nor a POST action",
            };
        }

        // ---- columns ----

        private List<Field> Fields(OpenApiSchema item)
        {
            if (_fields != null)
                return _fields;
            if (item.Type != "object" || item.Properties.Count == 0)
                throw new UnsupportedShapeException("the item schema is not an object with properties");
            if (!item.Properties.TryGetValue("id", out var id) || !Heuristics.IsScalar(id))
                throw new UnsupportedShapeException("the item schema has no scalar `id` property for the key");
            _keyPath = "$.id";

            var fields = new List<Field>();
            void Add(string property, OpenApiSchema s, string sourcePath, string display, bool required, string? parent)
            {
                var column = Heuristics.Column(o.Prefix, parent + property);
                if (fields.Any(f => f.Column == column))
                {
                    _notes.Add($"Property {sourcePath} would be column {column}, which another property already uses; it is not drafted.");
                    return;
                }
                if (s.Type == "array")
                {
                    fields.Add(new Field(column, sourcePath, display, "memo", null, null, null, required, parent, property));
                    return;
                }
                var type = Heuristics.ColumnType(s);
                int? maxLength = null, precision = null;
                if (type == "string")
                {
                    maxLength = s.MaxLength;
                    if (maxLength == null)
                    {
                        maxLength = Heuristics.DefaultMaxLength;
                        _notes.Add($"{column}: {sourcePath} declares no maxLength; drafted as {Heuristics.DefaultMaxLength}.");
                    }
                }
                if (type == "decimal")
                {
                    precision = Heuristics.DefaultPrecision;
                    _notes.Add($"{column}: {sourcePath} is a number; drafted as decimal with precision {Heuristics.DefaultPrecision}.");
                }
                fields.Add(new Field(column, sourcePath, display, type, maxLength, precision, Heuristics.EnumValues(s), required, parent, property));
            }

            foreach (var (name, s) in item.Properties)
            {
                if (name == "id")
                    continue;
                var required = item.Required.Contains(name);
                if (Heuristics.IsScalar(s) || s.Type == "array")
                {
                    Add(name, s, $"$.{name}", Heuristics.Humanise(name), required, null);
                    continue;
                }
                if (s.Type != "object")
                {
                    _notes.Add($"Property $.{name} has no type; it is not drafted.");
                    continue;
                }
                foreach (var (childName, child) in s.Properties)
                {
                    if (Heuristics.IsScalar(child) || child.Type == "array")
                        Add(childName, child, $"$.{name}.{childName}", $"{Heuristics.Humanise(name)} {Heuristics.Humanise(childName).ToLowerInvariant()}",
                            required && s.Required.Contains(childName), name);
                    else
                        _notes.Add($"Property $.{name}.{childName} is nested more than one level; it is not drafted.");
                }
            }

            var primary = fields.FirstOrDefault(f => f.Column == o.Prefix + "_name" && f.Type == "string") ?? fields.FirstOrDefault(f => f.Type == "string")
                ?? throw new UnsupportedShapeException("the item schema has no string property for the primary name");
            _primaryName = primary.Column;
            _notes.Add($"primaryName: {primary.Column} ({primary.SourcePath}), {(primary.Column == o.Prefix + "_name" ? "the name property" : "the first string property")}; review.");
            _fields = fields;
            return fields;
        }

        private static string OptionSetName(Field f) => f.Column[(f.Column.IndexOf('_') + 1)..];

        private void AddMap(YamlMap response, List<Field> fields)
        {
            var map = response.Map("map");
            foreach (var f in fields)
            {
                var type = Heuristics.MapType(f.Type, OptionSetName(f));
                if (type == null && f.Required)
                {
                    map.Add(f.Column, f.SourcePath);
                    continue;
                }
                var entry = map.Map(f.Column).Add("path", f.SourcePath);
                if (type != null)
                    entry.Add("type", type);
                if (!f.Required)
                    entry.Add("optional", true);
            }
        }

        private static void AddOptionSets(YamlMap root, IEnumerable<Field> fields)
        {
            var sets = fields.Where(f => f.Type == "optionset").ToList();
            if (sets.Count == 0)
                return;
            var map = root.Map("optionsets");
            foreach (var f in sets)
            {
                var set = map.Map(OptionSetName(f));
                for (var i = 0; i < f.Options!.Count; i++)
                    set.Add(f.Options[i], Heuristics.FirstOptionValue + i);
            }
        }

        private OpenApiSchema ItemSchema(Resource r)
        {
            if (doc.Paths.TryGetValue(r.Item, out var item) && item.Operations.TryGetValue(OperationType.Get, out var get) && SuccessSchema(get) is { } s)
                return s;
            if (doc.Paths.TryGetValue(r.Collection, out var list) && list.Operations.TryGetValue(OperationType.Get, out var getList) && SuccessSchema(getList) is { } ls)
                return Rows(ls).Item;
            throw new UnsupportedShapeException("no GET on the item or collection path describes the item schema");
        }

        // ---- descriptors ----

        private static YamlMap Header(string name, string binds, string? table)
        {
            var root = new YamlMap().Add("operation", name).Add("version", 1).Add("binds", binds);
            if (table != null)
                root.Add("table", table);
            return root;
        }

        private string List(string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters)
        {
            var responseSchema = SuccessSchema(op) ?? throw new UnsupportedShapeException("no response schema for the list");
            var (rowsPath, itemSchema, container) = Rows(responseSchema);
            var fields = Fields(itemSchema);
            var query = parameters.Where(p => p.In == ParameterLocation.Query).ToDictionary(p => p.Name, StringComparer.Ordinal);

            var root = Header(name, "RetrieveMultiple", o.Table);
            var request = root.Map("request").Add("method", "GET").Add("path", path);
            var paging = Paging(query, responseSchema, container, out var pagingParams);

            var queryMap = new YamlMap();
            foreach (var (param, p) in query)
            {
                if (pagingParams.Contains(param))
                    continue;
                if (param == "sort")
                {
                    _notes.Add($"{name}: query parameter sort is not drafted; declare a sort binding if the source's sort fields are known.");
                    continue;
                }
                var column = FilterColumn(p, fields);
                if (column == null)
                {
                    _notes.Add($"{name}: query parameter {param} matches no column; it is not declared as a filter.");
                    continue;
                }
                queryMap.Map(param).Add("filter", column.Column).Add("ops", new[] { "eq" });
            }
            if (queryMap.Count > 0)
                request.Add("query", queryMap);
            CheckPathParams(path, parameters, request, idParam: null, name);

            root.Add("paging", paging);
            var response = root.Map("response").Add("rows", rowsPath).Add("key", _keyPath!);
            AddMap(response, fields);
            AddOptionSets(root, fields);
            root.Map("errors").Add("default", "Fail");
            root.Add("sortStable", false);
            _notes.Add($"{name}: sortStable drafted as false; set true if the source's default order is stable.");
            return root.ToString();
        }

        private static (string Path, OpenApiSchema Item, string? Container) Rows(OpenApiSchema s)
        {
            if (s.Type == "array" && s.Items != null)
                return ("$[*]", s.Items, null);
            var arrays = s.Properties?.Where(p => p.Value.Type == "array" && p.Value.Items != null).ToList() ?? new();
            if (s.Type == "object" && arrays.Count == 1)
                return ($"$.{arrays[0].Key}[*]", arrays[0].Value.Items, arrays[0].Key);
            throw new UnsupportedShapeException("the list response is not an array or an object with exactly one array property");
        }

        private YamlMap Paging(Dictionary<string, OpenApiParameter> query, OpenApiSchema response, string? container, out HashSet<string> used)
        {
            var paging = new YamlMap();
            YamlMap PageSize(string param)
            {
                var max = query[param].Schema?.Maximum is { } m ? (int)m : (int?)null;
                if (max == null)
                    _notes.Add($"Paging parameter {param} declares no maximum; drafted as {Heuristics.DefaultPageSizeMax}.");
                return new YamlMap().Add("param", param).Add("max", max ?? Heuristics.DefaultPageSizeMax);
            }

            var cursorField = response.Properties?.Where(p => p.Key != container)
                .SelectMany(p => p.Value.Type == "object"
                    ? p.Value.Properties.Select(c => (Path: $"$.{p.Key}.{c.Key}", Name: c.Key, Schema: c.Value))
                    : new[] { (Path: $"$.{p.Key}", Name: p.Key, Schema: p.Value) })
                .FirstOrDefault(f => f.Schema.Type == "string" && f.Name.Contains("cursor", StringComparison.OrdinalIgnoreCase));
            var total = response.Properties?.TryGetValue("total", out var t) == true && t.Type == "integer";

            if (query.ContainsKey("limit") && query.ContainsKey("cursor") && cursorField?.Path != null)
            {
                used = new HashSet<string> { "limit", "cursor" };
                return paging.Add("strategy", "cursor").Add("pageSize", PageSize("limit"))
                    .Add("token", new YamlMap().Add("param", "cursor").Add("next", cursorField.Value.Path));
            }
            if (query.ContainsKey("limit") && query.ContainsKey("offset"))
            {
                used = new HashSet<string> { "limit", "offset" };
                var offset = new YamlMap().Add("param", "offset");
                if (total)
                    offset.Add("total", "$.total");
                return paging.Add("strategy", "offset").Add("pageSize", PageSize("limit")).Add("offset", offset);
            }
            var size = new[] { "pageSize", "size" }.FirstOrDefault(query.ContainsKey);
            if (query.ContainsKey("page") && size != null)
            {
                used = new HashSet<string> { "page", size };
                var @base = query["page"].Schema?.Minimum == 0 ? 0 : 1;
                return paging.Add("strategy", "pageNumber").Add("pageSize", PageSize(size)).Add("page", new YamlMap().Add("param", "page").Add("base", @base));
            }
            throw new UnsupportedShapeException("paging not identifiable: expected limit+cursor with a cursor field in the response, limit+offset, or page+pageSize");
        }

        /// <summary>The column a query parameter filters: same name as a top-level property, the parent of exactly one nested column, or the same enum values.</summary>
        private static Field? FilterColumn(OpenApiParameter p, List<Field> fields)
        {
            if (p.Schema == null || !Heuristics.IsScalar(p.Schema))
                return null;
            var direct = fields.FirstOrDefault(f => f.Parent == null && f.Property == p.Name && f.Type != "memo");
            if (direct != null)
                return direct;
            var children = fields.Where(f => f.Parent == p.Name && f.Type != "memo").ToList();
            if (children.Count == 1)
                return children[0];
            var values = Heuristics.EnumValues(p.Schema);
            if (values == null)
                return null;
            var same = fields.Where(f => f.Options != null && f.Options.SequenceEqual(values)).ToList();
            return same.Count == 1 ? same[0] : null;
        }

        private string Retrieve(string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters, Resource r)
        {
            var fields = Fields(SuccessSchema(op) ?? throw new UnsupportedShapeException("no response schema for the retrieve"));
            var root = Header(name, "Retrieve", o.Table);
            var request = root.Map("request").Add("method", "GET").Add("path", path);
            CheckPathParams(path, parameters, request, r.IdParam, name);
            var response = root.Map("response").Add("rows", "$").Add("key", _keyPath!);
            AddMap(response, fields);
            AddOptionSets(root, fields);
            Errors(root, op);
            return root.ToString();
        }

        private string Write(string binds, string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters, Resource r)
        {
            var fields = Fields(ItemSchema(r));
            var body = RequestSchema(op) ?? throw new UnsupportedShapeException($"no JSON request body for the {binds.ToLowerInvariant()}");
            if (body.Type != "object")
                throw new UnsupportedShapeException("the request body is not an object");

            var root = Header(name, binds, o.Table);
            var request = root.Map("request").Add("method", MethodOf(path, op)).Add("path", path);
            CheckPathParams(path, parameters, request, binds == "Update" ? r.IdParam : null, name);

            var bound = new List<Field>();
            var template = new YamlMap();
            foreach (var (prop, s) in body.Properties)
            {
                if (Heuristics.IsScalar(s) || s.Type == "array")
                {
                    Bind(template, prop, fields.FirstOrDefault(f => f.Parent == null && f.Property == prop), body.Required.Contains(prop), $"$.{prop}");
                    continue;
                }
                var nested = new YamlMap();
                foreach (var (childName, child) in s.Properties ?? new Dictionary<string, OpenApiSchema>())
                    Bind(nested, childName, fields.FirstOrDefault(f => f.Parent == prop && f.Property == childName),
                        body.Required.Contains(prop) && s.Required.Contains(childName), $"$.{prop}.{childName}");
                if (nested.Count > 0)
                    template.Add(prop, nested);
            }
            if (template.Count == 0)
                throw new UnsupportedShapeException("no request body property matches a column");
            request.Map("body").Add("template", template);

            void Bind(YamlMap into, string prop, Field? field, bool required, string where)
            {
                if (field == null)
                {
                    if (required)
                        throw new UnsupportedShapeException($"required body property {where} matches no column");
                    _notes.Add($"{name}: body property {where} matches no column; it is not sent.");
                    return;
                }
                var leaf = into.Map(prop).Add("col", field.Column);
                if (field.Type == "memo")
                    leaf.Add("format", "json");
                if (field.Type == "optionset")
                    leaf.Add("optionset", OptionSetName(field));
                bound.Add(field);
            }

            if (binds == "Create")
            {
                var response = root.Map("response");
                var created = op.Responses.Where(x => x.Key is "200" or "201").Select(x => x.Value).FirstOrDefault()
                    ?? throw new UnsupportedShapeException("the create declares no 200 or 201 response");
                if (JsonSchema(created.Content)?.Properties.TryGetValue("id", out var id) == true && Heuristics.IsScalar(id))
                    response.Add("key", "$.id");
                else if (created.Headers.ContainsKey("Location"))
                    response.Add("key", new YamlMap().Add("header", "Location").Add("pattern", $"/{r.Collection.Split('/')[^1]}/(?<id>[^/]+)$"));
                else
                    throw new UnsupportedShapeException("the create response has neither an id property nor a Location header for the new key");
                var unmapped = fields.Where(f => !bound.Contains(f)).Select(f => f.Column).ToList();
                if (unmapped.Count > 0)
                    response.Add("unmapped", unmapped);
            }
            AddOptionSets(root, bound);
            Errors(root, op);
            return root.ToString();
        }

        private string MethodOf(string path, OpenApiOperation op) =>
            doc.Paths[path].Operations.First(x => ReferenceEquals(x.Value, op)).Key.ToString().ToUpperInvariant();

        private string Delete(string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters, Resource r)
        {
            var root = Header(name, "Delete", o.Table);
            var request = root.Map("request").Add("method", "DELETE").Add("path", path);
            CheckPathParams(path, parameters, request, r.IdParam, name);
            Errors(root, op);
            return root.ToString();
        }

        private string CustomApi(string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters)
        {
            var api = $"{o.Prefix}_{Heuristics.Pascal(name)}";
            var responseSchema = SuccessSchema(op) ?? throw new UnsupportedShapeException("no response schema, so the Custom API would have no outputs");
            var inputs = new YamlMap();
            var root = Header(name, "customapi:" + api, null);
            var request = root.Map("request").Add("method", "POST").Add("path", path);

            void Input(string param, OpenApiSchema? s, bool required, string where)
            {
                if (s == null || !Heuristics.IsScalar(s))
                    throw new UnsupportedShapeException($"{where} is not a scalar, so it cannot be a Custom API parameter");
                var declared = inputs.Map(Heuristics.Pascal(param)).Add("type", Heuristics.ParamType(s));
                if (required)
                    declared.Add("required", true);
            }

            var pathParams = new YamlMap();
            var query = new YamlMap();
            foreach (var p in parameters)
            {
                switch (p.In)
                {
                    case ParameterLocation.Path:
                        Input(p.Name, p.Schema, true, $"path parameter {p.Name}");
                        pathParams.Map(p.Name).Add("param", Heuristics.Pascal(p.Name));
                        break;
                    case ParameterLocation.Query:
                        Input(p.Name, p.Schema, p.Required, $"query parameter {p.Name}");
                        query.Map(p.Name).Add("param", Heuristics.Pascal(p.Name));
                        break;
                    default:
                        throw new UnsupportedShapeException($"{p.In} parameter {p.Name} has no binding in a Custom API draft");
                }
            }
            if (pathParams.Count > 0)
                request.Add("pathParams", pathParams);
            if (query.Count > 0)
                request.Add("query", query);
            if (RequestSchema(op) is { } body)
            {
                if (body.Type != "object")
                    throw new UnsupportedShapeException("the request body is not an object");
                var template = new YamlMap();
                foreach (var (prop, s) in body.Properties)
                {
                    Input(prop, s, body.Required.Contains(prop), $"body property {prop}");
                    template.Map(prop).Add("param", Heuristics.Pascal(prop));
                }
                request.Map("body").Add("template", template);
            }

            var outputs = new YamlMap();
            var declaredOutputs = new YamlMap();
            foreach (var (prop, s) in responseSchema.Properties ?? new Dictionary<string, OpenApiSchema>())
            {
                if (!Heuristics.IsScalar(s))
                {
                    _notes.Add($"{name}: response property {prop} is not flat; it is not an output.");
                    continue;
                }
                var type = Heuristics.ParamType(s);
                var output = Heuristics.Pascal(prop);
                declaredOutputs.Add(output, type);
                var required = responseSchema.Required.Contains(prop);
                if (type == "string" && required)
                    outputs.Add(output, $"$.{prop}");
                else
                {
                    var entry = outputs.Map(output).Add("path", $"$.{prop}");
                    if (type != "string")
                        entry.Add("type", type);
                    if (!required)
                        entry.Add("optional", true);
                }
            }
            if (outputs.Count == 0)
                throw new UnsupportedShapeException("the response has no flat properties to return as outputs");
            root.Map("response").Add("outputs", outputs);
            Errors(root, op);

            var declaredApi = _customApis.Map(api).Add("displayName", Heuristics.Humanise(name));
            if (inputs.Count > 0)
                declaredApi.Add("inputs", inputs);
            declaredApi.Add("outputs", declaredOutputs);
            return root.ToString();
        }

        /// <summary>Path parameters: the record id where there is one, otherwise service instance config named like the parameter.</summary>
        private void CheckPathParams(string path, List<OpenApiParameter> parameters, YamlMap request, string? idParam, string name)
        {
            var map = new YamlMap();
            foreach (var p in parameters.Where(p => p.In == ParameterLocation.Path))
            {
                if (p.Name == idParam)
                {
                    map.Map(p.Name).Add("id", true);
                    continue;
                }
                map.Map(p.Name).Add("instance", p.Name);
                _notes.Add($"{name}: path parameter {p.Name} is bound to instance config `{p.Name}`; set it in the host settings for {o.ServiceInstance}.");
            }
            if (map.Count > 0)
                request.Add("pathParams", map);
        }

        private static void Errors(YamlMap root, OpenApiOperation op)
        {
            var errors = root.Map("errors");
            foreach (var (status, kind) in new[] { ("404", "NotFound"), ("409", "Conflict"), ("403", "Forbidden"), ("422", "Validation") })
                if (op.Responses.ContainsKey(status))
                    errors.Add(status, kind);
            errors.Add("default", "Fail");
        }

        private static OpenApiSchema? SuccessSchema(OpenApiOperation op) =>
            op.Responses.Where(r => r.Key is "200" or "201").Select(r => JsonSchema(r.Value.Content)).FirstOrDefault(s => s != null);

        private static OpenApiSchema? RequestSchema(OpenApiOperation op) => JsonSchema(op.RequestBody?.Content);

        private static OpenApiSchema? JsonSchema(IDictionary<string, OpenApiMediaType>? content) =>
            content != null && content.TryGetValue("application/json", out var media) ? media.Schema : null;

        // ---- schema file and verification ----

        private string SchemaYaml()
        {
            var root = new YamlMap();
            if (_fields != null && _descriptors.Values.Any(y => y.Contains("\ntable: ", StringComparison.Ordinal)))
            {
                var bare = o.Table[(o.Prefix.Length + 1)..];
                root.Add("table", o.Table).Add("displayName", Heuristics.Humanise(bare)).Add("displayCollectionName", Heuristics.Humanise(bare) + "s")
                    .Add("externalName", bare).Add("primaryName", _primaryName!);
                var columns = root.Map("columns");
                foreach (var f in _fields)
                {
                    var c = columns.Map(f.Column).Add("type", f.Type);
                    if (f.MaxLength is { } ml)
                        c.Add("maxLength", ml);
                    if (f.Precision is { } p)
                        c.Add("precision", p);
                    if (f.Options != null)
                    {
                        var options = c.Map("options");
                        for (var i = 0; i < f.Options.Count; i++)
                            options.Add(f.Options[i], Heuristics.FirstOptionValue + i);
                    }
                    c.Add("displayName", f.DisplayName);
                }
            }
            if (_customApis.Count > 0)
                root.Add("customApis", _customApis);
            return root.Count == 0 ? "" : root.ToString();
        }

        /// <summary>A draft that fails its own validation is a SwaggerForge bug, so it fails the run rather than reaching review.</summary>
        private void Verify(string schemaYaml)
        {
            if (schemaYaml.Length == 0)
            {
                if (_descriptors.Count > 0)
                    throw new InvalidOperationException("SwaggerForge drafted descriptors but no schema.");
                return;
            }
            var schema = SchemaParser.Parse(schemaYaml, $"{o.ServiceInstance}.yaml");
            foreach (var (name, yaml) in _descriptors)
            {
                var errors = DescriptorValidator.Validate(DescriptorParser.Parse(yaml, name + ".yaml"), ReservedNames.For(Array.Empty<string>()), schema);
                if (errors.Count > 0)
                    throw new InvalidOperationException($"SwaggerForge drafted an invalid descriptor {name}: {string.Join("; ", errors)}");
            }
        }
    }
}
