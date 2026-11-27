namespace JspIt.WebhookDelivery.Tests;

public class RetryPolicyTests
{
    private static readonly RetryPolicy NoJitter = new()
    {
        InitialDelay = TimeSpan.FromSeconds(10),
        BackoffMultiplier = 2,
        MaxDelay = TimeSpan.FromMinutes(1),
        UseJitter = false,
    };

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 40)]
    [InlineData(4, 60)] // capped
    [InlineData(40, 60)]
    [InlineData(int.MaxValue, 60)]
    public void Exponential_backoff_is_capped_at_MaxDelay(int completedAttempts, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), NoJitter.GetDelay(completedAttempts, 0.5));

    [Theory]
    [InlineData(0.0, 20)]
    [InlineData(0.5, 30)]
    [InlineData(1.0, 40)]
    public void Equal_jitter_stays_between_half_and_full_delay(double sample, int expectedSeconds)
    {
        var policy = NoJitter with { UseJitter = true };
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.GetDelay(3, sample));
    }

    [Fact]
    public void Retry_after_extends_delay_but_never_beyond_MaxDelay()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), NoJitter.GetDelay(1, 0, TimeSpan.FromSeconds(45)));
        Assert.Equal(TimeSpan.FromMinutes(1), NoJitter.GetDelay(1, 0, TimeSpan.FromHours(5)));
        Assert.Equal(TimeSpan.FromSeconds(10), NoJitter.GetDelay(1, 0, TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(10), (NoJitter with { RespectRetryAfter = false }).GetDelay(1, 0, TimeSpan.FromSeconds(45)));
    }

    public static TheoryData<RetryPolicy> InvalidPolicies() =>
    [
        new RetryPolicy { MaxAttempts = 0 },
        new RetryPolicy { MaxAttempts = RetryPolicy.MaxAttemptsLimit + 1 },
        new RetryPolicy { InitialDelay = TimeSpan.FromMilliseconds(10) },
        new RetryPolicy { BackoffMultiplier = 0.5 },
        new RetryPolicy { BackoffMultiplier = double.NaN },
        new RetryPolicy { BackoffMultiplier = 11 },
        new RetryPolicy { InitialDelay = TimeSpan.FromMinutes(5), MaxDelay = TimeSpan.FromMinutes(1) },
        new RetryPolicy { MaxDelay = TimeSpan.FromDays(2) },
        new RetryPolicy { MaxDeliveryAge = TimeSpan.FromDays(30) },
        new RetryPolicy { MaxDeliveryAge = TimeSpan.FromSeconds(5) },
    ];

    [Theory]
    [MemberData(nameof(InvalidPolicies))]
    public void Out_of_bounds_policies_are_rejected(RetryPolicy policy) =>
        Assert.Throws<WebhookConfigurationException>(policy.Validate);

    [Fact]
    public void Default_policy_is_valid_and_bounded()
    {
        RetryPolicy.Default.Validate();
        RetryPolicy.NoRetry.Validate();
        Assert.Equal(8, RetryPolicy.Default.MaxAttempts);
    }
}
