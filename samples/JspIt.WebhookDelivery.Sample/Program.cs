// Runnable end-to-end sample: a local receiver and a sender in one process.
//
//   dotnet run --project samples/JspIt.WebhookDelivery.Sample
//
// The receiver verifies signatures, de-duplicates on Idempotency-Key, and fails the first request with 503
// so you can watch a retry. The sender persists deliveries in a SQLite file and dispatches them.
// Exit code 0 means every event was delivered and processed exactly once by the receiver
// (even though the transport itself is at-least-once).

using System.Collections.Concurrent;
using JspIt.WebhookDelivery;
using JspIt.WebhookDelivery.Persistence;
using JspIt.WebhookDelivery.Sqlite;

// ---------------------------------------------------------------------------------------------
// Shared secret. In production, load it from a secret store and give it to the receiver out of band.
// ---------------------------------------------------------------------------------------------
var signingKey = WebhookSigningKey.Generate("2026-11");

// ---------------------------------------------------------------------------------------------
// Receiver (what your customer would run). Minimal API on 127.0.0.1 with a random port.
// ---------------------------------------------------------------------------------------------
var receiverBuilder = WebApplication.CreateSlimBuilder(args);
receiverBuilder.Logging.ClearProviders();
receiverBuilder.WebHost.UseUrls("http://127.0.0.1:0");
var receiver = receiverBuilder.Build();

var verifier = new WebhookSignatureVerifier([signingKey]);
var processedKeys = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
var requestCount = 0;

receiver.MapPost("/webhooks/orders", async (HttpRequest request) =>
{
    var number = Interlocked.Increment(ref requestCount);

    // 1. Read the raw body; verify against the bytes received, never a re-serialised object.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    // 2. Verify the signature and timestamp before doing anything else.
    var status = verifier.Verify(name => request.Headers.TryGetValue(name, out var v) ? v.ToString() : null, body);
    if (status != WebhookVerificationStatus.Valid)
    {
        Console.WriteLine($"  [receiver] #{number} rejected: {status}");
        return Results.Unauthorized();
    }

    // Simulate a transient outage on the very first request.
    if (number == 1)
    {
        Console.WriteLine("  [receiver] #1 simulating 503 Service Unavailable");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // 3. De-duplicate: delivery is at-least-once, so the same key can arrive more than once.
    var key = request.Headers[WebhookHeaders.IdempotencyKey].ToString();
    if (!processedKeys.TryAdd(key, true))
    {
        Console.WriteLine($"  [receiver] #{number} duplicate {key} ignored");
        return Results.Ok();
    }

    Console.WriteLine($"  [receiver] #{number} processed {key} ({request.Headers[WebhookHeaders.Event]}, attempt {request.Headers[WebhookHeaders.Attempt]})");
    return Results.Ok();
});

await receiver.StartAsync();
var receiverUrl = new Uri(new Uri(receiver.Urls.First()), "/webhooks/orders");

// ---------------------------------------------------------------------------------------------
// Sender (your application).
// ---------------------------------------------------------------------------------------------
var databasePath = Path.Combine(Path.GetTempPath(), $"webhook-sample-{Guid.NewGuid():N}.db");
using var store = SqliteDeliveryStore.ForFile(databasePath);

var options = new WebhookDeliveryOptions { PollInterval = TimeSpan.FromMilliseconds(100) };
options.Destinations.Add(new WebhookDestination
{
    Id = "orders-receiver",
    Url = receiverUrl,
    SigningKeys = [signingKey],
    RetryPolicy = new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(500), MaxAttempts = 5, UseJitter = false },
    Timeout = TimeSpan.FromSeconds(5),

    // LOCAL DEMO ONLY. Real destinations must be https and publicly routable; these two opt-ins exist
    // so the sample can talk to a plain-http receiver on 127.0.0.1.
    AllowInsecureHttp = true,
    AllowLoopback = true,
});

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));
await using var service = new WebhookDeliveryService(store, options, loggerFactory);

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var dispatcher = service.RunAsync(stop.Token);

Console.WriteLine($"Database: {databasePath}");
Console.WriteLine("Enqueueing 3 events (plus one accidental duplicate enqueue)...");
var ids = new List<string>();
foreach (var orderId in new[] { "1001", "1002", "1003" })
{
    var message = WebhookMessage.FromJson("orders-receiver", "order.created", $$"""{"order_id":"{{orderId}}","total":"19.90"}""", $"evt_order_{orderId}");
    ids.Add((await service.EnqueueAsync(message)).Record.Id);
}

var duplicate = await service.EnqueueAsync(WebhookMessage.FromJson("orders-receiver", "order.created", """{"order_id":"1001","total":"19.90"}""", "evt_order_1001"));
Console.WriteLine($"Duplicate enqueue created a new delivery: {duplicate.Created}");

// Wait for all deliveries to reach a terminal state.
while (!stop.IsCancellationRequested)
{
    var records = await Task.WhenAll(ids.Select(id => service.GetAsync(id)));
    if (records.All(r => r!.IsTerminal))
    {
        break;
    }

    await Task.Delay(100);
}

await stop.CancelAsync();
await dispatcher;

Console.WriteLine();
Console.WriteLine("Delivery summary:");
var allSucceeded = true;
foreach (var id in ids)
{
    var record = (await service.GetAsync(id))!;
    var attempts = await service.GetAttemptsAsync(id);
    Console.WriteLine($"  {record.IdempotencyKey}: {record.Status} after {record.AttemptCount} attempt(s) [{string.Join(", ", attempts.Select(a => a.Outcome))}]");
    allSucceeded &= record.Status == DeliveryStatus.Succeeded;
}

Console.WriteLine($"Receiver processed {processedKeys.Count} unique event(s) from {requestCount} request(s).");

await receiver.StopAsync();
store.Dispose();
TryDelete(databasePath);

return allSucceeded && processedKeys.Count == ids.Count ? 0 : 1;

static void TryDelete(string path)
{
    foreach (var file in new[] { path, path + "-wal", path + "-shm" })
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
