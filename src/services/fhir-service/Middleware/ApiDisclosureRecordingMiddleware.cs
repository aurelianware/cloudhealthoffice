using FhirService.Services.ApiUsage;
using FhirService.Services.Identity;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Middleware;

/// <summary>
/// Writes a Patient Access disclosure to the <see cref="IApiDisclosureLedger"/>
/// whenever a patient-context SMART token (a member's own app) gets a
/// successful read of FHIR data, so the plan's CMS-0057-F usage report counts
/// what CHO served (Q-20).
///
/// Runs after SMART scope enforcement, so only requests the caller was allowed
/// to make are seen. It counts successful GETs and POST searches under
/// <c>/fhir/r4</c>, leaving out conformance and directory paths that carry no
/// member data. Recording never fails the request: the response is already
/// sent, and a ledger outage is logged instead.
/// </summary>
public sealed class ApiDisclosureRecordingMiddleware
{
    private static readonly string[] NonDataPaths =
    [
        "/fhir/r4/metadata", "/fhir/r4/.well-known", "/fhir/r4/adapter-status",
        "/fhir/r4/StructureDefinition", "/fhir/r4/OperationDefinition",
        "/fhir/r4/CodeSystem", "/fhir/r4/ValueSet", "/fhir/r4/directory",
    ];

    private readonly RequestDelegate _next;
    private readonly ILogger<ApiDisclosureRecordingMiddleware> _logger;

    public ApiDisclosureRecordingMiddleware(RequestDelegate next, ILogger<ApiDisclosureRecordingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IApiDisclosureLedger ledger)
    {
        await _next(context);

        if (ToDisclosure(context, DateTime.UtcNow) is not { } disclosure)
            return;
        try
        {
            await ledger.RecordAsync(disclosure, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Patient Access disclosure for tenant {TenantId} was not recorded; the usage report will undercount",
                LogSanitizer.Sanitize(disclosure.TenantId));
        }
    }

    /// <summary>The disclosure this finished request made, or null when it made none.</summary>
    internal static ApiDisclosure? ToDisclosure(HttpContext context, DateTime nowUtc)
    {
        if (context.Response.StatusCode is < 200 or > 299)
            return null;

        var request = context.Request;
        var isRead = HttpMethods.IsGet(request.Method)
                     || (HttpMethods.IsPost(request.Method) && request.Path.Value?.EndsWith("/_search", StringComparison.Ordinal) == true);
        if (!isRead || !request.Path.StartsWithSegments("/fhir/r4") || NonDataPaths.Any(p => request.Path.StartsWithSegments(p)))
            return null;

        // The patient the request was bound to is the one SmartScopeEnforcement
        // enforced (it sets SmartPatientId only for a patient-scoped token), so
        // what is counted is exactly what was allowed.
        if (!FhirCallerSchemes.IsSmart(context.User)
            || context.Items["SmartPatientId"] is not string patientId
            || string.IsNullOrEmpty(patientId)
            || context.Items["TenantId"] is not string tenantId
            || string.IsNullOrEmpty(tenantId))
            return null;

        var caller = context.Items[AuthenticatedCaller.HttpContextItemKey] as AuthenticatedCaller;
        if (caller is not null && caller.CallerType != SmartCallerType.Patient)
            return null;

        var clientId = caller?.ClientId
                       ?? context.User.FindFirst("client_id")?.Value
                       ?? context.User.FindFirst("azp")?.Value;

        return new ApiDisclosure(
            tenantId,
            InteroperabilityApi.PatientAccess,
            patientId,
            string.IsNullOrEmpty(clientId) ? "(unknown)" : clientId,
            DateOnly.FromDateTime(nowUtc));
    }
}
