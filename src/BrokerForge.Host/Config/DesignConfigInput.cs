using BrokerForge.Connections;

namespace BrokerForge.Host.Config;

/// <summary>
/// One environment's input: the file input plus that environment's connection designs (spec 6 step 5, 7). Each design
/// becomes an instance whose base URL and auth overrides come from its <see cref="EnvironmentSettings"/>, whose
/// secrets are <c>store:&lt;name&gt;</c> references, and whose descriptors and schema are labelled with the design
/// version. A name already used by the files is refused rather than one of the two being picked.
/// </summary>
public static class DesignConfigInput
{
    /// <summary>
    /// The <see cref="DesignAuth"/> string fields an environment's <c>bf_&lt;Instance&gt;_Auth</c> may override, by JSON
    /// name. Not <c>mode</c> or <c>asyncContext</c>: who the source sees the call as is the design's, approved with it,
    /// and an environment variable must not change it (final review I2).
    /// </summary>
    private static readonly Dictionary<string, Action<AuthOptions, string>> Overrides = new(StringComparer.Ordinal)
    {
        ["staticKind"] = (a, v) => a.StaticKind = v,
        ["headerName"] = (a, v) => a.HeaderName = v,
        ["queryParam"] = (a, v) => a.QueryParam = v,
        ["username"] = (a, v) => a.Username = v,
        ["scope"] = (a, v) => a.Scope = v,
        ["ssoKind"] = (a, v) => a.SsoKind = v,
    };

    /// <param name="designScopes">The scopes the environment's designs may use in ServiceAccount or Impersonate mode (<see cref="DesignScopeRules"/>).</param>
    public static ConfigInput Merge(ConfigInput files, string environmentId, IEnumerable<(ConnectionDesign Design, EnvironmentSettings Settings)> designs,
        IReadOnlyCollection<string>? designScopes = null)
    {
        var input = Copy(files);
        input.EnvironmentId = environmentId;
        var designTables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (design, settings) in designs)
        {
            var name = design.Instance;
            var clash = false;
            void Clash(string what)
            {
                input.Problems.Add($"{what} is configured both in host settings and as a connection design in environment {environmentId}");
                clash = true;
            }
            if (input.Designs.ContainsKey(name))
            {
                input.Problems.Add($"Instance {name} has two connection designs in environment {environmentId}");
                continue;
            }
            if (files.Instances.ContainsKey(name))
                Clash($"Instance {name}");
            if (design.Auth.OAuth != null && files.OAuthResources.ContainsKey(name))
                Clash($"OAuth resource {name} (of instance {name})");
            if (design.ObjectMap is { } m && files.ObjectMaps.ContainsKey(m.Table))
                Clash($"Table {m.Table} (of instance {name})");
            if (design.ObjectMap is { } t && designTables.TryGetValue(t.Table, out var owner))
            {
                input.Problems.Add($"Table {t.Table} is mapped by both connection designs {owner} and {name} in environment {environmentId}");
                clash = true;
            }
            if (clash)
                continue;
            if (design.ObjectMap is { } own)
                designTables[own.Table] = name;

            var incomplete = new List<string>();
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                incomplete.Add($"base URL (environment variable bf_{name}_BaseUrl)");
            input.Designs[name] = new DesignOrigin(environmentId, design.Version, incomplete);

            var auth = new AuthOptions
            {
                Mode = design.Auth.Mode,
                StaticKind = design.Auth.StaticKind,
                HeaderName = design.Auth.HeaderName,
                QueryParam = design.Auth.QueryParam,
                Username = design.Auth.Username,
                Scope = design.Auth.Scope,
                SsoKind = design.Auth.SsoKind,
                AsyncContext = design.Auth.AsyncContext,
                OAuthResource = design.Auth.OAuth != null ? name : null,
            };
            foreach (var (key, value) in settings.AuthOverrides.OrderBy(o => o.Key, StringComparer.Ordinal))
                if (Overrides.TryGetValue(key, out var set))
                    set(auth, value);
                else
                    input.Problems.Add($"bf_{name}_Auth in environment {environmentId} overrides `{key}`, which is not an auth setting of instance {name} (use one of {string.Join(", ", Overrides.Keys)})");
            // After the overrides, so an environment cannot override its way to a scope it may not use.
            input.Problems.AddRange(DesignScopeRules.Problems(name, environmentId, auth.Mode, auth.Scope, design.Auth.OAuth?.Scopes,
                designScopes ?? Array.Empty<string>()));
            var secrets = design.Auth.SecretNames.ToDictionary(n => n, n => "store:" + n, StringComparer.Ordinal);
            if (auth.Mode == "Static" && design.Auth.SecretNames.Count > 0)
                auth.SecretRef = "store:" + design.Auth.SecretNames[0];

            input.Instances[name] = new InstanceOptions
            {
                BrokerType = design.BrokerType,
                BaseUrl = settings.BaseUrl?.Trim() ?? "",
                Auth = auth,
                Secrets = secrets,
            };
            if (design.Auth.OAuth is { } o)
                input.OAuthResources[name] = new OAuthResourceOptions
                {
                    AuthorizeUrl = o.AuthorizeUrl,
                    TokenUrl = o.TokenUrl,
                    Scopes = o.Scopes.ToList(),
                    ClientId = o.ClientId,
                    UsesConnectApp = o.UsesConnectApp,
                    // A confidential client's secret is an instance secret like any other: kept in the host's store (HLD D-34).
                    ClientSecretRef = !o.UsesConnectApp && design.Auth.SecretNames.Contains("clientSecret") ? "store:clientSecret" : null,
                };
            if (design.ObjectMap is { } map)
                input.ObjectMaps[map.Table] = new ObjectMapOptions
                {
                    ServiceInstance = name,
                    KeyStrategy = map.KeyStrategy,
                    KeyKind = map.KeyKind,
                    Operations = map.Operations.ToDictionary(p => p.Key, p => p.Value),
                };
            foreach (var (operation, text) in design.Descriptors)
                input.DescriptorTexts[(name, operation + ".yaml")] = text;
            input.SchemaTexts[name] = design.Schema;
        }
        return input;
    }

    /// <summary>The file input is shared by every environment, so each one builds on its own copy.</summary>
    private static ConfigInput Copy(ConfigInput files)
    {
        var input = new ConfigInput { ConnectBaseUrl = files.ConnectBaseUrl, ConnectClientId = files.ConnectClientId };
        foreach (var (k, v) in files.Instances) input.Instances[k] = v;
        foreach (var (k, v) in files.OAuthResources) input.OAuthResources[k] = v;
        foreach (var (k, v) in files.ObjectMaps) input.ObjectMaps[k] = v;
        foreach (var (k, v) in files.DescriptorTexts) input.DescriptorTexts[k] = v;
        foreach (var (k, v) in files.SchemaTexts) input.SchemaTexts[k] = v;
        input.Problems.AddRange(files.Problems);
        input.DescriptorProblems.AddRange(files.DescriptorProblems);
        return input;
    }
}
