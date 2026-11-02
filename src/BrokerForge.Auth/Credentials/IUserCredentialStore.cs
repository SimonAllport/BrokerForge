using System.Text.RegularExpressions;

namespace BrokerForge.Auth.Credentials;

/// <summary>A stored credential and the row version it was read at.</summary>
public sealed record VersionedCredential(UserCredential Credential, string Version);

/// <summary>
/// Per-user credentials keyed by service instance and the user's Entra object ID (HLD D-25, D-27).
/// <see cref="SaveAsync"/> and <see cref="DeleteAsync"/> are the user's own actions on the connect page and always
/// win. A token refresh writes only through the conditional methods, so a refresh that races a disconnect, a
/// reconnect or another replica's refresh never overwrites what happened meanwhile.
/// </summary>
public interface IUserCredentialStore
{
    Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct);
    Task<VersionedCredential?> GetVersionedAsync(string instance, string userObjectId, CancellationToken ct);
    Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct);

    /// <summary>Replaces the row only if it still has <paramref name="version"/>; false if it changed or is gone.</summary>
    Task<bool> ReplaceIfUnchangedAsync(string instance, string userObjectId, UserCredential credential, string version, CancellationToken ct);

    /// <summary>Changes the status, keeping the payload, only if the row still has <paramref name="version"/>; false if it changed or is gone.</summary>
    Task<bool> SetStatusIfUnchangedAsync(string instance, string userObjectId, CredentialStatus status, string version, CancellationToken ct);
    Task DeleteAsync(string instance, string userObjectId, CancellationToken ct);
}

internal static partial class CredentialKeys
{
    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex InstanceName();

    /// <summary>The validated (instance, object ID) pair; the object ID in lower-case "D" form so one user has one row.</summary>
    public static (string Instance, string User) Check(string instance, string userObjectId)
    {
        if (!InstanceName().IsMatch(instance))
            throw new ArgumentException("A service instance name is letters and digits only.", nameof(instance));
        if (!Guid.TryParse(userObjectId, out var oid))
            throw new ArgumentException("A user object ID is a GUID.", nameof(userObjectId));
        return (instance, oid.ToString("D"));
    }
}
