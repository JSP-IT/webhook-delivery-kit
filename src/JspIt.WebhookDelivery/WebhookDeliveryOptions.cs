namespace JspIt.WebhookDelivery;

/// <summary>Configuration for <see cref="WebhookDeliveryService"/>.</summary>
public sealed class WebhookDeliveryOptions
{
    /// <summary>
    /// The only endpoints messages can be delivered to. Destination ids must be unique. Destinations are
    /// resolved by id at send time, so retries use the current configuration (URL, keys, policy).
    /// </summary>
    public IList<WebhookDestination> Destinations { get; } = new List<WebhookDestination>();

    /// <summary>Maximum deliveries leased per poll (1–500). Default 32.</summary>
    public int BatchSize { get; set; } = 32;

    /// <summary>Maximum concurrent attempts per service instance (1–256). Default 8.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>Delay between polls when there is no due work (50 ms – 1 min). Default 1 s. Enqueueing wakes the dispatcher early.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a leased delivery is reserved for this instance (≥ longest destination timeout + 5 s, ≤ 1 h).
    /// If the process crashes mid-attempt, the delivery becomes eligible again once the lease expires,
    /// which can produce a duplicate delivery. Default 2 minutes.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Lease owner id for this instance. Default: machine name, process id and a random suffix.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}";

    internal void Validate()
    {
        Validation.Require(BatchSize is >= 1 and <= 500, "BatchSize must be between 1 and 500.");
        Validation.Require(MaxConcurrency is >= 1 and <= 256, "MaxConcurrency must be between 1 and 256.");
        Validation.Require(PollInterval >= TimeSpan.FromMilliseconds(50) && PollInterval <= TimeSpan.FromMinutes(1), "PollInterval must be between 50 ms and 1 minute.");
        Validation.Require(!string.IsNullOrWhiteSpace(InstanceId) && InstanceId.Length <= 200 && Validation.IsValidHeaderValue(InstanceId), "InstanceId must be 1-200 printable characters.");
        Validation.Require(Destinations.Count > 0, "At least one destination must be configured.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var maxTimeout = TimeSpan.Zero;
        foreach (var destination in Destinations)
        {
            Validation.Require(destination is not null, "Destinations must not contain null.");
            destination!.Validate();
            Validation.Require(ids.Add(destination.Id), $"Duplicate destination id '{destination.Id}'.");
            if (destination.Timeout > maxTimeout)
            {
                maxTimeout = destination.Timeout;
            }
        }

        Validation.Require(LeaseDuration >= maxTimeout + TimeSpan.FromSeconds(5) && LeaseDuration <= TimeSpan.FromHours(1),
            $"LeaseDuration must be at least the longest destination timeout plus 5 s ({maxTimeout + TimeSpan.FromSeconds(5)}) and at most 1 hour.");
    }
}
