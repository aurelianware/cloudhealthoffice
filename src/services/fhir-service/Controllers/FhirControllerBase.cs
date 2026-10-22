using Hl7.Fhir.Model;
using FhirService.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using CloudHealthOffice.Infrastructure.Logging;

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
            // JWT claim names: neither scheme maps inbound claims (D-32).
            foreach (var claimType in new[] { "sub", "client_id", "azp" })
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
    /// The one member this request may read, when it is confined to one:
    /// the patient a patient-scoped SMART token is bound to, or the member a
    /// provider/backend SMART token was just authorized for by
    /// ProviderAccessAuthorizationFilter (attribution + active ProviderAccess
    /// consent). Null for CHO callers, whose permission governs them, and for
    /// requests the filter does not govern.
    /// </summary>
    protected string? AuthorizedMemberId
        => SmartPatientId
           ?? HttpContext.Items[FhirService.Services.ProviderAccess.ProviderAccessAuthorizationFilter.AuthorizedMemberItemKey] as string;

    /// <summary>
    /// True when a request confined to one member (<see cref="AuthorizedMemberId"/>)
    /// asks for a resource that belongs to someone else (or to no identifiable
    /// member). Reads answer 404 then, so the caller cannot even learn that
    /// another member's resource exists. Always false for CHO callers.
    /// </summary>
    protected bool IsOutsidePatientContext(string? memberReference)
    {
        var bound = AuthorizedMemberId;
        if (bound is null) return false;
        if (string.IsNullOrEmpty(memberReference)) return true;

        var member = memberReference.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase)
            ? memberReference["Patient/".Length..]
            : memberReference;
        return !string.Equals(member, bound, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defence in depth for a search confined to one member: true when any
    /// result belongs to someone else, which means the data source ignored the
    /// member filter it was given. Logged as an error; the caller answers 502
    /// rather than return or silently trim another member's data.
    /// </summary>
    protected bool ResultsLeaveConfinement(string resourceType, IEnumerable<string?> memberReferences)
    {
        if (AuthorizedMemberId is null) return false;
        if (!memberReferences.Any(IsOutsidePatientContext)) return false;

        // Withholding the data must not depend on the logging infrastructure
        // being reachable, so the log is best-effort: a request without a
        // service provider still gets the refusal.
        HttpContext.RequestServices?.GetService<ILoggerFactory>()?.CreateLogger(GetType())
            .LogError("{Resource} search returned records outside the caller's member confinement; response withheld.", resourceType);
        return true;
    }

    /// <summary>SMART scopes approved for this request.</summary>
    protected IReadOnlySet<string> SmartScopes
        => HttpContext.Items["SmartScopes"] as HashSet<string> ?? new HashSet<string>();

    /// <summary>
    /// True only for a SMART backend (client-credentials) caller: the token's
    /// scopes — from the <c>scope</c> and/or <c>scp</c> claims, parsed by
    /// <see cref="FhirService.Services.Identity.SmartScopeClaims"/>, the parser
    /// <c>AuthenticatedCaller</c> and the scope middleware use — classify it as
    /// a system caller, and it is bound to no patient. A token carrying any
    /// patient/ or user/ scope, or any patient binding (middleware-injected,
    /// issuer-mapped, or the raw <c>patient</c> claim), is not a backend
    /// caller, however else it is scoped. A token whose <c>scope</c> and
    /// <c>scp</c> disagree never authenticates (D-32).
    /// </summary>
    protected bool IsBackendSystemCaller
    {
        get
        {
            if (User is null) return false;
            if (!string.IsNullOrEmpty(SmartPatientId)) return false;

            var caller = HttpContext.Items[FhirService.Services.Identity.AuthenticatedCaller.HttpContextItemKey]
                as FhirService.Services.Identity.AuthenticatedCaller;
            if (!string.IsNullOrEmpty(caller?.PatientId)) return false;
            if (!string.IsNullOrEmpty(User.FindFirst("patient")?.Value)) return false;
            if (caller is not null && caller.CallerType != FhirService.Services.Identity.SmartCallerType.System)
                return false;

            // Also classify from the principal itself: AuthenticatedCaller is
            // only present when the issuer resolved in the trusted registry.
            var scopes = FhirService.Services.Identity.SmartScopeClaims.Parse(User);

            return FhirService.Services.Identity.CallerIdentityResolver.ClassifyCaller(scopes)
                   == FhirService.Services.Identity.SmartCallerType.System;
        }
    }

    /// <summary>
    /// The payer the authenticated backend client is registered to act for
    /// (D-13): the <c>payer_id</c> claim of a token from the ONE trusted issuer
    /// marked as CHO's client-registration authority, resolved by
    /// <see cref="FhirService.Services.Identity.CallerIdentityResolver"/>.
    /// Null for any other issuer (its payer_id is ignored), for a client with
    /// no registered payer, and for a principal whose issuer did not resolve.
    /// </summary>
    protected string? RegisteredPayerId
        => (HttpContext.Items[FhirService.Services.Identity.AuthenticatedCaller.HttpContextItemKey]
               as FhirService.Services.Identity.AuthenticatedCaller)?.RegisteredPayerId;

    /// <summary>
    /// D-13: binds a Payer-to-Payer request's receiving payer to
    /// <see cref="RegisteredPayerId"/>. False (the caller answers one fixed 403)
    /// when the client has no registered payer, or when the body names a
    /// payer other than the registered one. The body value is optional; when
    /// present it must equal the registered payer exactly. On success
    /// <paramref name="receivingPayerId"/> is the REGISTERED payer, which is
    /// what the audit and logs record.
    /// </summary>
    protected bool TryBindReceivingPayer(
        string? namedInBody, ILogger logger, string operation, out string receivingPayerId)
    {
        receivingPayerId = string.Empty;
        var registered = RegisteredPayerId;
        var client = LogSanitizer.Sanitize(AuthenticatedActorId ?? "unknown");
        if (registered is null)
        {
            logger.LogWarning(
                "P2P {Operation} refused: client={Client} has no payer registered with the CHO registration authority",
                operation, client);
            return false;
        }

        var named = namedInBody?.Trim();
        if (!string.IsNullOrEmpty(named) && !string.Equals(named, registered, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "P2P {Operation} refused: client={Client} registeredPayer={Payer} named a different receiving payer",
                operation, client, LogSanitizer.Sanitize(registered));
            return false;
        }

        receivingPayerId = registered;
        return true;
    }

    /// <summary>
    /// 403 with a FHIR <c>OperationOutcome</c>. Callers pass a FIXED message,
    /// never one built from the request, so a refusal reveals nothing about
    /// the member or resource the request named.
    /// </summary>
    protected IActionResult FhirForbidden(string diagnostics)
        => StatusCode(403, BuildOutcome(
            OperationOutcome.IssueSeverity.Error,
            OperationOutcome.IssueType.Forbidden,
            diagnostics));

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
        Func<string, bool>? successBodyAllowed = null,
        Func<string, bool>? successBodyLeavesConfinement = null)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(logger);

        // `path` is derived from the user-supplied URL / query string and
        // flows into structured-log fields below. Sanitize once up front
        // so all log sites share the same scrubbed value (CodeQL: log
        // entries created from user input).
        var loggablePath = LogSanitizer.Sanitize(path);
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

            // A confined search whose results include another member's
            // resource means the upstream ignored its filter: nothing of it is
            // returned (defence in depth, as for Patient and Coverage, ADR 018).
            if (response.IsSuccessStatusCode && successBodyLeavesConfinement != null && successBodyLeavesConfinement(body))
            {
                logger.LogError(
                    "{Upstream} {Resource} search returned records outside the caller's member confinement for {Path}; response withheld",
                    upstreamLabel, resourceLabel, loggablePath);
                return FhirBadGateway($"{resourceLabel} upstream returned records outside the request's member scope.");
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
