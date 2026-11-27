using System.Security.Cryptography;
using System.Text;
using JspIt.WebhookDelivery.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace JspIt.WebhookDelivery.Tests;

public class SignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"evt_1"}""");
    private readonly FakeTimeProvider _clock = TestSetup.NewClock();

    private Dictionary<string, string> SignedHeaders(params WebhookSigningKey[] keys)
    {
        var signature = WebhookSigner.Sign(WebhookSignatureScheme.TimestampedHmacSha256V1, keys, _clock.GetUtcNow(), "evt_1", "invoice.paid", Body);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WebhookHeaders.Timestamp] = signature.Timestamp!,
            [WebhookHeaders.Signature] = signature.HeaderValue,
            [WebhookHeaders.IdempotencyKey] = "evt_1",
            [WebhookHeaders.Event] = "invoice.paid",
        };
    }

    private WebhookVerificationStatus Verify(Dictionary<string, string> headers, byte[] body, params WebhookSigningKey[] keys) =>
        new WebhookSignatureVerifier(keys, timeProvider: _clock).Verify(h => headers.GetValueOrDefault(h), body);

    [Fact]
    public void Signature_matches_documented_algorithm()
    {
        // Independent re-implementation of the format documented in docs/security.md.
        var headers = SignedHeaders(TestSetup.KeyA);
        var secret = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var input = Encoding.UTF8.GetBytes($"v1\n{headers[WebhookHeaders.Timestamp]}\nevt_1\ninvoice.paid\n").Concat(Body).ToArray();
        var expected = "v1=" + Convert.ToHexStringLower(HMACSHA256.HashData(secret, input));

        Assert.Equal(expected, headers[WebhookHeaders.Signature]);
        Assert.Equal(TestSetup.Epoch.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), headers[WebhookHeaders.Timestamp]);
    }

    [Fact]
    public void Valid_signature_verifies() =>
        Assert.Equal(WebhookVerificationStatus.Valid, Verify(SignedHeaders(TestSetup.KeyA), Body, TestSetup.KeyA));

    [Fact]
    public void Tampered_body_is_rejected()
    {
        var tampered = Encoding.UTF8.GetBytes("""{"id":"evt_2"}""");
        Assert.Equal(WebhookVerificationStatus.SignatureMismatch, Verify(SignedHeaders(TestSetup.KeyA), tampered, TestSetup.KeyA));
    }

    [Theory]
    [InlineData(WebhookHeaders.IdempotencyKey, "evt_2")]
    [InlineData(WebhookHeaders.Event, "invoice.refunded")]
    public void Tampered_signed_headers_are_rejected(string header, string value)
    {
        var headers = SignedHeaders(TestSetup.KeyA);
        headers[header] = value;
        Assert.Equal(WebhookVerificationStatus.SignatureMismatch, Verify(headers, Body, TestSetup.KeyA));
    }

    [Fact]
    public void Wrong_key_is_rejected() =>
        Assert.Equal(WebhookVerificationStatus.SignatureMismatch, Verify(SignedHeaders(TestSetup.KeyA), Body, TestSetup.KeyB));

    [Fact]
    public void Old_timestamp_is_rejected_as_possible_replay()
    {
        var headers = SignedHeaders(TestSetup.KeyA);
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(WebhookVerificationStatus.TimestampOutOfTolerance, Verify(headers, Body, TestSetup.KeyA));
    }

    [Fact]
    public void Missing_and_malformed_headers_are_reported()
    {
        var headers = SignedHeaders(TestSetup.KeyA);
        headers.Remove(WebhookHeaders.Signature);
        Assert.Equal(WebhookVerificationStatus.MissingHeader, Verify(headers, Body, TestSetup.KeyA));

        headers = SignedHeaders(TestSetup.KeyA);
        headers[WebhookHeaders.Timestamp] = "-12";
        Assert.Equal(WebhookVerificationStatus.MalformedHeader, Verify(headers, Body, TestSetup.KeyA));

        headers = SignedHeaders(TestSetup.KeyA);
        headers[WebhookHeaders.Signature] = "v0=abc";
        Assert.Equal(WebhookVerificationStatus.MalformedHeader, Verify(headers, Body, TestSetup.KeyA));
    }

    [Fact]
    public void Rotation_signs_with_all_active_keys_so_old_and_new_receivers_both_verify()
    {
        var headers = SignedHeaders(TestSetup.KeyA, TestSetup.KeyB);
        Assert.Equal(2, headers[WebhookHeaders.Signature].Split(", ").Length);
        Assert.Equal(WebhookVerificationStatus.Valid, Verify(headers, Body, TestSetup.KeyA));
        Assert.Equal(WebhookVerificationStatus.Valid, Verify(headers, Body, TestSetup.KeyB));
    }

    [Fact]
    public void Keys_outside_their_validity_window_do_not_sign()
    {
        var expired = new WebhookSigningKey("old", new byte[32], notAfter: TestSetup.Epoch);
        var future = new WebhookSigningKey("new", new byte[32], notBefore: TestSetup.Epoch.AddDays(1));

        var headers = SignedHeaders(expired, TestSetup.KeyA, future);
        Assert.Single(headers[WebhookHeaders.Signature].Split(", "));
        Assert.Throws<InvalidOperationException>(() => SignedHeaders(expired, future));
    }

    [Fact]
    public void Body_only_compatibility_scheme_matches_plain_hmac_of_body()
    {
        var key = WebhookSigningKey.FromUtf8("legacy", "your-webhook-secret-at-least-16-chars");
        var signature = WebhookSigner.Sign(WebhookSignatureScheme.BodyHmacSha256, [key], _clock.GetUtcNow(), "evt_1", "invoice.paid", Body);

        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("your-webhook-secret-at-least-16-chars"), Body));
        Assert.Equal(WebhookHeaders.LegacySignature, signature.HeaderName);
        Assert.Equal(expected, signature.HeaderValue);
        Assert.Null(signature.Timestamp);

        var verifier = new WebhookSignatureVerifier([key], new WebhookSignatureVerifierOptions { Scheme = WebhookSignatureScheme.BodyHmacSha256 });
        Assert.Equal(WebhookVerificationStatus.Valid, verifier.Verify(h => h == WebhookHeaders.LegacySignature ? signature.HeaderValue : null, Body));
        Assert.Equal(WebhookVerificationStatus.SignatureMismatch, verifier.Verify(h => h == WebhookHeaders.LegacySignature ? signature.HeaderValue : null, [1, 2, 3]));
    }

    [Fact]
    public void Short_or_invalid_keys_are_rejected()
    {
        Assert.Throws<WebhookConfigurationException>(() => new WebhookSigningKey("k", new byte[15]));
        Assert.Throws<WebhookConfigurationException>(() => new WebhookSigningKey("bad id", new byte[32]));
        Assert.Throws<WebhookConfigurationException>(() => WebhookSigningKey.FromBase64("k", "not base64!"));
        Assert.Throws<WebhookConfigurationException>(() => new WebhookSigningKey("k", new byte[32], TestSetup.Epoch, TestSetup.Epoch));
    }

    [Fact]
    public void Key_ToString_never_reveals_the_secret()
    {
        var key = WebhookSigningKey.FromUtf8("k1", "super-secret-value-1234");
        Assert.DoesNotContain("super-secret", key.ToString(), StringComparison.Ordinal);
        Assert.Contains("redacted", key.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_keys_are_random_and_recommended_length()
    {
        var a = WebhookSigningKey.Generate("a");
        var b = WebhookSigningKey.Generate("b");
        Assert.Equal(WebhookSigningKey.RecommendedSecretBytes, Convert.FromBase64String(a.ExportBase64Secret()).Length);
        Assert.NotEqual(a.ExportBase64Secret(), b.ExportBase64Secret());
    }
}
