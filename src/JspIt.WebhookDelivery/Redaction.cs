using System.Text;

namespace JspIt.WebhookDelivery;

/// <summary>
/// Helpers that keep secrets out of logs and persisted error text. The library never logs payloads,
/// header values, signatures or secrets, and only logs a destination's scheme, host and port: webhook
/// URLs frequently embed credentials in their path or query (e.g. <c>/hooks/T000/B000/XXXX</c>).
/// </summary>
public static class Redaction
{
    /// <summary>Maximum length of error text stored in delivery records.</summary>
    public const int MaxErrorLength = 500;

    /// <summary>Returns <c>scheme://host[:port]</c> without user info, path, query or fragment.</summary>
    public static string Endpoint(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri)
        {
            return "[invalid-url]";
        }

        // Authority is host[:port] and never includes user info; IPv6 literals keep their brackets.
        return $"{url.Scheme}://{url.Authority}";
    }

    /// <summary>
    /// Makes an error message safe to persist and log: removes occurrences of the destination URL, its
    /// path and query, strips control characters (log injection) and truncates.
    /// </summary>
    public static string ErrorMessage(string? message, Uri? destinationUrl)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var text = message;
        if (destinationUrl is { IsAbsoluteUri: true })
        {
            foreach (var sensitive in new[] { destinationUrl.OriginalString, destinationUrl.AbsoluteUri, destinationUrl.PathAndQuery, destinationUrl.Query })
            {
                if (sensitive.Length > 1)
                {
                    text = text.Replace(sensitive, "[redacted]", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxErrorLength));
        foreach (var c in text)
        {
            if (builder.Length >= MaxErrorLength)
            {
                break;
            }

            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        return builder.ToString();
    }
}
