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

    internal static DraftReview Draft(OpenApiDocument doc, DraftOptions o, Answers answers) => new Run(doc, o, answers).Execute();

    private static readonly Choice[] UseChoices = { new("yes", "Use it"), new("no", "Leave it out") };

    private sealed class Run(OpenApiDocument doc, DraftOptions o, Answers answers)
    {
        private readonly List<SkippedOperation> _skipped = new();
        private readonly List<string> _notes = new();
        private readonly List<string> _problems = new();
        private (string Name, YamlMap Map)? _lastApi;
        private readonly List<Question> _tableQuestions = new();
        private List<Question> _q = new();
        private List<Field>? _fields;
        private string? _tableUnsupported;
        private string? _keyPath;
        private string? _primaryName;
        private string _table = "";
        private string _tableDisplay = "";
        private string _tableCollection = "";
        private string? _keyProp;

        private sealed record Drafted(string Operation, string Method, string Path, string Becomes, bool TableBound, List<Question> Questions, string Yaml, (string Name, YamlMap Map)? Api);

        public DraftReview Execute()
        {
            var candidates = Candidates();
            var resource = candidates.Count == 1 ? candidates[0] : null;
            if (candidates.Count > 1)
            {
                var which = answers.Choice("table.collection", "Several collections have an item path. Which one becomes the table?",
                    candidates.Select(c => new Choice(c.Collection, c.Collection)).ToList());
                _tableQuestions.Add(which);
                resource = candidates.FirstOrDefault(c => c.Collection == which.Answer);
            }
            if (resource != null)
            {
                var singular = Singular(resource.Collection.Split('/')[^1]);
                _table = Table("table.name", "Table logical name", Heuristics.Column(o.Prefix, singular), NameCheck);
                _tableDisplay = Table("table.displayName", "Table display name", Heuristics.Humanise(singular), DisplayCheck);
                _tableCollection = Table("table.collectionName", "Table plural display name", Heuristics.Humanise(singular) + "s", DisplayCheck);
                try
                {
                    Fields(ItemSchema(resource));
                }
                catch (UnsupportedShapeException ex)
                {
                    _tableUnsupported = ex.Message;
                }
            }

            var ops = doc.Paths.SelectMany(p => p.Value.Operations.Select(op => (Path: p.Key, Item: p.Value, Method: op.Key.ToString().ToUpperInvariant(), Op: op.Value)))
                .OrderBy(x => x.Path, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal).ToList();

            var endpoints = new List<EndpointReview>();
            var drafted = new List<Drafted>();
            foreach (var (path, item, method, op) in ops)
            {
                var name = string.IsNullOrEmpty(op.OperationId) ? $"{method} {path}" : char.ToLowerInvariant(op.OperationId[0]) + op.OperationId[1..];
                var becomes = "Not drafted";
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

                    if (resource == null && candidates.Count > 1 && candidates.Select(c => Role(c, path, method)).FirstOrDefault(IsTableRole) is { } waiting)
                    {
                        endpoints.Add(new EndpointReview(name, method, path, Becomes(waiting, name), EndpointStatus.NeedsAnswers, null, Array.Empty<Question>(), Array.Empty<string>()));
                        continue;
                    }
                    var role = Role(resource, path, method);
                    becomes = Becomes(role, name);
                    var tableBound = IsTableRole(role);
                    if (tableBound && _tableUnsupported != null)
                        throw new UnsupportedShapeException(_tableUnsupported);

                    _q = new List<Question>();
                    var use = answers.Choice($"op.{name}.use", $"Use {method} {path}?", UseChoices, required: false, proposed: "yes");
                    _q.Add(use);
                    if (use.Value == "no")
                    {
                        endpoints.Add(new EndpointReview(name, method, path, becomes, EndpointStatus.NotUsed, null, _q, Array.Empty<string>()));
                        continue;
                    }
                    _lastApi = null;
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
                    drafted.Add(new Drafted(name, method, path, becomes, tableBound, _q, yaml, _lastApi));
                }
                catch (UnsupportedShapeException ex)
                {
                    _skipped.Add(new SkippedOperation(method, path, ex.Message));
                    endpoints.Add(new EndpointReview(name, method, path, becomes, EndpointStatus.Unsupported, ex.Message, Array.Empty<Question>(), Array.Empty<string>()));
                }
            }

            var tableOpen = _tableQuestions.Any(q => q.Open);
            var ready = drafted.Where(d => !d.Questions.Any(q => q.Open) && !(d.TableBound && tableOpen)).ToList();
            var schemaYaml = SchemaYaml(ready.Any(d => d.TableBound), ready.Where(d => d.Api != null).Select(d => d.Api!.Value));
            var errors = ready.Count == 0 ? new Dictionary<string, IReadOnlyList<string>>()
                : Check(schemaYaml, ready.ToDictionary(d => d.Operation, d => d.Yaml), o);
            foreach (var d in drafted)
            {
                var status = !ready.Contains(d) ? EndpointStatus.NeedsAnswers
                    : errors.TryGetValue(d.Operation, out var e) && e.Count > 0 ? EndpointStatus.Invalid : EndpointStatus.Ready;
                endpoints.Add(new EndpointReview(d.Operation, d.Method, d.Path, d.Becomes, status, null, d.Questions,
                    status == EndpointStatus.Invalid ? errors[d.Operation] : Array.Empty<string>()));
            }
            endpoints = endpoints.OrderBy(e => e.Path, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal).ToList();

            var table = resource == null && candidates.Count < 2 ? null : new TableProposal(resource?.Collection, _table, _tableDisplay, _tableCollection, _keyPath, _primaryName,
                (_fields ?? new()).Select(f => new ColumnProposal(f.SourcePath, f.Column, f.DisplayName, f.Type)).ToList(), _tableQuestions);

            var used = endpoints.Where(e => e.Status != EndpointStatus.NotUsed && e.Status != EndpointStatus.Unsupported).ToList();
            DraftResult? result = null;
            if (_problems.Count == 0 && used.Count > 0 && used.All(e => e.Status == EndpointStatus.Ready))
                result = new DraftResult(schemaYaml, new SortedDictionary<string, string>(ready.ToDictionary(d => d.Operation, d => d.Yaml), StringComparer.Ordinal),
                    _skipped, _notes);
            return new DraftReview(table, endpoints, _problems, answers.Unused(), result);
        }

        private static bool IsTableRole(string role) => role is "RetrieveMultiple" or "Retrieve" or "Create" or "Update" or "Delete";

        private static string Becomes(string role, string name) => role switch
        {
            "RetrieveMultiple" => "List",
            "Retrieve" => "Open",
            "Create" => "New",
            "Update" => "Save",
            "Delete" => "Delete",
            "CustomApi" => "Action: " + Heuristics.Humanise(name),
            _ => "Not drafted",
        };

        /// <summary>Asks an optional table-level name question and returns the value in use.</summary>
        private string Table(string key, string text, string proposed, Func<string, string?> check)
        {
            var q = answers.Text(key, text, false, proposed, check);
            _tableQuestions.Add(q);
            return q.Value!;
        }

        /// <summary>A logical name: the prefix, an underscore, then lower-case ASCII letters and digits; at most 50 characters.</summary>
        private string? NameCheck(string name) =>
            name.Length <= 50 && name.StartsWith(o.Prefix + "_", StringComparison.Ordinal) && name.Length > o.Prefix.Length + 1
            && name[(o.Prefix.Length + 1)..].All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterLower(c))
                ? null : $"must be {o.Prefix}_ followed by lower-case letters and digits, at most 50 characters";

        private static string? DisplayCheck(string name) =>
            name.Trim().Length > 0 && name.Length <= 100 && name == name.Trim() ? null : "must be 1 to 100 characters, with no leading or trailing spaces";

        // ---- resource and roles ----

        /// <summary>Every collection with an item path (orders for /orders/{id}), in path order.</summary>
        private List<Resource> Candidates()
        {
            var candidates = new List<Resource>();
            foreach (var path in doc.Paths.Keys.Order(StringComparer.Ordinal))
            {
                var segments = path.TrimEnd('/').Split('/');
                var last = segments[^1];
                if (last.StartsWith('{') && last.EndsWith('}') && segments.Length > 1 && !segments[^2].StartsWith('{'))
                    candidates.Add(new Resource(string.Join("/", segments[..^1]), path, last[1..^1]));
            }
            candidates = candidates.OrderBy(c => c.Collection, StringComparer.Ordinal).ToList();
            if (candidates.Count == 0)
                _notes.Add("No collection has an item path, so no virtual table operations were drafted.");
            return candidates;
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

        /// <summary>
        /// The table's columns, once, with the table questions: the key when there is no scalar <c>id</c>, each column's
        /// name and display name, a string's length and a number's precision when the definition does not state them,
        /// and the primary name when no string property is called <c>name</c>.
        /// </summary>
        private List<Field> Fields(OpenApiSchema item)
        {
            if (_fields != null)
                return _fields;
            if (item.Type != "object" || item.Properties.Count == 0)
                throw new UnsupportedShapeException("the item schema is not an object with properties");
            if (item.Properties.TryGetValue("id", out var id) && Heuristics.IsScalar(id))
                _keyProp = "id";
            else
            {
                var scalars = item.Properties.Where(p => Heuristics.IsScalar(p.Value)).Select(p => p.Key).ToList();
                if (scalars.Count == 0)
                    throw new UnsupportedShapeException("the item schema has no scalar property for the key");
                var key = answers.Choice("table.key", "The definition has no scalar `id`. Which property is the record's key?",
                    scalars.Select(n => new Choice("$" + Heuristics.Step(n), n)).ToList());
                _tableQuestions.Add(key);
                _keyProp = scalars.FirstOrDefault(n => "$" + Heuristics.Step(n) == key.Answer);
            }
            _keyPath = _keyProp == null ? null : "$" + Heuristics.Step(_keyProp);

            var fields = new List<Field>();
            var asked = new Dictionary<string, List<Question>>(StringComparer.Ordinal);
            void Add(string property, OpenApiSchema s, string sourcePath, string display, bool required, string? parent)
            {
                if (!Heuristics.Representable(property) || (parent != null && !Heuristics.Representable(parent)))
                {
                    _notes.Add($"Property {sourcePath} has characters a Dataverse column name cannot hold; it is not drafted.");
                    return;
                }
                var column = Heuristics.Column(o.Prefix, parent + property);
                if (fields.Any(f => f.Column == column))
                {
                    _notes.Add($"Property {sourcePath} would be column {column}, which another property already uses; it is not drafted.");
                    return;
                }
                var questions = asked[sourcePath] = new List<Question>();
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
                        var q = answers.Number($"column.{sourcePath}.maxLength", $"{display} ({sourcePath}): the definition gives no maximum length. What is the longest value?", 1, 4000);
                        questions.Add(q);
                        maxLength = q.Answer == null ? 0 : int.Parse(q.Answer, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                if (type == "decimal")
                {
                    precision = Heuristics.PrecisionOf(s);
                    if (precision == null)
                    {
                        var q = answers.Number($"column.{sourcePath}.precision", $"{display} ({sourcePath}) is a number. How many decimal places does it have?", 0, 10);
                        questions.Add(q);
                        precision = q.Answer == null ? 0 : int.Parse(q.Answer, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                fields.Add(new Field(column, sourcePath, display, type, maxLength, precision, Heuristics.EnumValues(s), required, parent, property));
            }

            foreach (var (name, s) in item.Properties)
            {
                if (name == _keyProp)
                    continue;
                var required = item.Required.Contains(name);
                if (Heuristics.IsScalar(s) || s.Type == "array")
                {
                    Add(name, s, "$" + Heuristics.Step(name), Heuristics.Humanise(name), required, null);
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
                        Add(childName, child, "$" + Heuristics.Step(name) + Heuristics.Step(childName), $"{Heuristics.Humanise(name)} {Heuristics.Humanise(childName).ToLowerInvariant()}",
                            required && s.Required.Contains(childName), name);
                    else
                        _notes.Add($"Property $.{name}.{childName} is nested more than one level; it is not drafted.");
                }
            }

            // Names the admin may edit, then refuse any that collide: every answered name in a clash gets the error.
            var named = new List<(Field Field, Question Name, Question Display)>();
            foreach (var f in fields)
            {
                var n = answers.Text($"column.{f.SourcePath}.name", $"Column logical name for {f.SourcePath}", false, f.Column,
                    v => NameCheck(v) ?? (v == _table + "id" ? $"{v} is the table's own key column" : null));
                var d = answers.Text($"column.{f.SourcePath}.displayName", $"Column display name for {f.SourcePath}", false, f.DisplayName, DisplayCheck);
                named.Add((f, n, d));
            }
            foreach (var clash in named.GroupBy(x => x.Name.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
                for (var i = 0; i < named.Count; i++)
                    if (named[i].Name.Value == clash.Key && named[i].Name.Answer != null)
                        named[i] = named[i] with { Name = named[i].Name with { Answer = null, Error = $"another column is also named {clash.Key}" } };
            fields = named.Select(x => x.Field with { Column = x.Name.Value!, DisplayName = x.Display.Value! }).ToList();
            foreach (var (f, n, d) in named)
                _tableQuestions.AddRange(new[] { n, d }.Concat(asked[f.SourcePath]));

            var strings = fields.Where(f => f.Type == "string").ToList();
            if (strings.Count == 0)
                throw new UnsupportedShapeException("the item schema has no string property for the primary name");
            var primary = strings.FirstOrDefault(f => f.Parent == null && f.Property == "name");
            if (primary == null)
            {
                var q = answers.Choice("table.primaryName", "No property is called `name`. Which one names a record in lists and lookups?",
                    strings.Select(f => new Choice(f.SourcePath, $"{f.DisplayName} ({f.SourcePath})")).ToList());
                _tableQuestions.Add(q);
                primary = strings.FirstOrDefault(f => f.SourcePath == q.Answer);
            }
            _primaryName = primary?.Column;
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

        private static readonly string[] SortParams = { "sort", "orderBy", "order_by" };

        private static readonly Choice[] SortChoices =
        {
            new("none", "It cannot sort, or BrokerForge should not use it"),
            new("asc", "Ascending only"),
            new("-{field}", "A leading minus sorts descending (-name)"),
            new("{field} desc", "`desc` after the field sorts descending (name desc)"),
        };

        private static readonly Choice[] YesNo = { new("yes", "Yes"), new("no", "No") };

        private string List(string name, string path, OpenApiOperation op, List<OpenApiParameter> parameters)
        {
            var responseSchema = SuccessSchema(op) ?? throw new UnsupportedShapeException("no response schema for the list");
            var (rowsPath, itemSchema, container) = Rows(responseSchema);
            var fields = Fields(itemSchema);
            var query = parameters.Where(p => p.In == ParameterLocation.Query).ToDictionary(p => p.Name, StringComparer.Ordinal);

            var root = Header(name, "RetrieveMultiple", _table);
            var request = root.Map("request").Add("method", "GET").Add("path", path);
            var paging = Paging(name, op, query, responseSchema, container, out var pagingParams);

            var queryMap = new YamlMap();
            var anyIn = false;
            foreach (var (param, p) in query)
            {
                if (pagingParams.Contains(param))
                    continue;
                if (SortParams.Contains(param))
                {
                    Sort(name, param, fields, queryMap);
                    continue;
                }
                var column = FilterColumn(p, fields);
                if (column == null)
                {
                    _notes.Add($"{name}: query parameter {param} matches no column; it is not declared as a filter.");
                    continue;
                }
                var choices = new List<Choice> { new("none", "Not a filter"), new("eq", "Equals") };
                if (column.Type is "string" or "optionset")
                    choices.AddRange(new[] { new Choice("in", "One of several, comma-separated"), new Choice("eq,in", "Equals, or one of several, comma-separated") });
                if (column.Type == "string")
                    choices.Add(new Choice("like", "Begins with"));
                var q = Ask(answers.Choice($"op.{name}.filter.{param}",
                    $"Query parameter `{param}` looks like a filter on {column.DisplayName}. What does the source really do with it? Anything not declared fails loudly if someone filters on it.", choices));
                if (q.Answer is null or "none")
                    continue;
                var ops = q.Answer.Split(',');
                var binding = queryMap.Map(param).Add("filter", column.Column).Add("ops", ops);
                if (ops.Contains("in"))
                {
                    binding.Add("format", "csv");
                    anyIn = true;
                }
            }
            if (queryMap.Count > 0)
                request.Add("query", queryMap);
            CheckPathParams(path, parameters, request, idParam: null, name);

            root.Add("paging", paging);
            var response = root.Map("response").Add("rows", rowsPath).Add("key", _keyPath ?? "$");
            AddMap(response, fields);
            AddOptionSets(root, fields);
            if (anyIn)
                root.Map("filters").Add("allowOr", true).Add("orStyle", "in");
            root.Map("errors").Add("default", "Fail");
            var stable = Ask(answers.Choice($"op.{name}.sortStable",
                "With no sort, does the source return rows in the same order on every call? Paging relies on it.", YesNo));
            root.Add("sortStable", stable.Answer == "yes");
            return root.ToString();
        }

        private Question Ask(Question q)
        {
            _q.Add(q);
            return q;
        }

        private void Sort(string name, string param, List<Field> fields, YamlMap queryMap)
        {
            var q = Ask(answers.Choice($"op.{name}.sort", $"Query parameter `{param}` looks like a sort. How does the source sort?", SortChoices));
            if (q.Answer is null or "none")
                return;
            var sortFields = new YamlMap();
            foreach (var f in fields.Where(f => f.Type != "memo"))
            {
                var field = Ask(answers.Text($"op.{name}.sort.{f.SourcePath}", $"The source's sort field for {f.DisplayName} (leave empty if it cannot sort by it)", false, null,
                    v => v.Length <= 100 && !v.Any(char.IsWhiteSpace) ? null : "must be 1 to 100 characters with no spaces"));
                if (field.Answer != null)
                    sortFields.Add(f.Column, field.Answer);
            }
            if (sortFields.Count == 0)
            {
                _q[_q.IndexOf(q)] = q with { Answer = null, Error = "name at least one column the source can sort by, or choose that it cannot sort" };
                return;
            }
            var binding = queryMap.Map(param).Add("sort", sortFields);
            if (q.Answer != "asc")
                binding.Add("descFormat", q.Answer);
        }

        private static (string Path, OpenApiSchema Item, string? Container) Rows(OpenApiSchema s)
        {
            if (s.Type == "array" && s.Items != null)
                return ("$[*]", s.Items, null);
            var arrays = s.Properties?.Where(p => p.Value.Type == "array" && p.Value.Items != null).ToList() ?? new();
            if (s.Type == "object" && arrays.Count == 1)
                return ("$" + Heuristics.Step(arrays[0].Key) + "[*]", arrays[0].Value.Items, arrays[0].Key);
            throw new UnsupportedShapeException("the list response is not an array or an object with exactly one array property");
        }

        /// <summary>
        /// Paging is always a question: the choices are the strategies this list's shape supports, from its parameters and
        /// response, plus "it doesn't page". <paramref name="used"/> is every parameter any candidate pages with, so those
        /// are never asked about as filters whichever strategy is chosen.
        /// </summary>
        private YamlMap Paging(string name, OpenApiOperation op, Dictionary<string, OpenApiParameter> query, OpenApiSchema response, string? container, out HashSet<string> used)
        {
            used = new HashSet<string>(StringComparer.Ordinal);
            YamlMap PageSize(string param)
            {
                var max = query[param].Schema?.Maximum is { } m ? (int)m : (int?)null;
                if (max == null)
                {
                    var q = Ask(answers.Number($"op.{name}.pageSizeMax", $"Paging parameter `{param}` declares no maximum. What is the largest page the source returns?", 1, 5000));
                    max = q.Answer == null ? 1 : int.Parse(q.Answer, System.Globalization.CultureInfo.InvariantCulture);
                }
                return new YamlMap().Add("param", param).Add("max", max.Value);
            }

            var cursorField = response.Properties?.Where(p => p.Key != container)
                .SelectMany(p => p.Value.Type == "object"
                    ? p.Value.Properties.Select(c => (Path: "$" + Heuristics.Step(p.Key) + Heuristics.Step(c.Key), Name: c.Key, Schema: c.Value))
                    : new[] { (Path: "$" + Heuristics.Step(p.Key), Name: p.Key, Schema: p.Value) })
                .FirstOrDefault(f => f.Schema.Type == "string" && f.Name.Contains("cursor", StringComparison.OrdinalIgnoreCase));
            var total = response.Properties?.TryGetValue("total", out var t) == true && t.Type == "integer";
            var size = new[] { "pageSize", "size" }.FirstOrDefault(query.ContainsKey);
            var linkSize = new[] { "limit", "pageSize", "size" }.FirstOrDefault(query.ContainsKey);
            var link = op.Responses.Where(r => r.Key is "200").Any(r => r.Value.Headers.Keys.Any(h => h.Equals("Link", StringComparison.OrdinalIgnoreCase)));

            var candidates = new List<(Choice Choice, string[] Params, Func<YamlMap> Build)>();
            if (query.ContainsKey("limit") && query.ContainsKey("cursor") && cursorField?.Path != null)
                candidates.Add((new Choice("cursor", $"`limit` and `cursor` query parameters; the next cursor is at {cursorField.Value.Path}"), new[] { "limit", "cursor" },
                    () => new YamlMap().Add("strategy", "cursor").Add("pageSize", PageSize("limit")).Add("token", new YamlMap().Add("param", "cursor").Add("next", cursorField.Value.Path))));
            if (query.ContainsKey("limit") && query.ContainsKey("offset"))
                candidates.Add((new Choice("offset", "`limit` and `offset` query parameters" + (total ? "; the total is at $.total" : "")), new[] { "limit", "offset" }, () =>
                {
                    var offset = new YamlMap().Add("param", "offset");
                    if (total)
                        offset.Add("total", "$.total");
                    return new YamlMap().Add("strategy", "offset").Add("pageSize", PageSize("limit")).Add("offset", offset);
                }));
            if (query.ContainsKey("page") && size != null)
                candidates.Add((new Choice("pageNumber", $"`page` and `{size}` query parameters"), new[] { "page", size }, () =>
                {
                    var @base = query["page"].Schema?.Minimum is { } min ? (int)min : (int?)null;
                    if (@base == null)
                    {
                        var q = Ask(answers.Choice($"op.{name}.pageBase", "Is the first page number 0 or 1?", new[] { new Choice("0", "0"), new Choice("1", "1") }));
                        @base = q.Answer == null ? 1 : int.Parse(q.Answer, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    return new YamlMap().Add("strategy", "pageNumber").Add("pageSize", PageSize(size)).Add("page", new YamlMap().Add("param", "page").Add("base", @base.Value));
                }));
            if (link)
                candidates.Add((new Choice("link", "A `Link` response header with rel=\"next\""), linkSize == null ? Array.Empty<string>() : new[] { linkSize }, () =>
                {
                    var paging = new YamlMap().Add("strategy", "link");
                    if (linkSize != null)
                        paging.Add("pageSize", PageSize(linkSize));
                    return paging;
                }));
            foreach (var c in candidates)
                used.UnionWith(c.Params);

            var choices = candidates.Select(c => c.Choice).Append(new Choice("none", "It doesn't page: one call returns every row")).ToList();
            var answer = Ask(answers.Choice($"op.{name}.paging", "How does this endpoint page its results?", choices));
            var chosen = candidates.FirstOrDefault(c => c.Choice.Value == answer.Answer);
            return chosen.Build != null ? chosen.Build() : new YamlMap().Add("strategy", "none");
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
            var root = Header(name, "Retrieve", _table);
            var request = root.Map("request").Add("method", "GET").Add("path", path);
            CheckPathParams(path, parameters, request, r.IdParam, name);
            var response = root.Map("response").Add("rows", "$").Add("key", _keyPath ?? "$");
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

            var root = Header(name, binds, _table);
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
                if (JsonSchema(created.Content)?.Properties.TryGetValue(_keyProp ?? "id", out var id) == true && Heuristics.IsScalar(id))
                    response.Add("key", _keyPath ?? "$");
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
            var root = Header(name, "Delete", _table);
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
                var declared = inputs.Map(Heuristics.ParamName(param, where)).Add("type", Heuristics.ParamType(s));
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
                        pathParams.Map(p.Name).Add("param", Heuristics.ParamName(p.Name, "path parameter"));
                        break;
                    case ParameterLocation.Query:
                        Input(p.Name, p.Schema, p.Required, $"query parameter {p.Name}");
                        query.Map(p.Name).Add("param", Heuristics.ParamName(p.Name, "query parameter"));
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
                    template.Map(prop).Add("param", Heuristics.ParamName(prop, "body property"));
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
                var output = Heuristics.ParamName(prop, "response property");
                declaredOutputs.Add(output, type);
                var required = responseSchema.Required.Contains(prop);
                if (type == "string" && required)
                    outputs.Add(output, "$" + Heuristics.Step(prop));
                else
                {
                    var entry = outputs.Map(output).Add("path", "$" + Heuristics.Step(prop));
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

            var declaredApi = new YamlMap().Add("displayName", Heuristics.Humanise(name));
            _lastApi = (api, declaredApi);
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

        private string SchemaYaml(bool table, IEnumerable<(string Name, YamlMap Map)> apis)
        {
            var root = new YamlMap();
            if (table && _fields != null)
            {
                var bare = _table[(o.Prefix.Length + 1)..];
                root.Add("table", _table).Add("displayName", _tableDisplay).Add("displayCollectionName", _tableCollection)
                    .Add("externalName", bare).Add("primaryName", _primaryName ?? "");
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
            var declared = new YamlMap();
            foreach (var (name, map) in apis.OrderBy(a => a.Name, StringComparer.Ordinal))
                declared.Add(name, map);
            if (declared.Count > 0)
                root.Add("customApis", declared);
            return root.Count == 0 ? "" : root.ToString();
        }
    }

    /// <summary>
    /// Validates drafted descriptors against the drafted schema file: operation name to its errors (empty when valid).
    /// A schema file that does not parse is an error on every operation. Never throws for invalid content.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Check(string schemaYaml, IReadOnlyDictionary<string, string> descriptors, DraftOptions o)
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        SourceSchema schema;
        try
        {
            schema = SchemaParser.Parse(schemaYaml, $"{o.ServiceInstance}.yaml");
        }
        catch (SchemaException ex)
        {
            foreach (var name in descriptors.Keys)
                result[name] = new[] { "schema: " + ex.Message };
            return result;
        }
        var reserved = ReservedNames.For(o.ReservedNames ?? Array.Empty<string>());
        foreach (var (name, yaml) in descriptors)
        {
            try
            {
                result[name] = DescriptorValidator.Validate(DescriptorParser.Parse(yaml, name + ".yaml"), reserved, schema).Select(e => e.ToString()).ToList();
            }
            catch (DescriptorException ex)
            {
                result[name] = new[] { ex.Message };
            }
        }
        return result;
    }
}
