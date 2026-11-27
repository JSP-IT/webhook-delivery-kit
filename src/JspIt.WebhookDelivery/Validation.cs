namespace JspIt.WebhookDelivery;

internal static class Validation
{
    public const int MaxIdempotencyKeyLength = 200;
    public const int MaxEventTypeLength = 128;

    /// <summary>[A-Za-z0-9._-], 1..maxLength.</summary>
    public static bool IsValidIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Visible ASCII (0x21-0x7E), 1..200 characters. No whitespace or control characters, so the key
    /// is header-safe and cannot be used for header or log injection, and the newline-delimited
    /// signature input stays unambiguous.
    /// </summary>
    public static bool IsValidIdempotencyKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxIdempotencyKeyLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is < '\x21' or > '\x7E')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Starts with a letter or digit; then [A-Za-z0-9._:/-]; 1..128 characters.</summary>
    public static bool IsValidEventType(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxEventTypeLength || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>RFC 7230 token characters, used for header names.</summary>
    public static bool IsValidHeaderName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Visible ASCII and spaces/tabs only (no CR/LF), bounded length.</summary>
    public static bool IsValidHeaderValue(string? value)
    {
        if (value is null || value.Length > 4096)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!(c is '\t' or (>= '\x20' and <= '\x7E')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A media type such as <c>application/json</c> or <c>application/cloudevents+json; charset=utf-8</c>.</summary>
    public static bool IsValidContentType(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 200
        && IsValidHeaderValue(value)
        && System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(value, out _);

    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new WebhookConfigurationException(message);
        }
    }
}
