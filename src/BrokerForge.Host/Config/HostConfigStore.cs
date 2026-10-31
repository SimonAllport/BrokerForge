using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Descriptors.Model;
using BrokerForge.Descriptors.Parsing;
using BrokerForge.Descriptors.Validation;
using BrokerForge.Keys;

namespace BrokerForge.Host.Config;

/// <summary>
/// Phase 1 configuration (D-10): service instances and object maps from host configuration, descriptors
/// from <c>Config:DescriptorsPath/&lt;instance&gt;/*.yaml</c>. Everything is parsed and validated at
/// startup; any error stops the host, so a bad descriptor never reaches a request.
/// </summary>
public sealed class HostConfigStore
{
    private static readonly HashSet<AuthMode> Phase1Modes = new() { AuthMode.Static, AuthMode.ServiceAccount };
    private static readonly HashSet<string> CrudMessages = new() { Messages.Retrieve, Messages.RetrieveMultiple, Messages.Create, Messages.Update, Messages.Delete };

    private readonly Dictionary<string, ServiceInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedMap> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Instance, string Operation), OperationDescriptor> _descriptors = new();

    public sealed record ResolvedMap(ObjectMap Map, IKeyStrategy Keys);

    public HostConfigStore(IConfiguration configuration)
    {
        var problems = new List<string>();
        LoadInstances(configuration.GetSection("Instances"), problems);
        LoadDescriptors(configuration["Config:DescriptorsPath"], problems);
        LoadMaps(configuration.GetSection("ObjectMaps"), problems);
        if (problems.Count > 0)
            throw new ConfigurationException("Host configuration is invalid:" + string.Concat(problems.Select(p => "\n - " + p)));
    }

    public IReadOnlyCollection<string> DescriptorNames => _descriptors.Keys.Select(k => $"{k.Instance}/{k.Operation}").ToList();

    public ServiceInstance? Instance(string name) => _instances.GetValueOrDefault(name);

    public ResolvedMap? Map(string table) => _maps.GetValueOrDefault(table);

    public OperationDescriptor? Descriptor(string instance, string operation) => _descriptors.GetValueOrDefault((instance, operation));

    private void LoadInstances(IConfigurationSection section, List<string> problems)
    {
        foreach (var s in section.GetChildren())
        {
            var o = s.Get<InstanceOptions>() ?? new InstanceOptions();
            var where = $"Instances:{s.Key}";
            if (o.BrokerType != "rest")
                problems.Add($"{where}:BrokerType must be rest in phase 1");
            if (!Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(baseUri.Query))
                problems.Add($"{where}:BaseUrl must be an absolute http(s) URL without a query");
            if (!Enum.TryParse<AuthMode>(o.Auth.Mode, out var mode))
            {
                problems.Add($"{where}:Auth:Mode must be one of {string.Join(", ", Enum.GetNames<AuthMode>())}");
                continue;
            }
            if (!Phase1Modes.Contains(mode))
                problems.Add($"{where}:Auth:Mode {mode} is not supported until phase 3");
            foreach (var reference in o.Secrets.Values.Append(o.Auth.SecretRef).Where(r => r != null))
                try { SecretRef.Parse(reference!); }
                catch (FormatException ex) { problems.Add($"{where}: {ex.Message}"); }

            _instances[s.Key] = new ServiceInstance(
                s.Key, o.BrokerType, o.BaseUrl.TrimEnd('/'),
                new AuthConfig(mode, o.Auth.StaticKind, o.Auth.HeaderName, o.Auth.QueryParam, o.Auth.Username, o.Auth.SecretRef, o.Auth.Scope),
                o.Config, o.Secrets);
        }
    }

    private void LoadDescriptors(string? root, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            problems.Add($"Config:DescriptorsPath `{root}` is not a directory");
            return;
        }
        foreach (var instance in _instances.Values)
        {
            var dir = Path.Combine(root, instance.Name);
            if (!Directory.Exists(dir))
                continue;
            var reserved = AuthHeaders.For(instance);
            foreach (var file in Directory.GetFiles(dir, "*.yaml").Order(StringComparer.Ordinal))
            {
                var source = $"{instance.Name}/{Path.GetFileName(file)}";
                try
                {
                    var d = DescriptorParser.Parse(File.ReadAllText(file), source);
                    problems.AddRange(DescriptorValidator.Validate(d, reserved).Select(e => $"{source}: {e}"));
                    if (!_descriptors.TryAdd((instance.Name, d.Operation), d))
                        problems.Add($"{source}: operation {d.Operation} is declared twice for {instance.Name}");
                }
                catch (DescriptorException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }
    }

    private void LoadMaps(IConfigurationSection section, List<string> problems)
    {
        foreach (var s in section.GetChildren())
        {
            var o = s.Get<ObjectMapOptions>() ?? new ObjectMapOptions();
            var where = $"ObjectMaps:{s.Key}";
            if (!_instances.ContainsKey(o.ServiceInstance))
            {
                problems.Add($"{where}:ServiceInstance `{o.ServiceInstance}` is not configured");
                continue;
            }
            if (!Enum.TryParse<KeyKind>(o.KeyKind, out var kind))
            {
                problems.Add($"{where}:KeyKind must be one of {string.Join(", ", Enum.GetNames<KeyKind>())}");
                continue;
            }
            IKeyStrategy? keys = (o.KeyStrategy, kind) switch
            {
                ("native", KeyKind.Guid) => new NativeGuidKeyStrategy(),
                ("reversible", not KeyKind.Guid) => new ReversibleKeyStrategy(kind),
                _ => null,
            };
            if (keys == null)
            {
                problems.Add($"{where}: KeyStrategy {o.KeyStrategy} with KeyKind {o.KeyKind} is not available in phase 1 (native needs Guid; reversible needs Int32, Int64 or String; keymap is phase 5)");
                continue;
            }
            foreach (var (message, operation) in o.Operations)
            {
                if (!CrudMessages.Contains(message))
                    problems.Add($"{where}:Operations:{message} is not a virtual table message");
                else if (!_descriptors.TryGetValue((o.ServiceInstance, operation), out var d))
                    problems.Add($"{where}:Operations:{message}: no descriptor {operation} for {o.ServiceInstance}");
                else if (d.Binds != message)
                    problems.Add($"{where}:Operations:{message}: {operation} binds {d.Binds}");
                else if (d.Table != null && d.Table != s.Key)
                    problems.Add($"{where}:Operations:{message}: {operation} is for table {d.Table}");
            }
            _maps[s.Key] = new ResolvedMap(new ObjectMap(o.ServiceInstance, s.Key, o.KeyStrategy, o.KeyKind, o.Operations), keys);
        }
    }

    private sealed class InstanceOptions
    {
        public string BrokerType { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public AuthOptions Auth { get; set; } = new();
        public Dictionary<string, string> Config { get; set; } = new();
        public Dictionary<string, string> Secrets { get; set; } = new();
    }

    private sealed class AuthOptions
    {
        public string Mode { get; set; } = "";
        public string? StaticKind { get; set; }
        public string? HeaderName { get; set; }
        public string? QueryParam { get; set; }
        public string? Username { get; set; }
        public string? SecretRef { get; set; }
        public string? Scope { get; set; }
    }

    private sealed class ObjectMapOptions
    {
        public string ServiceInstance { get; set; } = "";
        public string KeyStrategy { get; set; } = "reversible";
        public string KeyKind { get; set; } = "";
        public Dictionary<string, string> Operations { get; set; } = new();
    }
}
