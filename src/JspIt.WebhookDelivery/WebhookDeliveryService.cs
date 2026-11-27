using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using JspIt.WebhookDelivery.Http;
using JspIt.WebhookDelivery.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JspIt.WebhookDelivery;

/// <summary>
/// Enqueues webhook messages into an <see cref="IDeliveryStore"/> and dispatches them to configured
/// destinations with signing, bounded retries and cancellation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delivery is at-least-once, never exactly-once.</b> A receiver may see the same message more than once,
/// for example when its 2xx response is lost, when it responds after the attempt timed out, or when this
/// process crashes after sending but before recording the outcome. Every attempt carries the same
/// <see cref="WebhookHeaders.IdempotencyKey"/>; receivers must use it to de-duplicate.
/// A message can also ultimately fail (retries or age exhausted, non-retryable response, cancellation).
/// </para>
/// <para>
/// One instance can both enqueue and dispatch. Several instances (threads or processes) can share one store;
/// leases ensure a delivery is attempted by one instance at a time, and expired leases are reclaimed.
/// </para>
/// </remarks>
public sealed partial class WebhookDeliveryService : IAsyncDisposable
{
    private static readonly string UserAgent = BuildUserAgent();
    private static readonly TimeSpan StoreWriteTimeout = TimeSpan.FromSeconds(15);

    private readonly IDeliveryStore _store;
    private readonly WebhookDeliveryOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DestinationEntry> _destinations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, Task> _activeBatches = new();
    private readonly ConcurrentBag<HttpMessageInvoker> _retiredInvokers = [];
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _destinationGate = new();
    private long _batchSequence;
    private int _running;
    private int _disposed;

    /// <summary>Creates a service. Configuration is validated immediately.</summary>
    /// <param name="store">Persistence implementation.</param>
    /// <param name="options">Destinations and dispatcher options. The destination list is copied.</param>
    /// <param name="loggerFactory">Optional logger factory. Logs never contain payloads, secrets, header values or URL paths.</param>
    /// <param name="timeProvider">Optional clock (scheduling and signing timestamps). Network timeouts always use real time.</param>
    /// <exception cref="WebhookConfigurationException">The configuration is invalid.</exception>
    public WebhookDeliveryService(IDeliveryStore store, WebhookDeliveryOptions options, ILoggerFactory? loggerFactory = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _store = store;
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger("JspIt.WebhookDelivery");

        foreach (var destination in options.Destinations)
        {
            _destinations[destination.Id] = new DestinationEntry(destination, DestinationHttpClientFactory.Create(destination, options.MaxConcurrency));
        }
    }

    /// <summary>Configured destination ids.</summary>
    public IReadOnlyCollection<string> DestinationIds => _destinations.Keys.ToArray();

    internal Func<double> JitterSource { get; set; } = Random.Shared.NextDouble;

    /// <summary>
    /// Adds or replaces a destination at runtime, e.g. to rotate signing keys without a restart. Pending
    /// deliveries for the destination use the new configuration on their next attempt.
    /// </summary>
    /// <exception cref="WebhookConfigurationException">The destination is invalid.</exception>
    public void UpsertDestination(WebhookDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfDisposed();
        destination.Validate();
        Validation.Require(_options.LeaseDuration >= destination.Timeout + TimeSpan.FromSeconds(5),
            $"Destination '{destination.Id}': Timeout is too long for the configured LeaseDuration.");

        lock (_destinationGate)
        {
            var entry = new DestinationEntry(destination, DestinationHttpClientFactory.Create(destination, _options.MaxConcurrency));
            if (_destinations.TryGetValue(destination.Id, out var previous))
            {
                // In-flight attempts may still use the old invoker; dispose it when the service is disposed.
                _retiredInvokers.Add(previous.Invoker);
            }

            _destinations[destination.Id] = entry;
        }
    }

