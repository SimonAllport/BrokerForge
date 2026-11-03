using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Auth.Credentials;
using BrokerForge.Host.Admin;

namespace BrokerForge.Contract.Tests;

/// <summary>The one-off admin command that sets an instance secret (tools/dev/set-instance-secret.sh): it reads the value from a reference and never prints it.</summary>
public class SetInstanceSecretCommandTests
{
    private const string Env = "00000000-0000-0000-0000-00000000000a";
    private const string Value = "test-only-not-a-real-key-7f3a";

    private sealed class FakeResolver(string? value) : ISecretResolver
    {
        public Task<string> GetAsync(SecretRef reference, CancellationToken ct) =>
            value is null ? throw new CredentialUnavailableException($"Key Vault returned 404 for {reference}.") : Task.FromResult(value);
    }

    private static async Task<(int Code, string Output, InMemoryInstanceSecretStore Store)> Run(string? value, params string[] args)
    {
        var store = new InMemoryInstanceSecretStore();
        var output = new StringWriter();
        var code = await SetInstanceSecretCommand.RunAsync(args, [Env], store, new FakeResolver(value), output, CancellationToken.None);
        return (code, output.ToString(), store);
    }

    [Fact]
    public void SplitTakesTheCommandOffTheHostArguments()
    {
        Assert.Empty(SetInstanceSecretCommand.Split(["set-instance-secret", "--name", "apiKey"], out var command));
        Assert.Equal(["--name", "apiKey"], command!);
        Assert.Equal(["--urls", "x"], SetInstanceSecretCommand.Split(["--urls", "x"], out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task SetsTheSecretFromTheReferenceAndNeverPrintsIt()
    {
        var (code, output, store) = await Run(Value, "--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "kv:https://v.vault.azure.net/secrets/k");
        Assert.Equal(0, code);
        Assert.Equal(Value, await store.GetAsync(Env, "WarehouseAPI", "apiKey", default));
        Assert.Contains("Set instance secret `apiKey` of WarehouseAPI", output);
        Assert.DoesNotContain(Value, output);
    }

    [Theory]
    [InlineData("--environment", "00000000-0000-0000-0000-0000000000ff", "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "env:X")]
    [InlineData("--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "store:apiKey")]
    [InlineData("--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey")]
    [InlineData("--environment", Env, "--instance", "Warehouse_API", "--name", "apiKey", "--from", "env:X")]
    public async Task RefusesBadArgumentsAndSetsNothing(params string[] args)
    {
        var (code, output, store) = await Run(Value, args);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain(Value, output);
        Assert.False(await store.ExistsAsync(Env, "WarehouseAPI", "apiKey", default));
    }

    [Fact]
    public async Task UnreadableReferenceFailsNamingIt()
    {
        var (code, output, store) = await Run(null, "--environment", Env, "--instance", "WarehouseAPI", "--name", "apiKey", "--from", "kv:https://v.vault.azure.net/secrets/k");
        Assert.Equal(1, code);
        Assert.Contains("Could not read kv:https://v.vault.azure.net/secrets/k", output);
        Assert.False(await store.ExistsAsync(Env, "WarehouseAPI", "apiKey", default));
    }
}
