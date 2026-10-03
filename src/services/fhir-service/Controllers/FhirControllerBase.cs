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
    /// <summary>
    /// The tenant from the caller's validated token. There is no default: a
    /// request without one never reaches a controller (the shared
    /// TenantMiddleware refuses it), and if one did, it fails here rather than
    /// reading another tenant's data.
    /// </summary>
    protected string TenantId
        => HttpContext.GetTenantId()
           ?? throw new InvalidOperationException("No authenticated tenant for this request.");

    /// <summary>
    /// The acting identity from the validated token (CHO <c>sub</c>, or for a
    /// SMART caller its subject or client id). Never from a header or body.
    /// </summary>
    protected string? AuthenticatedActorId
    {
        get
        {
            foreach (var claimType in new[]
                     {
                         "sub", System.Security.Claims.ClaimTypes.NameIdentifier, "client_id", "azp",
                     })
            {
                var value = User?.FindFirst(claimType)?.Value;
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }
    }

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

    /// <summary>
    /// True when a patient-bound SMART caller asks for a resource that belongs
    /// to someone else (or to no identifiable member). Reads answer 404 then,
    /// so a patient token cannot even learn that another member's resource
    /// exists. Always false for CHO callers and for user/system SMART tokens,
    /// which have no patient binding.
    /// </summary>
    protected bool IsOutsidePatientContext(string? memberReference)
    {
        var bound = SmartPatientId;
        if (bound is null) return false;
        if (string.IsNullOrEmpty(memberReference)) return true;

        var member = memberReference.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase)
            ? memberReference["Patient/".Length..]
            : memberReference;
        return !string.Equals(member, bound, StringComparison.Ordinal);
    }

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
        CancellationToken ct,
        Func<string, bool>? successBodyAllowed = null)
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

            // A successful body the caller may not see (another member's
            // resource for a patient-bound token) is answered as not found.
            if (response.IsSuccessStatusCode && successBodyAllowed != null && !successBodyAllowed(body))
            {
                logger.LogWarning(
                    "{Upstream} {Resource} response withheld: outside the caller's patient context for {Path}",
                    upstreamLabel, resourceLabel, loggablePath);
                return StatusCode(404, BuildOutcome(
                    OperationOutcome.IssueSeverity.Error,
                    OperationOutcome.IssueType.NotFound,
                    $"{resourceLabel} not found"));
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
