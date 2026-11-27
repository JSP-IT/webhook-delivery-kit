using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JspIt.WebhookDelivery.Tests.Support;

/// <summary>A request captured by <see cref="MockReceiver"/>.</summary>
public sealed record ReceivedRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>How <see cref="MockReceiver"/> answers a request.</summary>
public sealed record ReceiverResponse(int Status = 200, TimeSpan Delay = default, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static ReceiverResponse Ok { get; } = new();
}

/// <summary>A real HTTP endpoint on 127.0.0.1 with a scriptable response, for integration tests.</summary>
public sealed class MockReceiver : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _count;

    private MockReceiver(WebApplication app, Uri baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public Uri BaseUrl { get; }

    public Uri Url => new(BaseUrl, "/hooks/secret-path-token?token=query-secret");

    public ConcurrentQueue<ReceivedRequest> Requests { get; } = new();

    /// <summary>Response selector: (request, zero-based request index) → response.</summary>
    public Func<ReceivedRequest, int, ReceiverResponse> Respond { get; set; } = (_, _) => ReceiverResponse.Ok;

    /// <summary>Signalled every time a request arrives (before the response delay).</summary>
    public SemaphoreSlim Arrived { get; } = new(0);

    public static async Task<MockReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        MockReceiver? receiver = null;
        app.Run(async context => await receiver!.HandleAsync(context));
        await app.StartAsync();
        receiver = new MockReceiver(app, new Uri(app.Urls.First()));
        return receiver;
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var request = new ReceivedRequest(context.Request.Method, context.Request.Path + context.Request.QueryString, headers, buffer.ToArray());
        var index = Interlocked.Increment(ref _count) - 1;
        Requests.Enqueue(request);
        Arrived.Release();

        var response = Respond(request, index);
        if (response.Delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(response.Delay, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        context.Response.StatusCode = response.Status;
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
        {
            context.Response.Headers[name] = value;
        }

        await context.Response.WriteAsync("response body that must not be stored", context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
