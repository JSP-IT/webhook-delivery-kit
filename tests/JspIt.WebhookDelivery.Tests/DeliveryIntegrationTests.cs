using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace JspIt.WebhookDelivery.Tests;

/// <summary>End-to-end delivery against real HTTP endpoints on 127.0.0.1.</summary>
public sealed class DeliveryIntegrationTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = TestSetup.NewClock();
    private readonly InMemoryDeliveryStore _store = new();
    private MockReceiver _receiver = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _receiver = await MockReceiver.StartAsync();

    public async ValueTask DisposeAsync() => await _receiver.DisposeAsync();

    private WebhookDeliveryService Service(Func<WebhookDestination, WebhookDestination>? configure = null) =>
        TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(_receiver, configure: configure));

    [Fact]
    public async Task Delivers_signed_request_with_protocol_headers()
    {
        await using var service = Service();
        var enqueued = await service.EnqueueAsync(TestSetup.Message(), Ct);

        Assert.Equal(1, await service.ProcessDueAsync(Ct));

        var request = Assert.Single(_receiver.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/hooks/secret-path-token?token=query-secret", request.Path);
        Assert.Equal("""{"id":"evt_1","amount":42}""", System.Text.Encoding.UTF8.GetString(request.Body));
        Assert.Equal("evt_1", request.Header(WebhookHeaders.IdempotencyKey));
        Assert.Equal("invoice.paid", request.Header(WebhookHeaders.Event));
        Assert.Equal("1", request.Header(WebhookHeaders.Attempt));
        Assert.StartsWith("application/json", request.Header("Content-Type"), StringComparison.Ordinal);
        Assert.StartsWith("JspIt.WebhookDelivery/", request.Header("User-Agent"), StringComparison.Ordinal);
        Assert.Null(request.Header("Cookie"));
        Assert.Equal(WebhookVerificationStatus.Valid, TestSetup.Verify(request, _clock));

        var record = (await service.GetAsync(enqueued.Record.Id, Ct))!;
        Assert.Equal(DeliveryStatus.Succeeded, record.Status);
        Assert.Equal(1, record.AttemptCount);
        Assert.Equal(200, record.LastStatusCode);
    }

    [Fact]
    public async Task Retries_with_backoff_reusing_the_idempotency_key_and_fresh_signatures()
    {
        _receiver.Respond = (_, i) => i < 2 ? new ReceiverResponse(503) : ReceiverResponse.Ok;
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct); // attempt 1 → 503, retry in 1 s
        var afterFirst = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Pending, afterFirst.Status);
        Assert.Equal(TestSetup.Epoch.AddSeconds(1), afterFirst.NextAttemptAt);

        Assert.Equal(0, await service.ProcessDueAsync(Ct)); // not due yet
        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.ProcessDueAsync(Ct); // attempt 2 → 503, retry in 2 s
        Assert.Equal(_clock.GetUtcNow().AddSeconds(2), (await service.GetAsync(id, Ct))!.NextAttemptAt);

        _clock.Advance(TimeSpan.FromSeconds(2));
        await service.ProcessDueAsync(Ct); // attempt 3 → 200

        var requests = _receiver.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.All(requests, r => Assert.Equal("evt_1", r.Header(WebhookHeaders.IdempotencyKey)));
        Assert.Equal(["1", "2", "3"], requests.Select(r => r.Header(WebhookHeaders.Attempt)));
        Assert.Equal(3, requests.Select(r => r.Header(WebhookHeaders.Timestamp)).Distinct().Count());
        Assert.Equal(WebhookVerificationStatus.Valid, TestSetup.Verify(requests[2], _clock));

        Assert.Equal(DeliveryStatus.Succeeded, (await service.GetAsync(id, Ct))!.Status);
        var attempts = await service.GetAttemptsAsync(id, Ct);
        Assert.Equal([AttemptOutcome.RetryScheduled, AttemptOutcome.RetryScheduled, AttemptOutcome.Succeeded], attempts.Select(a => a.Outcome));
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(500);
        await using var service = Service(d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = d.SigningKeys, AllowInsecureHttp = true, AllowLoopback = true, RetryPolicy = TestSetup.FastRetry(maxAttempts: 3),
        });
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        for (var i = 0; i < 10; i++)
        {
            await service.ProcessDueAsync(Ct);
            _clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.Equal(3, _receiver.Requests.Count);
        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Failed, record.Status);
        Assert.Equal(500, record.LastStatusCode);
        Assert.Contains("final attempt 3/3", record.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Honors_retry_after_on_429()
    {
        _receiver.Respond = (_, i) => i == 0 ? new ReceiverResponse(429, Headers: new Dictionary<string, string> { ["Retry-After"] = "20" }) : ReceiverResponse.Ok;
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        Assert.Equal(TestSetup.Epoch.AddSeconds(20), (await service.GetAsync(id, Ct))!.NextAttemptAt);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Non_retryable_4xx_fails_immediately(int status)
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(status);
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);
        _clock.Advance(TimeSpan.FromHours(1));
        await service.ProcessDueAsync(Ct);

        Assert.Single(_receiver.Requests);
        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Failed, record.Status);
        Assert.Equal(status, record.LastStatusCode);
        Assert.Equal(DeliveryErrorCategories.HttpStatus, record.LastErrorCategory);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Redirects_are_never_followed(int status)
    {
        await using var redirectTarget = await MockReceiver.StartAsync();
        _receiver.Respond = (_, _) => new ReceiverResponse(status, Headers: new Dictionary<string, string> { ["Location"] = redirectTarget.Url.ToString() });
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        Assert.Single(_receiver.Requests);
        Assert.Empty(redirectTarget.Requests);
        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Failed, record.Status);
        Assert.Equal(DeliveryErrorCategories.RedirectNotFollowed, record.LastErrorCategory);
    }

    [Fact]
    public async Task Timeout_is_retryable_and_bounded()
    {
        _receiver.Respond = (_, i) => i == 0 ? new ReceiverResponse(Delay: TimeSpan.FromSeconds(10)) : ReceiverResponse.Ok;
        await using var service = Service(d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = d.SigningKeys, AllowInsecureHttp = true, AllowLoopback = true, RetryPolicy = d.RetryPolicy,
            Timeout = TimeSpan.FromMilliseconds(500),
        });
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        var started = DateTime.UtcNow;
        await service.ProcessDueAsync(Ct);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "attempt should be cut off by the destination timeout");

        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.Equal(DeliveryErrorCategories.Timeout, record.LastErrorCategory);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.ProcessDueAsync(Ct);
        Assert.Equal(DeliveryStatus.Succeeded, (await service.GetAsync(id, Ct))!.Status);

        // The receiver saw two requests even though only one succeeded from the sender's view:
        // a timed-out attempt may still have been processed. This is at-least-once delivery.
        Assert.Equal(2, _receiver.Requests.Count);
    }

    [Fact]
    public async Task Connection_refused_is_retryable_network_error()
    {
        var port = _receiver.BaseUrl.Port;
        await _receiver.DisposeAsync();
        _receiver = await MockReceiver.StartAsync(); // keep DisposeAsync valid; use the freed port for the dead endpoint

        await using var service = TestSetup.CreateService(_store, _clock, new WebhookDestination
        {
            Id = "dead", Url = new Uri($"http://127.0.0.1:{port}/hook"), SigningKeys = [TestSetup.KeyA], AllowInsecureHttp = true, AllowLoopback = true,
            RetryPolicy = TestSetup.FastRetry(), Timeout = TimeSpan.FromSeconds(3),
        });
        var id = (await service.EnqueueAsync(TestSetup.Message(destinationId: "dead"), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Pending, record.Status);
        Assert.True(record.LastErrorCategory is DeliveryErrorCategories.Network or DeliveryErrorCategories.Timeout, record.LastErrorCategory);
    }

    [Fact]
    public async Task Loopback_is_blocked_without_explicit_opt_in()
    {
        await using var service = Service(d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = d.SigningKeys, AllowInsecureHttp = true, AllowLoopback = false, RetryPolicy = d.RetryPolicy,
        });
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        Assert.Empty(_receiver.Requests);
        var record = (await service.GetAsync(id, Ct))!;
        Assert.Equal(DeliveryStatus.Failed, record.Status);
        Assert.Equal(DeliveryErrorCategories.DestinationBlocked, record.LastErrorCategory);
    }

    [Fact]
    public async Task Host_name_resolving_to_loopback_is_blocked_after_dns_resolution()
    {
        await using var service = TestSetup.CreateService(_store, _clock, new WebhookDestination
        {
            Id = "byname", Url = new Uri($"http://localhost:{_receiver.BaseUrl.Port}/hook"), SigningKeys = [TestSetup.KeyA], AllowInsecureHttp = true,
            RetryPolicy = TestSetup.FastRetry(),
        });
        var id = (await service.EnqueueAsync(TestSetup.Message(destinationId: "byname"), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        Assert.Empty(_receiver.Requests);
        Assert.Equal(DeliveryErrorCategories.DestinationBlocked, (await service.GetAsync(id, Ct))!.LastErrorCategory);
    }

    [Fact]
    public async Task Persisted_error_text_and_logs_never_contain_url_path_or_query()
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(503);
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;

        await service.ProcessDueAsync(Ct);

        var record = (await service.GetAsync(id, Ct))!;
        foreach (var text in new[] { record.LastError, (await service.GetAttemptsAsync(id, Ct))[0].ErrorMessage })
        {
            Assert.DoesNotContain("secret-path-token", text, StringComparison.Ordinal);
            Assert.DoesNotContain("query-secret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("must not be stored", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Duplicate_enqueue_returns_existing_delivery_and_sends_once()
    {
        await using var service = Service();
        var first = await service.EnqueueAsync(TestSetup.Message(), Ct);
        var second = await service.EnqueueAsync(TestSetup.Message(), Ct);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Record.Id, second.Record.Id);

        await service.ProcessDueAsync(Ct);
        await service.EnqueueAsync(TestSetup.Message(), Ct); // after success: still the same delivery, not re-sent
        await service.ProcessDueAsync(Ct);
        Assert.Single(_receiver.Requests);
    }

    [Fact]
    public async Task Reusing_a_key_for_different_content_is_a_conflict()
    {
        await using var service = Service();
        await service.EnqueueAsync(TestSetup.Message(), Ct);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() =>
            service.EnqueueAsync(TestSetup.Message(json: """{"id":"evt_1","amount":43}"""), Ct));
    }

    [Fact]
    public async Task Static_headers_are_sent()
    {
        await using var service = Service(d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = d.SigningKeys, AllowInsecureHttp = true, AllowLoopback = true,
            Headers = new Dictionary<string, string> { ["Authorization"] = "Bearer receiver-token", ["X-Tenant"] = "t1" },
        });
        await service.EnqueueAsync(TestSetup.Message(), Ct);
        await service.ProcessDueAsync(Ct);

        var request = Assert.Single(_receiver.Requests);
        Assert.Equal("Bearer receiver-token", request.Header("Authorization"));
        Assert.Equal("t1", request.Header("X-Tenant"));
    }

    [Fact]
    public async Task Key_rotation_at_runtime_applies_to_pending_retries()
    {
        _receiver.Respond = (_, i) => i == 0 ? new ReceiverResponse(503) : ReceiverResponse.Ok;
        await using var service = Service();
        await service.EnqueueAsync(TestSetup.Message(), Ct);
        await service.ProcessDueAsync(Ct); // signed with A only

        // Phase 1 of rotation: sign with old and new key.
        service.UpsertDestination(TestSetup.LocalDestination(_receiver, configure: d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = [TestSetup.KeyA, TestSetup.KeyB], AllowInsecureHttp = true, AllowLoopback = true, RetryPolicy = d.RetryPolicy,
        }));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await service.ProcessDueAsync(Ct);

        var requests = _receiver.Requests.ToArray();
        Assert.Equal(WebhookVerificationStatus.SignatureMismatch, TestSetup.Verify(requests[0], _clock, TestSetup.KeyB));
        Assert.Equal(WebhookVerificationStatus.Valid, TestSetup.Verify(requests[1], _clock, TestSetup.KeyA));
        Assert.Equal(WebhookVerificationStatus.Valid, TestSetup.Verify(requests[1], _clock, TestSetup.KeyB));
    }

    [Fact]
    public async Task Body_only_compatibility_scheme_is_verifiable_by_existing_receivers()
    {
        var legacyKey = WebhookSigningKey.FromUtf8("legacy", "your-webhook-secret-at-least-16-chars");
        await using var service = Service(d => new WebhookDestination
        {
            Id = d.Id, Url = d.Url, SigningKeys = [legacyKey], SignatureScheme = WebhookSignatureScheme.BodyHmacSha256, AllowInsecureHttp = true, AllowLoopback = true,
        });
        await service.EnqueueAsync(WebhookMessage.FromJson("local", "invoice.paid", """{"event_id":"1"}""", "evt_1"), Ct);
        await service.ProcessDueAsync(Ct);

        var request = Assert.Single(_receiver.Requests);
        Assert.StartsWith("sha256=", request.Header(WebhookHeaders.LegacySignature), StringComparison.Ordinal);
        Assert.Equal("evt_1", request.Header(WebhookHeaders.IdempotencyKey));
        var verifier = new WebhookSignatureVerifier([legacyKey], new WebhookSignatureVerifierOptions { Scheme = WebhookSignatureScheme.BodyHmacSha256 });
        Assert.Equal(WebhookVerificationStatus.Valid, verifier.Verify(request.Header, request.Body));
    }

    [Fact]
    public async Task Deliveries_for_a_removed_destination_fail_instead_of_going_elsewhere()
    {
        await using (var first = Service())
        {
            await first.EnqueueAsync(TestSetup.Message(), Ct);
        }

        await using var other = await MockReceiver.StartAsync();
        await using var service = TestSetup.CreateService(_store, _clock, TestSetup.LocalDestination(other, id: "different"));
        await service.ProcessDueAsync(Ct);

        Assert.Empty(other.Requests);
        Assert.Empty(_receiver.Requests);
        var record = (await _store.FindByIdempotencyKeyAsync("local", "evt_1", Ct))!;
        Assert.Equal(DeliveryErrorCategories.UnknownDestination, record.LastErrorCategory);
    }

    [Fact]
    public async Task Deliveries_past_max_age_fail_without_sending()
    {
        _receiver.Respond = (_, _) => new ReceiverResponse(503);
        await using var service = Service();
        var id = (await service.EnqueueAsync(TestSetup.Message(), Ct)).Record.Id;
        _clock.Advance(TimeSpan.FromHours(2)); // FastRetry MaxDeliveryAge = 1 h

        await service.ProcessDueAsync(Ct);

        Assert.Empty(_receiver.Requests);
        Assert.Equal(DeliveryErrorCategories.Expired, (await service.GetAsync(id, Ct))!.LastErrorCategory);
    }

    [Fact]
    public async Task Concurrent_deliveries_respect_max_concurrency()
    {
        var current = 0;
        var peak = 0;
        _receiver.Respond = (_, _) =>
        {
            var now = Interlocked.Increment(ref current);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            Thread.Sleep(100);
            Interlocked.Decrement(ref current);
            return ReceiverResponse.Ok;
        };
        await using var service = TestSetup.CreateService(_store, _clock, o => o.MaxConcurrency = 3, TestSetup.LocalDestination(_receiver));
        for (var i = 0; i < 9; i++)
        {
            await service.EnqueueAsync(TestSetup.Message(key: $"evt_{i}"), Ct);
        }

        Assert.Equal(9, await service.ProcessDueAsync(Ct));
        Assert.Equal(9, _receiver.Requests.Count);
        Assert.InRange(peak, 1, 3);
    }
}
