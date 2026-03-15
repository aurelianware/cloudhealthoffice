using Hl7.Fhir.Model;
using FhirService.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace FhirService.Controllers;

/// <summary>
/// Shared helpers for all FHIR resource controllers.
/// Centralises OperationOutcome construction and FHIR-compliant HTTP responses.
/// </summary>
[ApiController]
public abstract class FhirControllerBase : ControllerBase
{
    protected string TenantId
        => HttpContext.GetTenantId() ?? "default";

    protected string FhirBaseUrl
    {
        get
        {
            var req = HttpContext.Request;
            return $"{req.Scheme}://{req.Host}/fhir/r4";
        }
    }

    protected string RawQueryString
        => HttpContext.Request.QueryString.Value ?? string.Empty;

    // ── SMART context ─────────────────────────────────────────────────────────

    /// <summary>
    /// Patient ID injected by SmartScopeEnforcementMiddleware from the `patient` JWT claim.
    /// Non-null when the token is patient-scoped.  Controllers use this to auto-restrict
    /// searches to the bound patient without requiring the caller to pass a patient param.
    /// </summary>
    protected string? SmartPatientId
        => HttpContext.Items["SmartPatientId"] as string;

    /// <summary>SMART scopes approved for this request.</summary>
    protected IReadOnlySet<string> SmartScopes
        => HttpContext.Items["SmartScopes"] as HashSet<string> ?? new HashSet<string>();

    // ── OperationOutcome helpers ──────────────────────────────────────────────

    protected IActionResult FhirNotFound(string resourceType, string id)
        => StatusCode(404, BuildOutcome(
            OperationOutcome.IssueSeverity.Error,
            OperationOutcome.IssueType.NotFound,
            $"{resourceType}/{id} not found"));

    protected IActionResult FhirBadRequest(string diagnostics)
        => StatusCode(400, BuildOutcome(
            OperationOutcome.IssueSeverity.Error,
            OperationOutcome.IssueType.Invalid,
            diagnostics));

    protected IActionResult FhirUnprocessable(string diagnostics)
        => StatusCode(422, BuildOutcome(
            OperationOutcome.IssueSeverity.Error,
            OperationOutcome.IssueType.Processing,
            diagnostics));

    private static OperationOutcome BuildOutcome(
        OperationOutcome.IssueSeverity severity,
        OperationOutcome.IssueType code,
        string diagnostics)
        => new()
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = severity,
                    Code = code,
                    Diagnostics = diagnostics
                }
            ]
        };

    // ── Search param clamping ─────────────────────────────────────────────────

    protected static int ClampPageSize(int requested, int max = 100)
        => Math.Clamp(requested, 1, max);

    protected static int ClampPage(int requested)
        => Math.Max(1, requested);

    // ── Logging helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Removes CR/LF characters from a user-supplied value before it is written
    /// to a log entry, preventing log-injection attacks.
    /// </summary>
    protected static string SanitizeForLog(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty
           : value.Replace("\r", string.Empty, StringComparison.Ordinal)
                  .Replace("\n", string.Empty, StringComparison.Ordinal);
}
