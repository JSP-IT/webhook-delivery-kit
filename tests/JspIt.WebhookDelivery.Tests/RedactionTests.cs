namespace JspIt.WebhookDelivery.Tests;

public class RedactionTests
{
    private static readonly Uri Url = new("https://hooks.example.com:8443/services/T000/B000/XXXXSECRET?token=abc123");

    [Fact]
    public void Endpoint_keeps_only_scheme_host_and_port()
    {
        Assert.Equal("https://hooks.example.com:8443", Redaction.Endpoint(Url));
        Assert.Equal("https://hooks.example.com", Redaction.Endpoint(new Uri("https://user:pw@hooks.example.com/a?b=c")));
        Assert.Equal("http://[::1]:8080", Redaction.Endpoint(new Uri("http://[::1]:8080/x")));
    }

    [Fact]
    public void Error_messages_lose_url_path_query_and_control_characters()
    {
        var message = $"Request to {Url} failed\r\nfake-log-line: path {Url.PathAndQuery}";
        var redacted = Redaction.ErrorMessage(message, Url);

        Assert.DoesNotContain("XXXXSECRET", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Error_messages_are_truncated() =>
        Assert.Equal(Redaction.MaxErrorLength, Redaction.ErrorMessage(new string('x', 5000), null).Length);

    [Fact]
    public void Destination_and_message_ToString_do_not_leak_url_path_or_payload()
    {
        var destination = new WebhookDestination { Id = "d", Url = Url, SigningKeys = [Support.TestSetup.KeyA] };
        Assert.DoesNotContain("XXXXSECRET", destination.ToString(), StringComparison.Ordinal);

        var message = WebhookMessage.FromJson("d", "e", """{"card":"4111111111111111"}""", "k");
        Assert.DoesNotContain("4111", message.ToString(), StringComparison.Ordinal);
    }
}
