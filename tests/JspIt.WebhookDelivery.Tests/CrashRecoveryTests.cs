using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Sqlite;
using JspIt.WebhookDelivery.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace JspIt.WebhookDelivery.Tests;

/// <summary>
/// Simulates process crashes against a durable SQLite database and shows the resulting at-least-once behaviour:
/// work is never lost, but a receiver can see the same delivery twice.
/// </summary>
public sealed class CrashRecoveryTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestSetup.NewClock();
    private readonly TempDatabase _db = new();
    private MockReceiver _receiver = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _receiver = await MockReceiver.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _receiver.DisposeAsync();
        _db.Dispose();
    }

    /// <summary>A store that "crashes" (throws) when asked to record an attempt, i.e. after the HTTP request was sent.</summary>
    private sealed class CrashBeforeRecordingStore(IDeliveryStore inner) : IDeliveryStore
    {
        public Task<EnqueueResult> EnqueueAsync(NewDelivery delivery, CancellationToken cancellationToken = default) => inner.EnqueueAsync(delivery, cancellationToken);

        public Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken cancellationToken = default) => inner.GetAsync(deliveryId, cancellationToken);

        public Task<DeliveryRecord?> FindByIdempotencyKeyAsync(string destinationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
            inner.FindByIdempotencyKeyAsync(destinationId, idempotencyKey, cancellationToken);

        public Task<IReadOnlyList<DeliveryRecord>> LeaseDueAsync(LeaseRequest request, CancellationToken cancellationToken = default) => inner.LeaseDueAsync(request, cancellationToken);

        public Task<bool> CompleteAttemptAsync(AttemptCompletion completion, CancellationToken cancellationToken = default) =>
            throw new IOException("Simulated crash: process died before the outcome was persisted.");

        public Task<CancelResult> CancelAsync(string deliveryId, DateTimeOffset now, CancellationToken cancellationToken = default) => inner.CancelAsync(deliveryId, now, cancellationToken);

        public Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken cancellationToken = default) => inner.GetAttemptsAsync(deliveryId, cancellationToken);

        public Task<int> PurgeAsync(DateTimeOffset completedBefore, CancellationToken cancellationToken = default) => inner.PurgeAsync(completedBefore, cancellationToken);
    }

    [Fact]
    public async Task Crash_after_send_before_recording_causes_duplicate_delivery_with_same_idempotency_key()
    {
        string deliveryId;
        using (var store = SqliteDeliveryStore.ForFile(_db.Path))
        await using (var crashing = TestSetup.CreateService(new CrashBeforeRecordingStore(store), _clock, TestSetup.LocalDestination(_receiver)))
        {
            deliveryId = (await crashing.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;
            await crashing.ProcessDueAsync(Ct); // receiver gets the request; the outcome is never recorded
        }

        Assert.Single(_receiver.Requests);

        // "Restart": a new process opens the same database. The lease (30 s) must expire first.
        using var restartedStore = SqliteDeliveryStore.ForFile(_db.Path);
        await using var restarted = TestSetup.CreateService(restartedStore, _clock, TestSetup.LocalDestination(_receiver));
        Assert.Equal(0, await restarted.ProcessDueAsync(Ct));
        Assert.Equal(DeliveryStatus.InFlight, (await restarted.GetAsync(deliveryId, Ct))!.Status);

        _clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, await restarted.ProcessDueAsync(Ct));

        var requests = _receiver.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Header(WebhookHeaders.IdempotencyKey), requests[1].Header(WebhookHeaders.IdempotencyKey));
        Assert.Equal(["1", "2"], requests.Select(r => r.Header(WebhookHeaders.Attempt)));
        Assert.All(requests, r => Assert.Equal(WebhookVerificationStatus.Valid, TestSetup.Verify(r, _clock)));

        // A receiver that de-duplicates on Idempotency-Key processes the event once.
        var processed = requests.Select(r => r.Header(WebhookHeaders.IdempotencyKey)).Distinct().Count();
        Assert.Equal(1, processed);

        var record = (await restarted.GetAsync(deliveryId, Ct))!;
        Assert.Equal(DeliveryStatus.Succeeded, record.Status);
        Assert.Equal(2, record.AttemptCount);
    }

    [Fact]
    public async Task Crash_after_lease_before_send_is_recovered_after_lease_expiry()
    {
        using var store = SqliteDeliveryStore.ForFile(_db.Path);
        await using var service = TestSetup.CreateService(store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        // A dispatcher in another process leases the delivery and dies immediately.
        Assert.Single(await store.LeaseDueAsync(new LeaseRequest(_clock.GetUtcNow(), 10, "dead-process", TimeSpan.FromSeconds(30)), Ct));
        Assert.Equal(0, await service.ProcessDueAsync(Ct));

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await service.ProcessDueAsync(Ct));

        Assert.Single(_receiver.Requests);
        Assert.Equal("2", _receiver.Requests.Single().Header(WebhookHeaders.Attempt));
        var attempts = await service.GetAttemptsAsync(id, Ct);
        Assert.Equal(AttemptOutcome.Succeeded, Assert.Single(attempts).Outcome);
    }

    [Fact]
    public async Task Crashed_attempts_count_toward_max_attempts()
    {
        using var store = SqliteDeliveryStore.ForFile(_db.Path);
        var destination = TestSetup.LocalDestination(_receiver, configure: d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = d.SigningKeys, AllowInsecureHttp = true, AllowLoopback = true, RetryPolicy = TestSetup.FastRetry(maxAttempts: 2),
        });
        await using var service = TestSetup.CreateService(store, _clock, destination);
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        for (var i = 0; i < 2; i++) // two "poison" crashes
        {
            Assert.Single(await store.LeaseDueAsync(new LeaseRequest(_clock.GetUtcNow(), 10, "crashy", TimeSpan.FromSeconds(30)), Ct));
            _clock.Advance(TimeSpan.FromSeconds(31));
        }

        await service.ProcessDueAsync(Ct);

        Assert.Empty(_receiver.Requests);
        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Failed, record.Status);
        Assert.Equal(DeliveryErrorCategories.AttemptsExhausted, record.LastErrorCategory);
    }

    [Fact]
    public async Task Pending_deliveries_survive_restart()
    {
        using (var store = SqliteDeliveryStore.ForFile(_db.Path))
        await using (var beforeRestart = TestSetup.CreateService(store, _clock, TestSetup.LocalDestination(_receiver)))
        {
            for (var i = 0; i < 3; i++)
            {
                await beforeRestart.EnqueueAsync(TestSetup.Message(key: $"evt_{i}"), Ct);
            }
        }

        using var reopened = SqliteDeliveryStore.ForFile(_db.Path);
        await using var afterRestart = TestSetup.CreateService(reopened, _clock, TestSetup.LocalDestination(_receiver));
        Assert.Equal(3, await afterRestart.ProcessDueAsync(Ct));
        Assert.Equal(3, _receiver.Requests.Select(r => r.Header(WebhookHeaders.IdempotencyKey)).Distinct().Count());
    }
}
