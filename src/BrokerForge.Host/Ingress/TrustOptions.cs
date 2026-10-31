namespace BrokerForge.Host.Ingress;

/// <summary>
/// Who may call the host (HLD 9.2, D-09). Tokens must come from <see cref="TenantId"/> for
/// <see cref="Audience"/>; each plug-in identity (azp) is allowed for exactly one environment.
/// </summary>
public sealed class TrustOptions
{
    public string TenantId { get; set; } = "";
    public string Audience { get; set; } = "";
    public List<AllowedCaller> AllowedCallers { get; set; } = new();

    public IEnumerable<string> Problems()
    {
        if (string.IsNullOrWhiteSpace(TenantId)) yield return "Trust:TenantId is required";
        if (string.IsNullOrWhiteSpace(Audience)) yield return "Trust:Audience is required";
        if (AllowedCallers.Count == 0) yield return "Trust:AllowedCallers needs at least one { ClientId, EnvironmentId }";
        foreach (var c in AllowedCallers.Where(c => string.IsNullOrWhiteSpace(c.ClientId) || string.IsNullOrWhiteSpace(c.EnvironmentId)))
            yield return "every Trust:AllowedCallers entry needs ClientId and EnvironmentId";
        foreach (var dup in AllowedCallers.GroupBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Trust:AllowedCallers lists client {dup.Key} more than once; one plug-in identity serves one environment";
    }
}

public sealed class AllowedCaller
{
    public string ClientId { get; set; } = "";
    public string EnvironmentId { get; set; } = "";
}
