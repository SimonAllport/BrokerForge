using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;

namespace BrokerForge.Auth.Credentials;

/// <summary>
/// Azure Table Storage store (HLD D-25): PartitionKey = service instance, RowKey = user object ID. Kind, status
/// and times are plain columns; tokens and the SSO credential are one Data Protection payload.
/// </summary>
public sealed class TableUserCredentialStore(TableClient table, IDataProtectionProvider protection) : IUserCredentialStore
{
    public const string Purpose = "BrokerForge.UserCredential.v1";
    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private sealed record Secrets(string? AccessToken, string? RefreshToken, string? Username, string? Secret);

    public async Task<UserCredential?> GetAsync(string instance, string userObjectId, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        var row = await table.GetEntityIfExistsAsync<TableEntity>(pk, rk, cancellationToken: ct);
        if (!row.HasValue)
            return null;
        var e = row.Value!;
        var secrets = JsonSerializer.Deserialize<Secrets>(_protector.Unprotect(e.GetString("Payload")))!;
        return new UserCredential(
            Enum.Parse<CredentialKind>(e.GetString("Kind")),
            Enum.Parse<CredentialStatus>(e.GetString("Status")),
            secrets.AccessToken, secrets.RefreshToken, e.GetDateTimeOffset("ExpiresAt"), secrets.Username, secrets.Secret,
            e.GetDateTimeOffset("ConnectedAt")!.Value);
    }

    public async Task SaveAsync(string instance, string userObjectId, UserCredential credential, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        var payload = _protector.Protect(JsonSerializer.Serialize(new Secrets(credential.AccessToken, credential.RefreshToken, credential.Username, credential.Secret)));
        await table.UpsertEntityAsync(new TableEntity(pk, rk)
        {
            ["Kind"] = credential.Kind.ToString(),
            ["Status"] = credential.Status.ToString(),
            ["ExpiresAt"] = credential.ExpiresAt,
            ["ConnectedAt"] = credential.ConnectedAt,
            ["Payload"] = payload,
        }, TableUpdateMode.Replace, ct);
    }

    public async Task SetStatusAsync(string instance, string userObjectId, CredentialStatus status, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        try
        {
            await table.UpdateEntityAsync(new TableEntity(pk, rk) { ["Status"] = status.ToString() }, ETag.All, TableUpdateMode.Merge, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
        }
    }

    public async Task DeleteAsync(string instance, string userObjectId, CancellationToken ct)
    {
        var (pk, rk) = CredentialKeys.Check(instance, userObjectId);
        await table.DeleteEntityAsync(pk, rk, ETag.All, ct);
    }
}
