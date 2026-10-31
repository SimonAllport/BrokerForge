namespace BrokerForge.Descriptors.Validation;

/// <summary>Headers a descriptor may not declare: Authorization plus whatever the instance's auth mode sets. Case-insensitive.</summary>
public sealed record ReservedHeaders(IReadOnlySet<string> Names)
{
    public static ReservedHeaders For(IEnumerable<string> authModeHeaders) =>
        new(new HashSet<string>(authModeHeaders.Append("Authorization"), StringComparer.OrdinalIgnoreCase));

    public bool Contains(string header) => Names.Contains(header);
}
