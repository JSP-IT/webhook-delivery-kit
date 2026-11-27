using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Sqlite;
using JspIt.WebhookDelivery.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace JspIt.WebhookDelivery.Tests;

public sealed class CancellationTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestSetup.NewClock();
    private readonly TempDatabase _db = new();
    private SqliteDeliveryStore _store = null!;
    private MockReceiver _receiver = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _receiver = await MockReceiver.StartAsync();
        _store = SqliteDeliveryStore.ForFile(_db.Path);
    }

    public async ValueTask DisposeAsync()
    {
        await _receiver.DisposeAsync();
        _store.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Cancelling_a_pending_delivery_prevents_sending()
    {
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        Assert.Equal(CancelResult.Cancelled, await service.CancelAsync(id, Ct));
        Assert.Equal(0, await service.ProcessDueAsync(Ct));

        Assert.Empty(_receiver.Requests);
        Assert.Equal(DeliveryStatus.Cancelled, (await service.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task Cancelling_between_retries_stops_further_attempts()
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(503);
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;
        await service.ProcessDueAsync(Ct);

        await service.CancelAsync(id, Ct);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await service.ProcessDueAsync(Ct);

        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task Cancelling_an_in_flight_delivery_aborts_the_request()
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(Delay: TimeSpan.FromSeconds(30));
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        var processing = service.ProcessDueAsync(Ct);
        Assert.True(await _receiver.Arrived.WaitAsync(TimeSpan.FromSeconds(10), Ct), "request should reach the receiver");

        var started = DateTime.UtcNow;
        Assert.Equal(CancelResult.Cancelled, await service.CancelAsync(id, Ct));
        await processing.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));

        Assert.Equal(DeliveryStatus.Cancelled, (await service.GetAsync(id, Ct))!.Status);
        var attempt = Assert.Single(await service.GetAttemptsAsync(id, Ct));
        Assert.Equal(AttemptOutcome.Cancelled, attempt.Outcome);
    }

    [Fact]
    public async Task Cancelling_a_completed_delivery_reports_already_terminal()
    {
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;
        await service.ProcessDueAsync(Ct);

        Assert.Equal(CancelResult.AlreadyTerminal, await service.CancelAsync(id, Ct));
        Assert.Equal(CancelResult.NotFound, await service.CancelAsync("dlv_missing", Ct));
        Assert.Equal(DeliveryStatus.Succeeded, (await service.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task Stopping_the_dispatcher_mid_attempt_releases_the_delivery_for_retry()
    {
        _receiver.Respond = (_, i) => i == 0 ? new ReceiverResponse(Delay: TimeSpan.FromSeconds(30)) : ReceiverResponse.Ok;
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver));
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        using var stop = new CancellationTokenSource();
        var processing = service.ProcessDueAsync(stop.Token);
        Assert.True(await _receiver.Arrived.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await stop.CancelAsync();
        await processing.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(DeliveryErrorCategories.DispatcherStopping, record.LastErrorCategory);

        // Immediately eligible again, without waiting for a lease to expire.
        await service.ProcessDueAsync(Ct);
        Assert.Equal(DeliveryStatus.Succeeded, (await service.GetAsync(id, Ct))!.Status);
    }

    [Fact]
    public async Task RunAsync_delivers_in_background_and_returns_when_cancelled()
    {
        // Real clock: RunAsync waits on the poll interval.
        await using var service = TestSetup.CreateService(_store, TimeProvider.System, o => o.PollInterval = TimeSpan.FromMilliseconds(50), TestSetup.LocalDestination(_receiver));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = service.RunAsync(stop.Token);

        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;
        Assert.True(await _receiver.Arrived.WaitAsync(TimeSpan.FromSeconds(10), Ct));

        await WaitUntilAsync(async () => (await service.GetAsync(id, Ct))!.Status == DeliveryStatus.Succeeded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(Ct));

        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(loop.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Disposing_the_service_stops_RunAsync()
    {
        var service = TestSetup.CreateService(_store, TimeProvider.System, o => o.PollInterval = TimeSpan.FromMilliseconds(50), TestSetup.LocalDestination(_receiver));
        var loop = service.RunAsync(Ct);
        await service.DisposeAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.EnqueueAsync(TestSetup.Message(), Ct));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not met in time");
            await Task.Delay(25, Ct);
        }
    }
}
