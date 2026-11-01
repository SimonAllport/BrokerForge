using System.Collections.Concurrent;

namespace BrokerForge.Auth.Credentials;

/// <summary>For tests and local runs: unencrypted, lost on restart.</summary>
public sealed class InMemoryUserCredentialStore : IUserCredentialStore
{
    private readonly ConcurrentDictionary<(string, string), UserCredential> _rows = new();

    public Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct) =>
        Task.FromResult(_rows.GetValueOrDefault(CredentialKeys.Check(instance, userObjectId)));

    public Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct)
    {
        _rows[CredentialKeys.Check(instance, userObjectId)] = credential;
        return Task.CompletedTask;
    }

    public Task SetStatusAsync(string instance, string userObjectId, CredentialStatus status, CancellationToken ct)
    {
        var key = CredentialKeys.Check(instance, userObjectId);
        if (_rows.TryGetValue(key, out var row))
            _rows[key] = row with { Status = status };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string instance, string userObjectId, CancellationToken ct)
    {
        _rows.TryRemove(CredentialKeys.Check(instance, userObjectId), out _);
        return Task.CompletedTask;
    }
}
