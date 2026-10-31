namespace BrokerForge.Abstractions;

/// <summary>A secret reference: <c>kv:&lt;secret uri&gt;</c> or <c>env:&lt;VARIABLE&gt;</c>. Never the value.</summary>
public sealed record SecretRef(string Scheme, string Location)
{
    public static SecretRef Parse(string reference)
    {
        var colon = reference.IndexOf(':');
        if (colon <= 0 || colon == reference.Length - 1)
            throw new FormatException("A secret reference has the form kv:<secret uri> or env:<VARIABLE>.");
        var scheme = reference[..colon];
        if (scheme is not ("kv" or "env"))
            throw new FormatException($"Unknown secret reference scheme `{scheme}`; use kv or env.");
        return new SecretRef(scheme, reference[(colon + 1)..]);
    }

    public override string ToString() => $"{Scheme}:{Location}";
}

/// <summary>Resolves secret references. Only the host calls this; adapters and hooks never do.</summary>
public interface ISecretResolver
{
    Task<string> GetAsync(SecretRef reference, CancellationToken ct);
}
