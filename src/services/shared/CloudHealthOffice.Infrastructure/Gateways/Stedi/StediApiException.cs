namespace CloudHealthOffice.Infrastructure.Gateways.Stedi;

/// <summary>
/// Internal exception for Stedi transport/parse failures. It carries a
/// vendor-neutral <see cref="GatewayErrorCategory"/> and a message that is safe
/// for normal logs — it never contains the API key, request/response bodies, or
/// PHI. This type stays inside the Stedi implementation; the gateway converts it
/// into a <c>GatewayResponse</c> failure, so it never leaks to domain services.
/// </summary>
internal sealed class StediApiException : Exception
{
    public GatewayErrorCategory Category { get; }

    /// <summary>Whether a transient failure of this kind may be retried.</summary>
    public bool IsTransient { get; }

    /// <summary>Suggested delay before retry (e.g. from a Retry-After header).</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Number of retries performed before this failure surfaced.</summary>
    public int RetryCount { get; set; }

    public StediApiException(
        GatewayErrorCategory category,
        string message,
        bool isTransient = false,
        TimeSpan? retryAfter = null,
        Exception? inner = null)
        : base(message, inner)
    {
        Category = category;
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }
}
