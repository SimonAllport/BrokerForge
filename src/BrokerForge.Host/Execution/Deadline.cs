namespace BrokerForge.Host.Execution;

/// <summary>
/// The end of one canonical request's budget (HLD 11: 90 seconds, inside the 2-minute sandbox limit).
/// Created once per request; retries and paging calls all draw on it.
/// </summary>
public sealed record Deadline(DateTimeOffset At)
{
    public static Deadline After(TimeSpan budget) => new(DateTimeOffset.UtcNow + budget);

    public TimeSpan Remaining => At - DateTimeOffset.UtcNow;

    public bool Passed => Remaining <= TimeSpan.Zero;
}
