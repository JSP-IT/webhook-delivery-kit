using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Sqlite;
using JspIt.WebhookDelivery.Tests.Support;

namespace JspIt.WebhookDelivery.Tests;

/// <summary>
/// Behaviour every <see cref="IDeliveryStore"/> must have. Third-party stores can copy this class to verify themselves.
/// </summary>
public abstract class StoreContractTests : IDisposable
{
    protected static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private readonly Lazy<IDeliveryStore> _store;

    protected StoreContractTests() => _store = new Lazy<IDeliveryStore>(CreateStore);

    protected IDeliveryStore Store => _store.Value;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract IDeliveryStore CreateStore();

    public virtual void Dispose()
    {
        if (_store.IsValueCreated && _store.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected static NewDelivery New(string id = "d1", string key = "k1", DateTimeOffset? at = null, string destination = "dest") => new()
    {
        Id = id,
        DestinationId = destination,
        IdempotencyKey = key,
        EventType = "invoice.paid",
        ContentType = "application/json",
        Payload = "{}"u8.ToArray(),
        PayloadSha256 = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a",
        CreatedAt = at ?? TestSetup.Epoch,
        ExpiresAt = (at ?? TestSetup.Epoch).AddHours(1),
    };

    protected static AttemptCompletion Complete(DeliveryRecord leased, AttemptOutcome outcome, DateTimeOffset at, DateTimeOffset? next = null) => new()
    {
        DeliveryId = leased.Id,
        LeaseToken = leased.LeaseToken!,
        LeaseOwner = leased.LeaseOwner!,
        AttemptNumber = leased.AttemptCount,
        StartedAt = at,
        CompletedAt = at,
        Outcome = outcome,
        NextAttemptAt = next,
        StatusCode = outcome == AttemptOutcome.Succeeded ? 200 : 503,
        ErrorCategory = outcome == AttemptOutcome.Succeeded ? null : DeliveryErrorCategories.HttpStatus,
        ErrorMessage = outcome == AttemptOutcome.Succeeded ? null : "Receiver responded 503.",
    };

    [Fact]
    public async Task Enqueue_is_idempotent_per_destination_and_key()
    {
        var first = await Store.EnqueueAsync(New("d1", "k1"), Ct);
        var second = await Store.EnqueueAsync(New("d2", "k1"), Ct);
        var otherDestination = await Store.EnqueueAsync(New("d3", "k1", destination: "other"), Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal("d1", second.Record.Id);
        Assert.True(otherDestination.Created);
        Assert.Equal(DeliveryStatus.Pending, first.Record.Status);
        Assert.Equal("{}"u8.ToArray(), first.Record.Payload.ToArray());
        Assert.Null(await Store.GetAsync("d2", Ct));
        Assert.Equal("d1", (await Store.FindByIdempotencyKeyAsync("dest", "k1", Ct))!.Id);
    }

    [Fact]
    public async Task Lease_claims_due_records_increments_attempts_and_issues_token()
    {
        await Store.EnqueueAsync(New("d1", "k1"), Ct);
        await Store.EnqueueAsync(New("d2", "k2", TestSetup.Epoch.AddMinutes(5)), Ct); // not yet due

        var leased = await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 10, "owner-a", Lease), Ct);

        var record = Assert.Single(leased);
        Assert.Equal("d1", record.Id);
        Assert.Equal(DeliveryStatus.InFlight, record.Status);
        Assert.Equal(1, record.AttemptCount);
        Assert.Equal("owner-a", record.LeaseOwner);
        Assert.False(string.IsNullOrEmpty(record.LeaseToken));
        Assert.Equal(TestSetup.Epoch + Lease, record.LeaseExpiresAt);
    }

    [Fact]
    public async Task Leased_record_is_not_leased_again_until_lease_expires()
    {
        await Store.EnqueueAsync(New(), Ct);
        var first = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 10, "a", Lease), Ct));

        Assert.Empty(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch.AddSeconds(29), 10, "b", Lease), Ct));

        var reclaimed = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch.AddSeconds(30), 10, "b", Lease), Ct));
        Assert.Equal(2, reclaimed.AttemptCount);
        Assert.Equal("b", reclaimed.LeaseOwner);
        Assert.NotEqual(first.LeaseToken, reclaimed.LeaseToken);
        Assert.Equal(DeliveryErrorCategories.LeaseExpired, reclaimed.LastErrorCategory);
    }

    [Fact]
    public async Task Completion_with_stale_lease_token_is_recorded_but_not_applied()
    {
        await Store.EnqueueAsync(New(), Ct);
        var stale = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 10, "a", Lease), Ct));
        var current = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch.AddMinutes(1), 10, "b", Lease), Ct));

        Assert.False(await Store.CompleteAttemptAsync(Complete(stale, AttemptOutcome.Succeeded, TestSetup.Epoch.AddMinutes(1)), Ct));
        Assert.Equal(DeliveryStatus.InFlight, (await Store.GetAsync("d1", Ct))!.Status);

        Assert.True(await Store.CompleteAttemptAsync(Complete(current, AttemptOutcome.Succeeded, TestSetup.Epoch.AddMinutes(1)), Ct));
        var done = (await Store.GetAsync("d1", Ct))!;
        Assert.Equal(DeliveryStatus.Succeeded, done.Status);
        Assert.Null(done.LeaseToken);
        Assert.Null(done.LastErrorCategory);
        Assert.Equal(200, done.LastStatusCode);

        var attempts = await Store.GetAttemptsAsync("d1", Ct);
        Assert.Equal(2, attempts.Count);
    }

    [Fact]
    public async Task Retry_schedules_next_attempt_and_returns_to_pending()
    {
        await Store.EnqueueAsync(New(), Ct);
        var leased = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 10, "a", Lease), Ct));
        var next = TestSetup.Epoch.AddSeconds(10);

        Assert.True(await Store.CompleteAttemptAsync(Complete(leased, AttemptOutcome.RetryScheduled, TestSetup.Epoch, next), Ct));

        var record = (await Store.GetAsync("d1", Ct))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(next, record.NextAttemptAt);
        Assert.Equal(503, record.LastStatusCode);
        Assert.Equal(DeliveryErrorCategories.HttpStatus, record.LastErrorCategory);
        Assert.Null(record.CompletedAt);

        Assert.Empty(await Store.LeaseDueAsync(new LeaseRequest(next.AddMilliseconds(-1), 10, "a", Lease), Ct));
        Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(next, 10, "a", Lease), Ct));
    }

    [Fact]
    public async Task Cancel_moves_pending_and_in_flight_to_cancelled_and_blocks_later_completion()
    {
        await Store.EnqueueAsync(New("d1", "k1"), Ct);
        await Store.EnqueueAsync(New("d2", "k2"), Ct);
        var leased = (await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 1, "a", Lease), Ct)).Single();
        var pendingId = leased.Id == "d1" ? "d2" : "d1";

        Assert.Equal(CancelResult.Cancelled, await Store.CancelAsync(leased.Id, TestSetup.Epoch, Ct));
        Assert.Equal(CancelResult.Cancelled, await Store.CancelAsync(pendingId, TestSetup.Epoch, Ct));
        Assert.Equal(CancelResult.AlreadyTerminal, await Store.CancelAsync(pendingId, TestSetup.Epoch, Ct));
        Assert.Equal(CancelResult.NotFound, await Store.CancelAsync("missing", TestSetup.Epoch, Ct));

        Assert.False(await Store.CompleteAttemptAsync(Complete(leased, AttemptOutcome.Succeeded, TestSetup.Epoch), Ct));
        Assert.Equal(DeliveryStatus.Cancelled, (await Store.GetAsync(leased.Id, Ct))!.Status);
        Assert.Empty(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch.AddHours(1), 10, "a", Lease), Ct));
    }

    [Fact]
    public async Task Lease_respects_batch_size_and_orders_by_due_time()
    {
        for (var i = 0; i < 5; i++)
        {
            await Store.EnqueueAsync(New($"d{i}", $"k{i}", TestSetup.Epoch.AddSeconds(-i)), Ct);
        }

        var leased = await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 3, "a", Lease), Ct);
        Assert.Equal(["d4", "d3", "d2"], leased.Select(r => r.Id));
    }

    [Fact]
    public async Task Concurrent_leases_never_hand_out_the_same_record_twice()
    {
        for (var i = 0; i < 50; i++)
        {
            await Store.EnqueueAsync(New($"d{i}", $"k{i}"), Ct);
        }

        var tasks = Enumerable.Range(0, 8).Select(n => Task.Run(() => LeaseFromAsync(n), Ct));
        var all = (await Task.WhenAll(tasks)).SelectMany(x => x).ToList();

        Assert.Equal(50, all.Count);
        Assert.Equal(50, all.Distinct().Count());
    }

    /// <summary>Overridable so the SQLite tests can lease through separate store instances (separate connections).</summary>
    protected virtual async Task<List<string>> LeaseFromAsync(int worker)
    {
        var ids = new List<string>();
        while (true)
        {
            var batch = await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 3, $"w{worker}", Lease), Ct);
            if (batch.Count == 0)
            {
                return ids;
            }

            ids.AddRange(batch.Select(r => r.Id));
        }
    }

    [Fact]
    public async Task Purge_removes_only_old_terminal_deliveries()
    {
        await Store.EnqueueAsync(New("done", "k1"), Ct);
        await Store.EnqueueAsync(New("pending", "k2", TestSetup.Epoch.AddHours(1)), Ct);
        var leased = Assert.Single(await Store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 10, "a", Lease), Ct));
        await Store.CompleteAttemptAsync(Complete(leased, AttemptOutcome.Succeeded, TestSetup.Epoch), Ct);

        Assert.Equal(0, await Store.PurgeAsync(TestSetup.Epoch, Ct));
        Assert.Equal(1, await Store.PurgeAsync(TestSetup.Epoch.AddSeconds(1), Ct));
        Assert.Null(await Store.GetAsync("done", Ct));
        Assert.Empty(await Store.GetAttemptsAsync("done", Ct));
        Assert.NotNull(await Store.GetAsync("pending", Ct));
        Assert.True((await Store.EnqueueAsync(New("again", "k1"), Ct)).Created);
    }
}