    /// <summary>
    /// Persists a message for delivery. Idempotent per destination and idempotency key: enqueueing the same
    /// key again returns the existing delivery with <see cref="EnqueueResult.Created"/> = false.
    /// </summary>
    /// <exception cref="UnknownDestinationException">The destination is not configured.</exception>
    /// <exception cref="IdempotencyConflictException">The key exists with a different event type, content type or payload.</exception>
    /// <exception cref="ArgumentException">The message is invalid (event type, key, content type or payload size).</exception>
    public async Task<EnqueueResult> EnqueueAsync(WebhookMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        if (message.DestinationId is null || !_destinations.TryGetValue(message.DestinationId, out var entry))
        {
            throw new UnknownDestinationException(message.DestinationId ?? "(null)");
        }

        if (!Validation.IsValidEventType(message.EventType))
        {
            throw new ArgumentException("EventType must start with a letter or digit and contain only letters, digits and . _ : / - (max 128).", nameof(message));
        }

        if (!Validation.IsValidIdempotencyKey(message.IdempotencyKey))
        {
            throw new ArgumentException($"IdempotencyKey must be 1-{Validation.MaxIdempotencyKeyLength} visible ASCII characters.", nameof(message));
        }

        if (!Validation.IsValidContentType(message.ContentType))
        {
            throw new ArgumentException("ContentType is not a valid media type.", nameof(message));
        }

        if (message.Payload.Length > entry.Destination.MaxPayloadBytes)
        {
            throw new ArgumentException($"Payload is {message.Payload.Length} bytes; destination '{entry.Destination.Id}' allows at most {entry.Destination.MaxPayloadBytes}.", nameof(message));
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(message.Payload.Span));
        var now = _time.GetUtcNow();
        var result = await _store.EnqueueAsync(new NewDelivery
        {
            Id = "dlv_" + Guid.CreateVersion7(now).ToString("N"),
            DestinationId = entry.Destination.Id,
            IdempotencyKey = message.IdempotencyKey,
            EventType = message.EventType,
            ContentType = message.ContentType,
            Payload = message.Payload,
            PayloadSha256 = hash,
            CreatedAt = now,
            ExpiresAt = now + entry.Destination.RetryPolicy.MaxDeliveryAge,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Created)
        {
            var existing = result.Record;
            if (existing.PayloadSha256 != hash || existing.EventType != message.EventType || existing.ContentType != message.ContentType)
            {
                throw new IdempotencyConflictException(existing.DestinationId, existing.IdempotencyKey, existing.Id);
            }

            LogDuplicateEnqueue(existing.Id, existing.DestinationId);
            return result;
        }

        LogEnqueued(result.Record.Id, result.Record.DestinationId, result.Record.EventType);
        Wake();
        return result;
    }

    /// <summary>Gets a delivery by id.</summary>
    public Task<DeliveryRecord?> GetAsync(string deliveryId, CancellationToken cancellationToken = default) =>
        _store.GetAsync(deliveryId, cancellationToken);

    /// <summary>Gets a delivery by destination and idempotency key.</summary>
    public Task<DeliveryRecord?> FindAsync(string destinationId, string idempotencyKey, CancellationToken cancellationToken = default) =>
        _store.FindByIdempotencyKeyAsync(destinationId, idempotencyKey, cancellationToken);

    /// <summary>Gets the attempt history of a delivery.</summary>
    public Task<IReadOnlyList<DeliveryAttempt>> GetAttemptsAsync(string deliveryId, CancellationToken cancellationToken = default) =>
        _store.GetAttemptsAsync(deliveryId, cancellationToken);

    /// <summary>
    /// Cancels a pending or in-flight delivery. If an attempt is running in this instance, its HTTP request is
    /// aborted. Cancellation cannot recall a request the receiver has already received: a receiver may still
    /// process a delivery that is reported as cancelled.
    /// </summary>
    public async Task<CancelResult> CancelAsync(string deliveryId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deliveryId);
        var result = await _store.CancelAsync(deliveryId, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (result == CancelResult.Cancelled)
        {
            if (_inFlight.TryGetValue(deliveryId, out var cts))
            {
                try
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // The attempt finished concurrently.
                }
            }

            LogCancelled(deliveryId);
        }

        return result;
    }

