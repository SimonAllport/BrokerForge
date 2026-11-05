using Azure.Data.Tables;
using BrokerForge.Auth.Tests;
using BrokerForge.Host.Generation;

namespace BrokerForge.Contract.Tests.Generation;

/// <summary>
/// The lease that keeps one Generate per environment and instance across host processes (plan 4 task 3, D-36). Every
/// implementation passes the same contract.
/// </summary>
public abstract class GenerationLeaseContract
{
    protected const string Env = "00000000-0000-0000-0000-00000000000a";
    protected static readonly TimeSpan Duration = TimeSpan.FromMinutes(20);
    protected readonly ConnectTests.MutableTime Time = new() { Now = new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero) };

    protected abstract IGenerationLease Lease { get; }

    [Fact]
    public async Task Lease_SecondHolderRefused()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "InventoryAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ExpiredTakenOver()
    {
        var first = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        Time.Now += Duration + TimeSpan.FromSeconds(1);
        var second = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        // The first holder, back from wherever it was, cannot free the new holder's lease.
        await Lease.ReleaseAsync(Env, "WarehouseAPI", first!, CancellationToken.None);
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ReleaseByOtherTokenIgnored()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
        await Lease.ReleaseAsync(Env, "WarehouseAPI", "not-the-holder", CancellationToken.None);
        Assert.Null(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_ReleasedThenAcquiredAgain()
    {
        var token = await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None);
        await Lease.ReleaseAsync(Env, "WarehouseAPI", token!, CancellationToken.None);
        Assert.NotNull(await Lease.TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None));
    }

    [Fact]
    public async Task Lease_KeyIgnoresCase()
    {
        Assert.NotNull(await Lease.TryAcquireAsync(Env.ToUpperInvariant(), "WarehouseAPI", Duration, CancellationToken.None));
        Assert.Null(await Lease.TryAcquireAsync(Env, "warehouseapi", Duration, CancellationToken.None));
    }
}

public sealed class InMemoryGenerationLeaseTests : GenerationLeaseContract
{
    public InMemoryGenerationLeaseTests() => Lease = new InMemoryGenerationLease(Time);
    protected override IGenerationLease Lease { get; }
}

[Collection(AzuriteCollection.Name)]
public sealed class TableGenerationLeaseTests : GenerationLeaseContract
{
    public TableGenerationLeaseTests(AzuriteFixture _)
    {
        var table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bflease" + Guid.NewGuid().ToString("N")[..12]);
        table.CreateIfNotExists();
        Lease = new TableGenerationLease(table, Time);
    }

    protected override IGenerationLease Lease { get; }

    [Fact]
    public async Task Lease_TwoHostsOneTable_SecondRefused()
    {
        // Two leases on the same table stand for two host replicas.
        var table = new TableServiceClient(AzuriteFixture.ConnectionString).GetTableClient("bflease" + Guid.NewGuid().ToString("N")[..12]);
        table.CreateIfNotExists();
        var a = new TableGenerationLease(table, Time);
        var b = new TableGenerationLease(table, Time);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => (i % 2 == 0 ? a : b).TryAcquireAsync(Env, "WarehouseAPI", Duration, CancellationToken.None)));
        Assert.Single(results, r => r != null);
    }
}
