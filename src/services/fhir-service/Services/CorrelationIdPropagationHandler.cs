namespace FhirService.Services;

/// <summary>
/// DelegatingHandler that stamps every outbound request with the current
/// request's correlation ID. Pattern set in PR 3 for the
/// <c>ChoAppealsService</c> named HttpClient; PR 4 will extend it for
/// the 275 consumer tracing path.
///
/// Header name: <c>X-Correlation-Id</c>. Downstream appeals-service
/// stores the value on its AppealEvent.CorrelationId field (audit
/// trail), so a submit sequence (POST /api/appeals → POST notes →
/// POST attachments) is end-to-end reconstructable from that single id.
/// </summary>
public sealed class CorrelationIdPropagationHandler : DelegatingHandler
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly ICorrelationIdAccessor _accessor;

    public CorrelationIdPropagationHandler(ICorrelationIdAccessor accessor)
    {
        _accessor = accessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Replace any caller-supplied value — the current accessor is the
        // single source of truth for this request's correlation id.
        request.Headers.Remove(HeaderName);
        request.Headers.Add(HeaderName, _accessor.CorrelationId);
        return base.SendAsync(request, cancellationToken);
    }
}
