using JspIt.WebhookDelivery.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace JspIt.WebhookDelivery.Tests.Support;

public static class TestSetup
{
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly WebhookSigningKey KeyA = WebhookSigningKey.FromBase64("key-a", Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()));

    public static readonly WebhookSigningKey KeyB = WebhookSigningKey.FromBase64("key-b", Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray()));

    public static RetryPolicy FastRetry(int maxAttempts = 4) => new()
    {
        MaxAttempts = maxAttempts,
        InitialDelay = TimeSpan.FromSeconds(1),
        BackoffMultiplier = 2,
        MaxDelay = TimeSpan.FromSeconds(30),
        UseJitter = false,
        MaxDeliveryAge = TimeSpan.FromHours(1),
    };

    /// <summary>A destination pointing at a loopback mock receiver, with the explicit local-only opt-ins.</summary>
    public static WebhookDestination LocalDestination(MockReceiver receiver, string id = "local", Func<WebhookDestination, WebhookDestination>? configure = null)
    {
        var destination = new WebhookDestination
        {
            Id = id,
            Url = receiver.Url,
            SigningKeys = [KeyA],
            AllowInsecureHttp = true,
            AllowLoopback = true,
            RetryPolicy = FastRetry(),
            Timeout = TimeSpan.FromSeconds(5),
        };
        return configure?.Invoke(destination) ?? destination;
    }

    public static WebhookDeliveryService CreateService(IDeliveryStore store, TimeProvider time, params WebhookDestination[] destinations) =>
        CreateService(store, time, null, destinations);

    public static WebhookDeliveryService CreateService(IDeliveryStore store, TimeProvider time, Action<WebhookDeliveryOptions>? configure, params WebhookDestination[] destinations)
    {
        var options = new WebhookDeliveryOptions { LeaseDuration = TimeSpan.FromSeconds(30), InstanceId = "test-" + Guid.NewGuid().ToString("N")[..6] };
        foreach (var d in destinations)
        {
            options.Destinations.Add(d);
        }

        configure?.Invoke(options);
        return new WebhookDeliveryService(store, options, timeProvider: time);
    }

    public static FakeTimeProvider NewClock() => new(Epoch);

    public static WebhookMessage Message(string key = "evt_1", string destinationId = "local", string json = """{"id":"evt_1","amount":42}""") =>
        WebhookMessage.FromJson(destinationId, "invoice.paid", json, key);

    /// <summary>Verifies a captured request with the given keys, at the given clock.</summary>
    public static WebhookVerificationStatus Verify(ReceivedRequest request, TimeProvider time, params WebhookSigningKey[] keys) =>
        new WebhookSignatureVerifier(keys.Length == 0 ? [KeyA] : keys, timeProvider: time).Verify(request.Header, request.Body);
}
