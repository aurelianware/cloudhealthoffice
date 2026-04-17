namespace MemberService.Services;

/// <summary>
/// Thrown by downstream clients when the remote service is unconfigured or unreachable.
/// Controllers convert these to RFC 7807 ProblemDetails with status 503.
/// </summary>
public sealed class DownstreamUnavailableException : Exception
{
    public string ServiceName { get; }
    public string? Detail { get; }

    public DownstreamUnavailableException(string serviceName, string? detail = null, Exception? inner = null)
        : base($"Downstream service '{serviceName}' is unavailable.", inner)
    {
        ServiceName = serviceName;
        Detail = detail;
    }
}
