namespace FhirService.Services.ChoNative;

/// <summary>
/// A CHO service behind a FHIR data adapter could not answer: 5xx, transport
/// failure, timeout or an unreadable body. Answered as FHIR 502 with a transient
/// OperationOutcome that names the resource, never the upstream detail.
/// </summary>
public sealed class FhirUpstreamException : Exception
{
    public FhirUpstreamException(string resource, string message, Exception? inner = null)
        : base(message, inner)
    {
        Resource = resource;
    }

    /// <summary>The FHIR resource type the request was for.</summary>
    public string Resource { get; }
}

/// <summary>
/// The search asked for something this server does not do (an unsupported date
/// prefix, an identifier system CHO does not index, a reference to another
/// resource type). Answered as FHIR 400 rather than silently ignoring the
/// parameter, so a client never mistakes an unfiltered result for a filtered one.
/// </summary>
public sealed class FhirSearchNotSupportedException : Exception
{
    public FhirSearchNotSupportedException(string message) : base(message) { }
}
