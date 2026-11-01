using System.Collections.Concurrent;

namespace BrokerForge.Auth.PerUser;

/// <summary>
/// One refresh at a time per (instance, user). Sources that rotate refresh tokens accept each one once, so two
/// parallel refreshes would make one of them fail and wrongly mark the user expired.
/// </summary>
internal sealed class RefreshGate
{
    private readonly ConcurrentDictionary<(string, string), SemaphoreSlim> _gates = new();

    public async Task<IDisposable> EnterAsync(string instance, string user, CancellationToken ct)
    {
        var gate = _gates.GetOrAdd((instance, user), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new Release(gate);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
