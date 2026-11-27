namespace JspIt.WebhookDelivery;

/// <summary>
/// Bounded exponential-backoff retry policy for a destination.
/// </summary>
/// <remarks>
/// The delay before attempt <c>n + 1</c> is <c>InitialDelay * BackoffMultiplier^(n - 1)</c>, capped at
/// <see cref="MaxDelay"/>. With <see cref="UseJitter"/> the delay is randomised in <c>[delay/2, delay]</c>
/// ("equal jitter") so that receivers recovering from an outage are not hit by synchronised retries.
/// A delivery stops being retried when it has used <see cref="MaxAttempts"/> attempts or when it is
/// older than <see cref="MaxDeliveryAge"/>, whichever comes first. Every value has a hard upper bound
/// so a misconfiguration cannot create unbounded retry traffic.
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>Hard upper bound for <see cref="MaxAttempts"/>.</summary>
    public const int MaxAttemptsLimit = 50;

    /// <summary>Hard upper bound for <see cref="MaxDelay"/>.</summary>
    public static readonly TimeSpan MaxDelayLimit = TimeSpan.FromHours(24);

    /// <summary>Hard upper bound for <see cref="MaxDeliveryAge"/>.</summary>
    public static readonly TimeSpan MaxDeliveryAgeLimit = TimeSpan.FromDays(7);

    /// <summary>Lower bound for <see cref="InitialDelay"/>.</summary>
    public static readonly TimeSpan MinInitialDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>The default policy: 8 attempts, 10 s initial delay, doubling, capped at 1 h, 24 h max age, jitter on.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>A policy that makes exactly one attempt and never retries.</summary>
    public static RetryPolicy NoRetry { get; } = new() { MaxAttempts = 1 };

    /// <summary>Total attempts including the first one (1–50). Default 8.</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>Delay before the first retry (100 ms – 1 h). Default 10 s.</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Multiplier applied per retry (1.0–10.0). Default 2.0.</summary>
    public double BackoffMultiplier { get; init; } = 2.0;

    /// <summary>Upper bound for a single delay (≥ <see cref="InitialDelay"/>, ≤ 24 h). Default 1 h.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Randomise delays in <c>[delay/2, delay]</c>. Default true.</summary>
    public bool UseJitter { get; init; } = true;

    /// <summary>
    /// Honour a <c>Retry-After</c> response header on 429/503 responses, never waiting longer than
    /// <see cref="MaxDelay"/>. Default true.
    /// </summary>
    public bool RespectRetryAfter { get; init; } = true;

    /// <summary>
    /// Deliveries older than this are failed instead of retried (1 min – 7 days). Default 24 h.
    /// </summary>
    public TimeSpan MaxDeliveryAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Validates the policy, throwing <see cref="WebhookConfigurationException"/> when out of bounds.</summary>
    public void Validate()
    {
        Validation.Require(MaxAttempts is >= 1 and <= MaxAttemptsLimit, $"RetryPolicy.MaxAttempts must be between 1 and {MaxAttemptsLimit}.");
        Validation.Require(InitialDelay >= MinInitialDelay && InitialDelay <= TimeSpan.FromHours(1), "RetryPolicy.InitialDelay must be between 100 ms and 1 hour.");
        Validation.Require(BackoffMultiplier is >= 1.0 and <= 10.0 && double.IsFinite(BackoffMultiplier), "RetryPolicy.BackoffMultiplier must be between 1.0 and 10.0.");
        Validation.Require(MaxDelay >= InitialDelay && MaxDelay <= MaxDelayLimit, "RetryPolicy.MaxDelay must be at least InitialDelay and at most 24 hours.");
        Validation.Require(MaxDeliveryAge >= TimeSpan.FromMinutes(1) && MaxDeliveryAge <= MaxDeliveryAgeLimit, "RetryPolicy.MaxDeliveryAge must be between 1 minute and 7 days.");
    }

    /// <summary>
    /// Computes the delay to wait after <paramref name="completedAttempts"/> failed attempts.
    /// </summary>
    /// <param name="completedAttempts">Number of attempts made so far (≥ 1).</param>
    /// <param name="jitterSample">A value in [0, 1) used when <see cref="UseJitter"/> is on.</param>
    /// <param name="retryAfter">Optional server-requested delay.</param>
    public TimeSpan GetDelay(int completedAttempts, double jitterSample, TimeSpan? retryAfter = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempts, 1);

        var exponent = Math.Min(completedAttempts - 1, 64);
        var raw = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, exponent);
        var cappedMs = double.IsFinite(raw) ? Math.Min(raw, MaxDelay.TotalMilliseconds) : MaxDelay.TotalMilliseconds;

        if (UseJitter)
        {
            var sample = Math.Clamp(jitterSample, 0.0, 1.0);
            cappedMs = (cappedMs / 2) + (sample * cappedMs / 2);
        }

        var delay = TimeSpan.FromMilliseconds(cappedMs);

        if (RespectRetryAfter && retryAfter is { } ra && ra > delay)
        {
            delay = ra < MaxDelay ? ra : MaxDelay;
        }

        return delay;
    }
}