    /// <summary>
    /// Leases one batch of due deliveries and attempts them (up to <see cref="WebhookDeliveryOptions.MaxConcurrency"/> at once).
    /// Returns the number of deliveries processed. Use this to drive dispatch yourself; otherwise call <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Stops dispatch. Running attempts are aborted and released for another attempt.</param>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        var leased = await _store.LeaseDueAsync(
            new LeaseRequest(_time.GetUtcNow(), _options.BatchSize, _options.InstanceId, _options.LeaseDuration),
            cancellationToken).ConfigureAwait(false);

        if (leased.Count == 0)
        {
            return 0;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var batchId = Interlocked.Increment(ref _batchSequence);
        var batch = Parallel.ForEachAsync(
            leased,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency },
            async (record, _) => await ProcessOneAsync(record, stopping.Token).ConfigureAwait(false));

        _activeBatches[batchId] = batch;
        try
        {
            await batch.ConfigureAwait(false);
        }
        finally
        {
            _activeBatches.TryRemove(batchId, out _);
        }

        return leased.Count;
    }

    /// <summary>
    /// Runs the dispatch loop until <paramref name="cancellationToken"/> is cancelled or the service is disposed,
    /// then returns. Store errors are logged and retried after <see cref="WebhookDeliveryOptions.PollInterval"/>.
    /// Only one loop may run per service instance; run more instances (sharing the store) to scale out.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException("RunAsync is already running on this instance.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var processed = 0;
                try
                {
                    processed = await ProcessDueAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 1)
                {
                    break;
                }
