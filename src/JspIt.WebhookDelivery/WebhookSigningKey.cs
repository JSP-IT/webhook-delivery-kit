using System.Security.Cryptography;
using System.Text;

namespace JspIt.WebhookDelivery;

/// <summary>
/// A shared HMAC-SHA256 secret used to sign (sender) or verify (receiver) webhook requests.
/// </summary>
/// <remarks>
/// <para>
/// Keys are supplied through configuration and are never written to the delivery store: requests
/// are signed at send time using the keys configured <em>at that moment</em>, so rotating a key also
/// applies to retries of deliveries that were enqueued before the rotation.
/// </para>
/// <para>
/// To rotate without downtime, configure the new key alongside the old one (both are used to sign),
/// update receivers to accept the new key, then remove the old key. <see cref="NotBefore"/> and
/// <see cref="NotAfter"/> can schedule that window in advance. See <c>docs/security.md</c>.
/// </para>
/// <para><see cref="ToString"/> never includes secret material.</para>
/// </remarks>
public sealed class WebhookSigningKey
{
    /// <summary>Minimum accepted secret length in bytes (128 bits).</summary>
    public const int MinimumSecretBytes = 16;

    /// <summary>Recommended secret length in bytes (256 bits).</summary>
    public const int RecommendedSecretBytes = 32;

    /// <summary>Maximum accepted secret length in bytes.</summary>
    public const int MaximumSecretBytes = 512;

    private readonly byte[] _secret;

    /// <summary>Creates a signing key from raw secret bytes. The bytes are copied.</summary>
    /// <param name="keyId">Non-secret identifier for the key, used in logs and diagnostics (1–64 chars of [A-Za-z0-9._-]).</param>
    /// <param name="secret">Secret bytes, at least <see cref="MinimumSecretBytes"/> long.</param>
    /// <param name="notBefore">Optional instant before which the key is not used.</param>
    /// <param name="notAfter">Optional instant after which the key is no longer used.</param>
    public WebhookSigningKey(string keyId, ReadOnlySpan<byte> secret, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        if (!Validation.IsValidIdentifier(keyId, 64))
        {
            throw new WebhookConfigurationException("Signing key id must be 1-64 characters of [A-Za-z0-9._-].");
        }

        if (secret.Length < MinimumSecretBytes)
        {
            throw new WebhookConfigurationException($"Signing key '{keyId}' is too short: secrets must be at least {MinimumSecretBytes} bytes ({RecommendedSecretBytes} recommended).");
        }

        if (secret.Length > MaximumSecretBytes)
        {
            throw new WebhookConfigurationException($"Signing key '{keyId}' is too long: secrets must be at most {MaximumSecretBytes} bytes.");
        }

        if (notBefore is { } nb && notAfter is { } na && na <= nb)
        {
            throw new WebhookConfigurationException($"Signing key '{keyId}' has NotAfter earlier than or equal to NotBefore.");
        }

        KeyId = keyId;
        _secret = secret.ToArray();
        NotBefore = notBefore;
        NotAfter = notAfter;
    }

    /// <summary>Non-secret key identifier.</summary>
    public string KeyId { get; }

    /// <summary>The key is not used before this instant, if set.</summary>
    public DateTimeOffset? NotBefore { get; }

    /// <summary>The key is not used after this instant, if set.</summary>
    public DateTimeOffset? NotAfter { get; }

    internal ReadOnlySpan<byte> Secret => _secret;

    /// <summary>Creates a key from a Base64-encoded secret.</summary>
    public static WebhookSigningKey FromBase64(string keyId, string base64Secret, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        ArgumentNullException.ThrowIfNull(base64Secret);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Secret);
        }
        catch (FormatException ex)
        {
            throw new WebhookConfigurationException($"Signing key '{keyId}' is not valid Base64.", ex);
        }

        try
        {
            return new WebhookSigningKey(keyId, bytes, notBefore, notAfter);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// Creates a key from a text secret, using its UTF-8 bytes. Use this for interoperability with
    /// receivers that treat the secret as a string (for example <c>hmac.new(secret.encode(), ...)</c>).
    /// </summary>
    public static WebhookSigningKey FromUtf8(string keyId, string secret, DateTimeOffset? notBefore = null, DateTimeOffset? notAfter = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            return new WebhookSigningKey(keyId, bytes, notBefore, notAfter);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Generates a new random key with <see cref="RecommendedSecretBytes"/> of entropy.</summary>
    public static WebhookSigningKey Generate(string keyId)
    {
        Span<byte> bytes = stackalloc byte[RecommendedSecretBytes];
        RandomNumberGenerator.Fill(bytes);
        try
        {
            return new WebhookSigningKey(keyId, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Returns the secret as Base64 so it can be handed to a receiver out of band. Treat the result as a secret.</summary>
    public string ExportBase64Secret() => Convert.ToBase64String(_secret);

    /// <summary>Whether the key's validity window includes <paramref name="instant"/>.</summary>
    public bool IsActiveAt(DateTimeOffset instant) =>
        (NotBefore is null || instant >= NotBefore) && (NotAfter is null || instant < NotAfter);

    /// <inheritdoc />
    public override string ToString() => $"WebhookSigningKey({KeyId}, secret=[redacted])";
}
