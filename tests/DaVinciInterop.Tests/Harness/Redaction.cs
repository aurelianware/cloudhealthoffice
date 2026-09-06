using System.Text.RegularExpressions;

namespace DaVinciInterop.Tests.Harness;

/// <summary>
/// Keeps credentials and anything credential-shaped out of interop artifacts.
///
/// Interop artifacts are meant to be reviewable and, once sanitized, publishable.
/// Nothing here is a substitute for the harness's other rule — only synthetic data
/// ever reaches an external implementation — but a redaction pass means a stray
/// token in an upstream error body never lands on disk.
/// </summary>
public static class Redaction
{
    public const string Placeholder = "[REDACTED]";

    /// <summary>Header names whose values are never written to an artifact.</summary>
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "Api-Key",
        "X-Auth-Token",
        "X-Amz-Security-Token",
        "Private-Token",
    };

    /// <summary>
    /// Credential-shaped payload patterns. Deliberately conservative: each pattern
    /// keeps the key name so the artifact still shows what was present.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] BodyPatterns =
    {
        // JSON members that carry secrets: "access_token": "…", "client_secret": "…"
        (new Regex("\"(access_token|refresh_token|id_token|client_secret|private_key|password|api_key|apiKey)\"\\s*:\\s*\"[^\"]*\"",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "\"$1\": \"" + Placeholder + "\""),

        // Bearer credentials appearing inside a body (e.g. an echoed request in an error).
        (new Regex(@"\bBearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Bearer " + Placeholder),

        // Basic credentials.
        (new Regex(@"\bBasic\s+[A-Za-z0-9+/]{8,}={0,2}", RegexOptions.IgnoreCase | RegexOptions.Compiled),
            "Basic " + Placeholder),

        // PEM private key blocks.
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled),
            Placeholder),

        // Bare JWTs (three base64url segments).
        (new Regex(@"\bey[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}\.[A-Za-z0-9\-_]{8,}", RegexOptions.Compiled),
            Placeholder),
    };

    public static bool IsSensitiveHeader(string name) => SensitiveHeaders.Contains(name);

    /// <summary>Returns the header value to record: the real value, or the placeholder.</summary>
    public static string HeaderValue(string name, string value) =>
        IsSensitiveHeader(name) ? Placeholder : value;

    /// <summary>Redacts a set of headers for artifact capture.</summary>
    public static Dictionary<string, string> Headers(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in headers)
        {
            result[name] = HeaderValue(name, string.Join(", ", values));
        }

        return result;
    }

    /// <summary>Redacts credential-shaped content from a body before it is written to disk.</summary>
    public static string Body(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return string.Empty;
        }

        var redacted = body;
        foreach (var (pattern, replacement) in BodyPatterns)
        {
            redacted = pattern.Replace(redacted, replacement);
        }

        return redacted;
    }

    /// <summary>
    /// Strips userinfo and query-string credentials from a URL so the endpoint is
    /// still reproducible but no secret travels with it.
    /// </summary>
    public static string Url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Body(url);
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        if (!string.IsNullOrEmpty(uri.Query))
        {
            var pairs = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            var kept = pairs.Select(pair =>
            {
                var name = pair.Split('=', 2)[0];
                return IsSensitiveQueryParameter(name) ? $"{name}={Placeholder}" : pair;
            });
            builder.Query = string.Join('&', kept);
        }

        return builder.Uri.ToString();
    }

    private static bool IsSensitiveQueryParameter(string name) =>
        name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Equals("code", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase);
}