#pragma warning disable CA1031 // The loop must survive transient store failures.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogDispatchLoopError(ex);
                }

                if (processed < _options.BatchSize)
                {
                    await WaitForWorkAsync(token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Stops dispatching, waits briefly for running attempts to release their leases, and releases HTTP resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _disposeCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_activeBatches.Values).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best-effort shutdown.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Attempts that did not finish keep their lease and are reclaimed after it expires.
        }

        foreach (var entry in _destinations.Values)
        {
            entry.Invoker.Dispose();
        }

        while (_retiredInvokers.TryTake(out var invoker))
        {
            invoker.Dispose();
        }

        // _disposeCts and _wake are intentionally not disposed: a concurrently finishing RunAsync or
        // ProcessDueAsync may still observe them, and neither holds unmanaged resources here.
    }

    private async Task ProcessOneAsync(DeliveryRecord record, CancellationToken stoppingToken)
    {
        var startedAt = _time.GetUtcNow();
        var startTimestamp = _time.GetTimestamp();
        var attempt = record.AttemptCount;

        AttemptCompletion Completion(AttemptOutcome outcome, string? category = null, string? message = null, int? status = null, DateTimeOffset? next = null) => new()
        {
            DeliveryId = record.Id,
            LeaseToken = record.LeaseToken!,
            LeaseOwner = _options.InstanceId,
            AttemptNumber = attempt,
            StartedAt = startedAt,
            CompletedAt = _time.GetUtcNow(),
            Outcome = outcome,
            ErrorCategory = category,
            ErrorMessage = message,
            StatusCode = status,
            NextAttemptAt = next,
        };

        AttemptCompletion completion;
        WebhookDestination? destination = null;

        if (!_destinations.TryGetValue(record.DestinationId, out var entry))
        {
            completion = Completion(AttemptOutcome.Failed, DeliveryErrorCategories.UnknownDestination, $"Destination '{record.DestinationId}' is no longer configured.");
        }
        else if (startedAt >= record.ExpiresAt)
        {
            destination = entry.Destination;
            completion = Completion(AttemptOutcome.Failed, DeliveryErrorCategories.Expired, "Delivery exceeded its maximum age before it could be delivered.");
        }
        else if (attempt > entry.Destination.RetryPolicy.MaxAttempts)
        {
            // Reached when earlier attempts crashed or stalled (lease expiry): they still consume the budget,
            // which bounds the damage of a payload that crashes the process.
            destination = entry.Destination;
            completion = Completion(AttemptOutcome.Failed, DeliveryErrorCategories.AttemptsExhausted, $"All {entry.Destination.RetryPolicy.MaxAttempts} attempts used (including attempts that did not complete).");
        }
        else
        {
            destination = entry.Destination;
            using var deliveryCts = new CancellationTokenSource();
            _inFlight[record.Id] = deliveryCts;
            SendResult result;
            try
            {
                result = await SendAsync(entry, record, attempt, deliveryCts.Token, stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // One failing delivery must not fault the batch; the attempt is recorded and retried.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                result = SendResult.Retryable(DeliveryErrorCategories.Unexpected, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _inFlight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(record.Id, deliveryCts));
            }

            completion = ToCompletion(result, destination, record, attempt, Completion);
        }

        bool applied;
        try
        {
            using var writeTimeout = new CancellationTokenSource(StoreWriteTimeout);
            applied = await _store.CompleteAttemptAsync(completion, writeTimeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Persisting must not crash the dispatcher; the lease will expire and the delivery is retried.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPersistFailed(ex, record.Id, record.DestinationId, attempt);
            return;
        }

        var elapsedMs = (long)_time.GetElapsedTime(startTimestamp).TotalMilliseconds;
        var endpoint = destination is null ? "-" : Redaction.Endpoint(destination.Url);

        if (!applied)
        {
            if (completion.Outcome != AttemptOutcome.Cancelled)
            {
                LogLeaseLost(record.Id, record.DestinationId, attempt);
            }

            return;
        }

        switch (completion.Outcome)
        {
            case AttemptOutcome.Succeeded:
                LogDelivered(record.Id, record.DestinationId, endpoint, attempt, completion.StatusCode ?? 0, elapsedMs);
                break;
            case AttemptOutcome.RetryScheduled:
                LogRetryScheduled(record.Id, record.DestinationId, endpoint, attempt, completion.ErrorCategory, completion.StatusCode, completion.NextAttemptAt!.Value, completion.ErrorMessage);
                break;
            case AttemptOutcome.Failed:
                LogFailed(record.Id, record.DestinationId, endpoint, attempt, completion.ErrorCategory, completion.StatusCode, completion.ErrorMessage);
                break;
            case AttemptOutcome.Cancelled:
                break;
        }
    }

    private AttemptCompletion ToCompletion(
        SendResult result,
        WebhookDestination destination,
        DeliveryRecord record,
        int attempt,
        Func<AttemptOutcome, string?, string?, int?, DateTimeOffset?, AttemptCompletion> completion)
    {
        var message = result.Message is null ? null : Redaction.ErrorMessage(result.Message, destination.Url);
        switch (result.Kind)
        {
            case SendResultKind.Success:
                return completion(AttemptOutcome.Succeeded, null, null, result.StatusCode, null);

            case SendResultKind.Permanent:
                return completion(AttemptOutcome.Failed, result.Category, message, result.StatusCode, null);

            case SendResultKind.Cancelled:
                return completion(AttemptOutcome.Cancelled, DeliveryErrorCategories.Cancelled, "Attempt aborted because the delivery was cancelled.", result.StatusCode, null);

            case SendResultKind.Stopping:
                // Release the lease immediately so another instance (or this one after restart) can retry.
                return completion(AttemptOutcome.RetryScheduled, DeliveryErrorCategories.DispatcherStopping,
                    "Dispatcher stopped during the attempt; the receiver may or may not have received it.", result.StatusCode, _time.GetUtcNow());

            case SendResultKind.Retryable:
            {
                var policy = destination.RetryPolicy;
                if (attempt >= policy.MaxAttempts)
                {
                    return completion(AttemptOutcome.Failed, result.Category, $"{message} (final attempt {attempt}/{policy.MaxAttempts})", result.StatusCode, null);
                }

                var next = _time.GetUtcNow() + policy.GetDelay(attempt, JitterSource(), result.RetryAfter);
                if (next >= record.ExpiresAt)
                {
                    return completion(AttemptOutcome.Failed, result.Category, $"{message} (next retry would exceed the maximum delivery age)", result.StatusCode, null);
                }

                return completion(AttemptOutcome.RetryScheduled, result.Category, message, result.StatusCode, next);
            }

            default:
                throw new InvalidOperationException("Unknown send result.");
        }
    }

    private async Task<SendResult> SendAsync(DestinationEntry entry, DeliveryRecord record, int attempt, CancellationToken deliveryToken, CancellationToken stoppingToken)
    {
        var destination = entry.Destination;
        WebhookSigner.SignatureHeaders signature;
        try
        {
            signature = WebhookSigner.Sign(destination.SignatureScheme, destination.SigningKeys, _time.GetUtcNow(), record.IdempotencyKey, record.EventType, record.Payload.Span);
        }
        catch (InvalidOperationException)
        {
            // Retryable: a key with a future NotBefore may become active, or configuration may be fixed at runtime.
            return SendResult.Retryable(DeliveryErrorCategories.NoActiveSigningKey, "No signing key is active for this destination.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, destination.Url)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new ReadOnlyMemoryContent(record.Payload),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(record.ContentType);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        foreach (var (name, value) in destination.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Headers.TryAddWithoutValidation(WebhookHeaders.IdempotencyKey, record.IdempotencyKey);
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Event, record.EventType);
        request.Headers.TryAddWithoutValidation(WebhookHeaders.Attempt, attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (signature.Timestamp is not null)
        {
            request.Headers.TryAddWithoutValidation(WebhookHeaders.Timestamp, signature.Timestamp);
        }

        request.Headers.TryAddWithoutValidation(signature.HeaderName, signature.HeaderValue);

        using var timeoutCts = new CancellationTokenSource(destination.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, deliveryToken, stoppingToken);

        try
        {
            using var response = await entry.Invoker.SendAsync(request, linked.Token).ConfigureAwait(false);
            // The response body is intentionally not read or stored: it may contain sensitive data and is unbounded.
            return Classify(response);
        }
        catch (OperationCanceledException) when (deliveryToken.IsCancellationRequested)
        {
            return SendResult.Cancelled();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return SendResult.Stopping();
        }
        catch (OperationCanceledException)
        {
            return SendResult.Retryable(DeliveryErrorCategories.Timeout, $"No response within {destination.Timeout.TotalMilliseconds:0} ms.");
        }
        catch (HttpRequestException ex) when (FindInner<DestinationBlockedException>(ex) is { } blocked)
        {
            return SendResult.Permanent(DeliveryErrorCategories.DestinationBlocked, blocked.Message);
        }
        catch (HttpRequestException ex)
        {
            return SendResult.Retryable(DeliveryErrorCategories.Network, DescribeNetworkError(ex));
        }
    }

    private SendResult Classify(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 200 and <= 299)
        {
            return SendResult.Success(status);
        }

        if (status is >= 300 and <= 399)
        {
            return SendResult.Permanent(DeliveryErrorCategories.RedirectNotFollowed, $"Receiver responded {status}; redirects are never followed.", status);
        }

        if (status is 408 or 425 or 429 or >= 500)
        {
            TimeSpan? retryAfter = null;
            if (status is 429 or 503 && response.Headers.RetryAfter is { } header)
            {
                retryAfter = header.Delta ?? (header.Date is { } date ? date - _time.GetUtcNow() : null);
                if (retryAfter < TimeSpan.Zero)
                {
                    retryAfter = TimeSpan.Zero;
                }
            }

            return SendResult.Retryable(DeliveryErrorCategories.HttpStatus, $"Receiver responded {status}.", status, retryAfter);
        }

        return SendResult.Permanent(DeliveryErrorCategories.HttpStatus, $"Receiver responded {status}.", status);
    }

    private static string DescribeNetworkError(HttpRequestException ex)
    {
        var detail = ex.InnerException is { } inner && inner.Message != ex.Message ? $"{ex.Message} ({inner.GetType().Name}: {inner.Message})" : ex.Message;
        return ex.HttpRequestError == HttpRequestError.Unknown ? detail : $"{ex.HttpRequestError}: {detail}";
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private async Task WaitForWorkAsync(CancellationToken token)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var delay = Task.Delay(_options.PollInterval, _time, waitCts.Token);
            var wake = _wake.WaitAsync(waitCts.Token);
            await Task.WhenAny(delay, wake).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently; the caller's loop observes cancellation next.
        }
        finally
        {
            await waitCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // Already signalled.
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently.
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    private static string BuildUserAgent()
    {
        var version = typeof(WebhookDeliveryService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return $"JspIt.WebhookDelivery/{(plus >= 0 ? version[..plus] : version)}";
    }

    private sealed record DestinationEntry(WebhookDestination Destination, HttpMessageInvoker Invoker);

    private enum SendResultKind
    {
        Success,
        Retryable,
        Permanent,
        Cancelled,
        Stopping,
    }

    private readonly record struct SendResult(SendResultKind Kind, int? StatusCode, string? Category, string? Message, TimeSpan? RetryAfter)
    {
        public static SendResult Success(int status) => new(SendResultKind.Success, status, null, null, null);

        public static SendResult Retryable(string category, string message, int? status = null, TimeSpan? retryAfter = null) =>
            new(SendResultKind.Retryable, status, category, message, retryAfter);

        public static SendResult Permanent(string category, string message, int? status = null) => new(SendResultKind.Permanent, status, category, message, null);

        public static SendResult Cancelled() => new(SendResultKind.Cancelled, null, null, null, null);

        public static SendResult Stopping() => new(SendResultKind.Stopping, null, null, null, null);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Webhook delivery {DeliveryId} enqueued for destination {DestinationId} (event {EventType}).")]
    private partial void LogEnqueued(string deliveryId, string destinationId, string eventType);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "Webhook enqueue for destination {DestinationId} matched existing delivery {DeliveryId} by idempotency key; no new delivery created.")]
    private partial void LogDuplicateEnqueue(string deliveryId, string destinationId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} to {DestinationId} ({Endpoint}) succeeded on attempt {Attempt} with status {StatusCode} in {ElapsedMs} ms.")]
    private partial void LogDelivered(string deliveryId, string destinationId, string endpoint, int attempt, int statusCode, long elapsedMs);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Webhook delivery {DeliveryId} to {DestinationId} ({Endpoint}) attempt {Attempt} failed ({ErrorCategory}, status {StatusCode}); retry scheduled at {NextAttemptAt:o}. {Error}")]
    private partial void LogRetryScheduled(string deliveryId, string destinationId, string endpoint, int attempt, string? errorCategory, int? statusCode, DateTimeOffset nextAttemptAt, string? error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error, Message = "Webhook delivery {DeliveryId} to {DestinationId} ({Endpoint}) failed permanently on attempt {Attempt} ({ErrorCategory}, status {StatusCode}). {Error}")]
    private partial void LogFailed(string deliveryId, string destinationId, string endpoint, int attempt, string? errorCategory, int? statusCode, string? error);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information, Message = "Webhook delivery {DeliveryId} cancelled.")]
    private partial void LogCancelled(string deliveryId);

    [LoggerMessage(EventId = 7, Level = LogLevel.Error, Message = "Could not record attempt {Attempt} of webhook delivery {DeliveryId} to {DestinationId}. The delivery will be retried after its lease expires and may be delivered again.")]
    private partial void LogPersistFailed(Exception exception, string deliveryId, string destinationId, int attempt);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning, Message = "Lease on webhook delivery {DeliveryId} to {DestinationId} was lost before attempt {Attempt} was recorded (cancelled or reclaimed by another instance).")]
    private partial void LogLeaseLost(string deliveryId, string destinationId, int attempt);

    [LoggerMessage(EventId = 9, Level = LogLevel.Error, Message = "Webhook dispatch loop error; retrying after the poll interval.")]
    private partial void LogDispatchLoopError(Exception exception);
}
