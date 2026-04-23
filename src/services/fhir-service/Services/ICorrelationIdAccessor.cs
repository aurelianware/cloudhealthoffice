namespace FhirService.Services;

/// <summary>
/// Per-request correlation ID holder. Populated by the controller that
/// starts a work unit (e.g. <c>AppealSubmitController.SubmitAppeal</c>)
/// and read by the <c>CorrelationIdPropagationHandler</c> attached to
/// every HttpClient that calls downstream services.
///
/// Scope is request-scoped (DI: <c>AddScoped</c>). For non-Submit paths,
/// the accessor is seeded by middleware from the inbound
/// <c>X-Correlation-Id</c> header, or given a fresh GUID when absent.
/// </summary>
public interface ICorrelationIdAccessor
{
    /// <summary>
    /// Current correlation ID for this request. Never null — the default
    /// value is a fresh GUID when the accessor is first accessed.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Replace the current correlation ID. Used by submit workflows that
    /// want to anchor all sub-requests to a single logical operation.
    /// </summary>
    void Set(string correlationId);
}

public sealed class CorrelationIdAccessor : ICorrelationIdAccessor
{
    private string? _value;

    public string CorrelationId => _value ??= Guid.NewGuid().ToString("D");

    public void Set(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("Correlation ID must be non-empty.", nameof(correlationId));
        _value = correlationId;
    }
}
