using System.Collections.Concurrent;
using BrokerForge.Abstractions;

namespace BrokerForge.Host.Config;

/// <summary>
/// Builds one store per environment from the files and that environment's designs, on first use and again after
/// <c>ttl</c> or <see cref="Invalidate"/>. A build that fails is not cached, so the next call tries again and a
/// fixed design is picked up at once. Without a design source every environment gets <see cref="Files"/>.
/// </summary>
public sealed class CompositeConfigProvider(HostConfigStore files, IDesignSource? designs, TimeProvider time, TimeSpan ttl) : IHostConfigProvider
{
    private sealed record Entry(Task<HostConfigStore> Store, DateTimeOffset Until);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public HostConfigStore Files => files;

    public async Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct)
    {
        if (designs == null)
            return files;
        var key = Key(environmentId);
        while (true)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var entry) && entry.Until > now && !entry.Store.IsFaulted && !entry.Store.IsCanceled)
                return await entry.Store.WaitAsync(ct);
            // Concurrent first calls share one build; the loser of the swap waits for the winner's.
            var fresh = new Entry(Build(key), now + ttl);
            if (entry == null ? !_entries.TryAdd(key, fresh) : !_entries.TryUpdate(key, fresh, entry))
                continue;
            try
            {
                return await fresh.Store.WaitAsync(ct);
            }
            catch when (fresh.Store.IsFaulted || fresh.Store.IsCanceled)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, fresh));
                throw;
            }
        }
    }

    public void Invalidate(string environmentId) => _entries.TryRemove(Key(environmentId), out _);

    private async Task<HostConfigStore> Build(string environmentId)
    {
        // Not the caller's token: the build is shared, and one caller giving up must not fail the others.
        var loaded = await designs!.LoadAsync(environmentId, CancellationToken.None);
        return new HostConfigStore(DesignConfigInput.Merge(files.Input, environmentId, loaded));
    }

    private static string Key(string environmentId) =>
        Guid.TryParse(environmentId, out var id)
            ? id.ToString("D")
            : throw new ConfigurationException($"Environment ID `{environmentId}` is not a GUID.");
}
