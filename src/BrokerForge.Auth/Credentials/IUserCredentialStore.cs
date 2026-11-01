using System.Text.RegularExpressions;

namespace BrokerForge.Auth.Credentials;

/// <summary>Per-user credentials keyed by service instance and the user's Entra object ID (HLD D-25, D-27).</summary>
public interface IUserCredentialStore
{
    Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct);
    Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct);

    /// <summary>Changes the status and keeps the payload; does nothing when there is no row.</summary>
    Task SetStatusAsync(string instance, string userObjectId, CredentialStatus status, CancellationToken ct);
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
