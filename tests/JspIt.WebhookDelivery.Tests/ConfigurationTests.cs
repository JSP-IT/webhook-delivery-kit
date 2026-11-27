using System.Net;
using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Tests.Support;

namespace JspIt.WebhookDelivery.Tests;

public class ConfigurationTests
{
    private static WebhookDestination Valid(Func<WebhookDestination, WebhookDestination>? change = null)
    {
        var d = new WebhookDestination { Id = "billing", Url = new Uri("https://hooks.example.com/in"), SigningKeys = [TestSetup.KeyA] };
        return change?.Invoke(d) ?? d;
    }

    private static WebhookDestination With(WebhookDestination d, Uri? url = null, IReadOnlyList<WebhookSigningKey>? keys = null, bool? http = null,
        TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? headers = null, IReadOnlyList<IPNetwork>? networks = null, string? id = null, int? maxPayload = null) =>
        new()
        {
            Id = id ?? d.Id,
            Url = url ?? d.Url,
            SigningKeys = keys ?? d.SigningKeys,
            AllowInsecureHttp = http ?? d.AllowInsecureHttp,
            Timeout = timeout ?? d.Timeout,
            Headers = headers ?? d.Headers,
            AllowedPrivateNetworks = networks ?? d.AllowedPrivateNetworks,
            MaxPayloadBytes = maxPayload ?? d.MaxPayloadBytes,
        };

    [Fact]
    public void Valid_destination_passes() => Valid().Validate();

    public static TheoryData<string, WebhookDestination> InvalidDestinations() => new()
    {
        { "http without opt-in", With(Valid(), url: new Uri("http://hooks.example.com/in")) },
        { "ftp scheme", With(Valid(), url: new Uri("ftp://hooks.example.com/in"), http: true) },
        { "user info", With(Valid(), url: new Uri("https://user:pass@hooks.example.com/in")) },
        { "fragment", With(Valid(), url: new Uri("https://hooks.example.com/in#x")) },
        { "relative url", With(Valid(), url: new Uri("/in", UriKind.Relative)) },
        { "no keys", With(Valid(), keys: []) },
        { "duplicate key ids", With(Valid(), keys: [TestSetup.KeyA, TestSetup.KeyA]) },
        { "zero timeout", With(Valid(), timeout: TimeSpan.Zero) },
        { "huge timeout", With(Valid(), timeout: TimeSpan.FromMinutes(5)) },
        { "bad id", With(Valid(), id: "has space") },
        { "huge payload", With(Valid(), maxPayload: 50 * 1024 * 1024) },
        { "reserved header Host", With(Valid(), headers: new Dictionary<string, string> { ["Host"] = "evil" }) },
        { "reserved header prefix", With(Valid(), headers: new Dictionary<string, string> { ["Webhook-Signature"] = "x" }) },
        { "content header", With(Valid(), headers: new Dictionary<string, string> { ["Content-Language"] = "fi" }) },
        { "reserved idempotency header", With(Valid(), headers: new Dictionary<string, string> { ["Idempotency-Key"] = "x" }) },
        { "header injection", With(Valid(), headers: new Dictionary<string, string> { ["X-Ok"] = "a\r\nX-Evil: 1" }) },
        { "too broad network", With(Valid(), networks: [IPNetwork.Parse("0.0.0.0/0")]) },
    };

    [Theory]
    [MemberData(nameof(InvalidDestinations))]
    public void Invalid_destinations_are_rejected(string because, WebhookDestination destination)
    {
        Assert.NotNull(because);
        Assert.Throws<WebhookConfigurationException>(destination.Validate);
    }

    [Fact]
    public void Service_requires_at_least_one_destination_and_unique_ids()
    {
        var store = new InMemoryDeliveryStore();
        Assert.Throws<WebhookConfigurationException>(() => new WebhookDeliveryService(store, new WebhookDeliveryOptions()));

        var options = new WebhookDeliveryOptions();
        options.Destinations.Add(Valid());
        options.Destinations.Add(Valid());
        Assert.Throws<WebhookConfigurationException>(() => new WebhookDeliveryService(store, options));
    }

    [Fact]
    public void Lease_must_outlast_the_attempt_timeout()
    {
        var options = new WebhookDeliveryOptions { LeaseDuration = TimeSpan.FromSeconds(10) };
        options.Destinations.Add(With(Valid(), timeout: TimeSpan.FromSeconds(30)));
        Assert.Throws<WebhookConfigurationException>(() => new WebhookDeliveryService(new InMemoryDeliveryStore(), options));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10_000)]
    public void Dispatcher_bounds_are_enforced(int batchSize)
    {
        var options = new WebhookDeliveryOptions { BatchSize = batchSize };
        options.Destinations.Add(Valid());
        Assert.Throws<WebhookConfigurationException>(() => new WebhookDeliveryService(new InMemoryDeliveryStore(), options));
    }

    [Fact]
    public async Task Enqueue_only_accepts_configured_destinations_never_urls()
    {
        var options = new WebhookDeliveryOptions();
        options.Destinations.Add(Valid());
        await using var service = new WebhookDeliveryService(new InMemoryDeliveryStore(), options);

        var ex = await Assert.ThrowsAsync<UnknownDestinationException>(() =>
            service.EnqueueAsync(WebhookMessage.FromJson("https://attacker.example/x", "e", "{}", "k"), TestContext.Current.CancellationToken));
        Assert.Equal("https://attacker.example/x", ex.DestinationId);
    }

    [Theory]
    [InlineData("bad key", "invoice.paid", "application/json")]
    [InlineData("", "invoice.paid", "application/json")]
    [InlineData("k", ".bad", "application/json")]
    [InlineData("k", "has space", "application/json")]
    [InlineData("k", "invoice.paid", "not a media type")]
    public async Task Invalid_messages_are_rejected(string key, string eventType, string contentType)
    {
        var options = new WebhookDeliveryOptions();
        options.Destinations.Add(Valid());
        await using var service = new WebhookDeliveryService(new InMemoryDeliveryStore(), options);
        var message = new WebhookMessage("billing", eventType, "{}"u8.ToArray(), key) { ContentType = contentType };
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAsync(message, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_at_enqueue()
    {
        var options = new WebhookDeliveryOptions();
        options.Destinations.Add(With(Valid(), maxPayload: 10));
        await using var service = new WebhookDeliveryService(new InMemoryDeliveryStore(), options);
        await Assert.ThrowsAsync<ArgumentException>(() => service.EnqueueAsync(WebhookMessage.FromJson("billing", "e", "{\"x\":\"0123456789\"}", "k"), TestContext.Current.CancellationToken));
    }
}