public sealed class InMemoryStoreContractTests : StoreContractTests
{
    protected override IDeliveryStore CreateStore() => new InMemoryDeliveryStore();
}

public sealed class SqliteStoreContractTests : StoreContractTests
{
    private readonly TempDatabase _db = new();
    private readonly List<SqliteDeliveryStore> _extra = [];

    protected override IDeliveryStore CreateStore() => SqliteDeliveryStore.ForFile(_db.Path);

    protected override async Task<List<string>> LeaseFromAsync(int worker)
    {
        // Each worker gets its own store instance, as separate processes would.
        var store = SqliteDeliveryStore.ForFile(_db.Path);
        lock (_extra)
        {
            _extra.Add(store);
        }

        var ids = new List<string>();
        while (true)
        {
            var batch = await store.LeaseDueAsync(new LeaseRequest(TestSetup.Epoch, 3, $"w{worker}", Lease), Ct);
            if (batch.Count == 0)
            {
                return ids;
            }

            ids.AddRange(batch.Select(r => r.Id));
        }
    }

    [Fact]
    public async Task Data_survives_reopening_the_database()
    {
        await Store.EnqueueAsync(New(), Ct);
        using var reopened = SqliteDeliveryStore.ForFile(_db.Path);
        var record = await reopened.GetAsync("d1", Ct);
        Assert.NotNull(record);
        Assert.Equal(TestSetup.Epoch, record.CreatedAt);
        Assert.Equal(TestSetup.Epoch.AddHours(1), record.ExpiresAt);
    }

    [Fact]
    public async Task Newer_schema_version_is_refused()
    {
        await Store.EnqueueAsync(New(), Ct);
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db.Path}"))
        {
            await connection.OpenAsync(Ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO wdk_schema (version) VALUES (99);";
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        using var newer = SqliteDeliveryStore.ForFile(_db.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newer.GetAsync("d1", Ct));
    }

    public override void Dispose()
    {
        foreach (var store in _extra)
        {
            store.Dispose();
        }

        base.Dispose();
        _db.Dispose();
    }
}
