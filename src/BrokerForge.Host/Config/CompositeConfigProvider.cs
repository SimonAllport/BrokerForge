using System.Collections.Concurrent;
using BrokerForge.Abstractions;

namespace BrokerForge.Host.Config;

/// <summary>
/// Builds one store per environment from the files and that environment's designs, on first use and again after
/// <c>ttl</c> or <see cref="Invalidate"/>. Without a design source every environment gets <see cref="Files"/>.
/// <list type="bullet">
/// <item>A build that fails is cached for <see cref="FailureCacheTime"/> (or <c>ttl</c> if shorter), so a broken or
/// unreachable environment is not read again on every request; a fixed design is picked up after that.</item>
/// <item>When the source cannot be read for a transient reason (<see cref="DesignSourceUnavailableException"/>), the last good store
/// is served, with a warning on every request, until one more <c>ttl</c> has passed after it expired. Then, or with
/// no good store, every request gets a <see cref="ConfigurationException"/> with the source's message.</item>
/// <item>A design that is wrong, or a read that fails for a lasting reason (such as 400 or 403), is never covered by an
/// older store: the environment fails until it is fixed.</item>
/// </list>
/// </summary>
public sealed class CompositeConfigProvider(HostConfigStore files, IDesignSource? designs, TimeProvider time, TimeSpan ttl, ILogger? log = null) : IHostConfigProvider
{
    /// <summary>How long a failed build is reused before the source is read again.</summary>
    public static readonly TimeSpan FailureCacheTime = TimeSpan.FromSeconds(30);

    private sealed record Entry(Task<HostConfigStore> Store, DateTimeOffset Started);

    private sealed record Good(HostConfigStore Store, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Good> _lastGood = new(StringComparer.Ordinal);
    private readonly TimeSpan _failureTtl = ttl < FailureCacheTime ? ttl : FailureCacheTime;

    public HostConfigStore Files => files;

    public async Task<HostConfigStore> ForEnvironmentAsync(string environmentId, CancellationToken ct)
    {
        if (designs == null)
            return files;
        var key = Key(environmentId);
        Entry entry;
        while (true)
        {
            var now = time.GetUtcNow();
            if (_entries.TryGetValue(key, out var cached) && Current(cached, now))
            {
                entry = cached;
                break;
            }
            // Concurrent first calls share one build; the loser of the swap waits for the winner's.
            var fresh = new Entry(Build(key, now), now);
            if (cached == null ? _entries.TryAdd(key, fresh) : _entries.TryUpdate(key, fresh, cached))
            {
                entry = fresh;
                break;
            }
        }
        try
        {
            return await entry.Store.WaitAsync(ct);
        }
        catch (DesignSourceUnavailableException ex) when (entry.Store.IsFaulted)
        {
            if (_lastGood.TryGetValue(key, out var good) && time.GetUtcNow() < good.Expires + ttl)
            {
                log?.LogWarning("{Reason} Serving the connection designs read before {Expired:O}, at most until {Until:O}.", ex.Message, good.Expires, good.Expires + ttl);
                return good.Store;
            }
            throw new ConfigurationException(ex.Message);
        }
    }

    public void Invalidate(string environmentId) => _entries.TryRemove(Key(environmentId), out _);

    /// <summary>A running build is always shared; a finished one lasts <c>ttl</c>, a failed one the failure time.</summary>
    private bool Current(Entry entry, DateTimeOffset now) =>
        !entry.Store.IsCompleted
        || (entry.Store.IsCompletedSuccessfully ? entry.Started + ttl > now : entry.Started + _failureTtl > now);

    private async Task<HostConfigStore> Build(string environmentId, DateTimeOffset started)
    {
        // Not the caller's token: the build is shared, and one caller giving up must not fail the others.
        var loaded = await designs!.LoadAsync(environmentId, CancellationToken.None);
        var store = new HostConfigStore(DesignConfigInput.Merge(files.Input, environmentId, loaded));
        _lastGood[environmentId] = new Good(store, started + ttl);
        return store;
    }

    private static string Key(string environmentId) =>
        Guid.TryParse(environmentId, out var id)
            ? id.ToString("D")
            : throw new ConfigurationException($"Environment ID `{environmentId}` is not a GUID.");
}
