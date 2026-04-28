using Hl7.Fhir.Model;
using FhirService.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

    /// <summary>
    /// 502 Bad Gateway with a FHIR <c>OperationOutcome</c>. Used when an
    /// upstream FHIR service this controller proxies to (e.g.
    /// provider-service for capability 5.7 Practitioner endpoints) fails
    /// or returns a non-FHIR error. Diagnostics is the operator-facing
    /// reason — DO NOT pass through arbitrary upstream response bodies
    /// here as they may leak internal detail.
    /// </summary>
    protected IActionResult FhirBadGateway(string diagnostics)
        => StatusCode(502, BuildOutcome(
            OperationOutcome.IssueSeverity.Error,
            OperationOutcome.IssueType.Transient,
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

    // ── Generic upstream proxy helper ────────────────────────────────────────

    /// <summary>
    /// Forward a GET request to an upstream FHIR-emitting service and pass
    /// the response through to the caller. Status pass-through, 5xx → 502
    /// FHIR <c>OperationOutcome</c>, transport faults → 502, caller
    /// cancellation propagates verbatim.
    ///
    /// <para>
    /// Extracted in capability BP 5.8 so both the provider-service proxy
    /// (capabilities 5.7 / 5.8 / 5.9) and the new benefit-plan-service
    /// proxy (capability BP 5.8 InsurancePlan) share one
    /// status-translation rule. Decision 5b — the helper takes the
    /// upstream <see cref="HttpClient"/> as a parameter so callers can
    /// keep using the typed-client pattern they already have.
    /// </para>
    ///
    /// <para>
    /// Logging uses the structured fields <c>{Upstream}</c>,
    /// <c>{Resource}</c>, <c>{Status}</c>, <c>{Path}</c> so operators can
    /// distinguish proxy failures by upstream service AND by resource
    /// type (Practitioner / Organization / InsurancePlan / etc.) without
    /// adding new log lines.
    /// </para>
    /// </summary>
    protected async Task<IActionResult> ProxyUpstreamServiceAsync(
        HttpClient upstream,
        string upstreamLabel,
        string resourceLabel,
        string path,
        ILogger logger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(logger);

        // `path` is derived from the user-supplied URL / query string and
        // flows into structured-log fields below. Sanitize once up front
        // so all log sites share the same scrubbed value (CodeQL: log
        // entries created from user input).
        var loggablePath = SanitizeForLog(path);
        try
        {
            using var response = await upstream.GetAsync(path, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/fhir+json";

            // Pass status + body through verbatim. The upstream service
            // emits FHIR OperationOutcome on 4xx, so the proxy needs to
            // forward those without rewrapping. 5xx responses are mapped
            // to a FHIR 502 OperationOutcome — exposing upstream 5xx
            // bodies could leak internal detail.
            if ((int)response.StatusCode >= 500)
            {
                logger.LogWarning(
                    "{Upstream} {Resource} upstream returned {Status} for {Path}",
                    upstreamLabel, resourceLabel, (int)response.StatusCode, loggablePath);
                return FhirBadGateway($"{resourceLabel} upstream is unavailable.");
            }

            return new ContentResult
            {
                Content = body,
                ContentType = contentType,
                StatusCode = (int)response.StatusCode,
            };
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex,
                "{Upstream} {Resource} proxy hop failed for {Path}",
                upstreamLabel, resourceLabel, loggablePath);
            return FhirBadGateway($"{resourceLabel} upstream is unreachable.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled (client disconnect, server abort). Don't
            // pretend the upstream timed out — propagate cancellation so
            // the request pipeline returns its standard 499/aborted shape
            // and we don't pollute logs / metrics with phantom 502s.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own configured timeout as
            // TaskCanceledException; ct was NOT cancelled (handled above).
            // That genuinely is an upstream-too-slow → 502.
            logger.LogWarning(ex,
                "{Upstream} {Resource} proxy hop timed out for {Path}",
                upstreamLabel, resourceLabel, loggablePath);
            return FhirBadGateway($"{resourceLabel} upstream timed out.");
        }
    }
}
