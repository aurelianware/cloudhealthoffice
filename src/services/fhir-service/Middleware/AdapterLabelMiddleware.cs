using FhirService.Services;

namespace FhirService.Middleware;

/// <summary>
/// Stamps every response with Demo / Hybrid / Live adapter headers so a
/// buyer demo cannot be mistaken for live payer-backed evidence.
/// </summary>
public sealed class AdapterLabelMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IFhirAdapterStatusService _status;

    public AdapterLabelMiddleware(RequestDelegate next, IFhirAdapterStatusService status)
    {
        _next = next;
        _status = status;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var report = _status.GetStatus();
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers[FhirAdapterStatusService.HeaderMode] = report.EffectiveMode;
            headers[FhirAdapterStatusService.HeaderDataClass] = report.DataClassification;
            headers[FhirAdapterStatusService.HeaderLabel] = report.BuyerSafeLabel;
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
