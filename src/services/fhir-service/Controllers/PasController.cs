using FhirService.Services.Identity;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Hl7.Fhir.Model;
using FhirService.Models;
using FhirService.Services;
using FhirService.Services.Pas;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using CloudHealthOffice.Infrastructure.Middleware;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Logging;

namespace FhirService.Controllers;

/// <summary>
/// Da Vinci PAS (Prior Authorization Support) controller.
/// Implements the synchronous Claim/$submit operation per PAS IG v2.1.0.
/// Target: respond within 15 seconds including network time.
/// </summary>
[Route("fhir/r4")]
public class PasController : FhirControllerBase
{
    private readonly IPasAutoAdjudicator _adjudicator;
    private readonly PasResponseBuilder _responseBuilder;
    private readonly ICms0057ComplianceChecker _complianceChecker;
    private readonly IPriorAuthorizationInquiryService _inquiry;
    private readonly PasAutoAdjudicationConfig _config;
    private readonly IPasAuthorizationRecorder _recorder;
    private readonly HttpClient _providerVerificationClient;
    private readonly ILogger<PasController> _logger;

    public PasController(
        IPasAutoAdjudicator adjudicator,
        PasResponseBuilder responseBuilder,
        ICms0057ComplianceChecker complianceChecker,
        IPriorAuthorizationInquiryService inquiry,
        IOptions<PasAutoAdjudicationConfig> config,
        IHttpClientFactory httpClientFactory,
        IPasAuthorizationRecorder recorder,
        ILogger<PasController> logger)
    {
        _adjudicator = adjudicator;
        _responseBuilder = responseBuilder;
        _complianceChecker = complianceChecker;
        _inquiry = inquiry;
        _config = config.Value;
        _recorder = recorder;
        _providerVerificationClient = httpClientFactory.CreateClient("ProviderVerificationService");
        _logger = logger;
    }

    /// <summary>
    /// Da Vinci PAS Claim/$submit
    /// Synchronous prior authorization request/response.
    /// Target: respond within 15 seconds per PAS IG 2.1.0 Section 5.2.1.
    /// </summary>
    [HttpPost("Claim/$submit")]
    [FhirAccess(smart: true, cho: "authorizations:write")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ClaimSubmit([FromBody] Bundle requestBundle)
    {
        // The decision clock starts when the request arrives (CMS-0057-F), so
        // receipt is stamped first, before validation and adjudication (D-08),
        // and stored as the record's SubmittedDate.
        var receivedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Validate and extract the PAS Claim from the request bundle
            var (validatedClaim, validationError) = ValidateAndExtractClaim(requestBundle);
            if (validationError != null)
            {
                return validationError;
            }
            var claim = validatedClaim!;

            _logger.LogInformation(
                "PAS $submit received for tenant {TenantId}, claim type {ClaimType}",
                LogSanitizer.Sanitize(TenantId),
                LogSanitizer.Sanitize(claim.Type?.Coding?.FirstOrDefault()?.Code ?? "unknown"));

            // 2. Validate compliance
            var compliance = _complianceChecker.ValidateCompliance(claim);
            if (!compliance.Compliant)
            {
                var issues = string.Join("; ", compliance.Issues.Select(i => i.Message));
                _logger.LogWarning("PAS $submit compliance validation failed: {Issues}", LogSanitizer.Sanitize(issues));
                return FhirBadRequest($"Claim does not meet CMS-0057-F compliance requirements: {issues}");
            }

            // 2.1 Map the request onto the authorization record BEFORE deciding
            // anything (D-01, D-03): a request the record cannot hold without
            // placeholders is refused here, so nothing is decided that cannot
            // be stored.
            var mapping = PasAuthorizationMapper.Map(requestBundle, claim, receivedAt);

            // Nonstandard or unrecognised values that were mapped anyway (e.g.
            // an urgent priority outside its code system). Request values,
            // so sanitized.
            foreach (var warning in mapping.Warnings)
            {
                _logger.LogWarning(
                    "PAS $submit for tenant {TenantId}: {Element}: {Warning} (system {System}, code {Code})",
                    LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(warning.Expression), warning.Message,
                    LogSanitizer.Sanitize(warning.System ?? "none"), LogSanitizer.Sanitize(warning.Code));
            }
            if (!mapping.IsValid)
            {
                _logger.LogWarning(
                    "PAS $submit refused for tenant {TenantId}: request incomplete at {Elements}",
                    LogSanitizer.Sanitize(TenantId),
                    LogSanitizer.Sanitize(string.Join(", ", mapping.Errors.Select(e => e.Expression))));
                RecordMetrics(sw, "rejected", "incomplete-request");
                return IncompleteRequest(mapping.Errors);
            }
            var record = mapping.Request!;

            // 2.5 Provider verification pre-check, on the requesting provider's
            // NPI as mapped from the Bundle.
            var providerNpi = record.RequestingProviderNPI;
            if (!string.IsNullOrEmpty(providerNpi))
            {
                var verificationResult = await CheckProviderVerificationAsync(providerNpi, HttpContext.RequestAborted);
                if (verificationResult.IsExcluded)
                {
                    var deniedDecision = new PasDecisionResult
                    {
                        HasDecision = true,
                        Decision = "denied",
                        DenialReasonCode = "PROVIDER_EXCLUDED",
                        DenialReason = $"Provider NPI {providerNpi} is excluded from federal healthcare programs." +
                                       (string.IsNullOrWhiteSpace(verificationResult.ExclusionSource)
                                           ? string.Empty
                                           : $" Source: {verificationResult.ExclusionSource}"),
                        // A fact about the provider, whatever the plan: no line
                        // of business or state was evaluated, so none is claimed.
                        RuleName = ProviderExclusionRule,
                    };

                    _logger.LogWarning(
                        "PAS $submit auto-denied: excluded provider NPI {Npi}, source={Source}",
                        LogSanitizer.Sanitize(providerNpi), LogSanitizer.Sanitize(verificationResult.ExclusionSource));

                    return await RecordAndRespondAsync(claim, record, mapping, deniedDecision, sw);
                }

                if (verificationResult.IntegrityScore >= 0 && verificationResult.IntegrityScore < 40)
                {
                    _logger.LogWarning(
                        "PAS $submit: provider NPI {Npi} has low integrity score {Score} ({Rating})",
                        LogSanitizer.Sanitize(providerNpi), verificationResult.IntegrityScore,
                        LogSanitizer.Sanitize(verificationResult.Rating));
                }
            }

            // 3. Auto-adjudicate with time budget, on the line of business and
            // providers the record stores (B1). A line of business the request
            // does not determine is not guessed: nothing is auto-decided, the
            // request is pended (A4) and the record says why.
            if (!TryLineOfBusiness(record.LineOfBusiness, out var lineOfBusiness))
            {
                _logger.LogInformation(
                    "PAS $submit for tenant {TenantId}: line of business undetermined, pended without adjudication",
                    LogSanitizer.Sanitize(TenantId));
                return await RecordAndRespondAsync(claim, record, mapping, new PasDecisionResult
                {
                    HasDecision = false,
                    RuleName = LineOfBusinessUndeterminedRule,
                    PendReason = LineOfBusinessUndeterminedPendReason,
                    // Nothing was evaluated: no line of business is claimed.
                }, sw);
            }

            var basis = PasDecisionBasis.From(lineOfBusiness, record);
            var timeBudgetMs = _config.MaxResponseMs;
            var decision = await _adjudicator.TryDecideAsync(
                claim, requestBundle, TenantId, basis, timeBudgetMs, HttpContext.RequestAborted);

            return await RecordAndRespondAsync(claim, record, mapping, decision, sw);
        }
        catch (Exception ex)
        {
            var traceId = ChoProblemResults.TraceId(HttpContext);
            _logger.LogError(ex, "PAS $submit failed after {ElapsedMs}ms (trace id {TraceId})", sw.ElapsedMilliseconds, traceId);
            RecordMetrics(sw, "error", "exception");
            // The exception text (hosts, connection strings, PHI) stays in the log.
            return FhirUnprocessable($"Internal error processing PAS request. Quote traceId {traceId} when reporting it.");
        }
    }

    /// <summary>
    /// Stores the request and its decision, then answers with what was STORED.
    /// Every outcome (approved, denied, pended) gets its tracking number before
    /// the write, and the response carries it only after the write succeeded:
    /// a <c>preAuthRef</c> is never issued for a record that does not exist
    /// (D-01). If the write fails, <c>$submit</c> fails with an
    /// OperationOutcome and no ClaimResponse.
    /// </summary>
    private async Task<IActionResult> RecordAndRespondAsync(
        Claim claim, PasAuthorizationRecordRequest record, PasMappingResult mapping,
        PasDecisionResult decision, Stopwatch sw)
    {
        decision.AuthorizationNumber ??= NewAuthorizationNumber();

        var outcome = decision.HasDecision && decision.Decision == "approved" ? "Approved"
            : decision.HasDecision && decision.Decision == "denied" ? "Denied"
            : "Pended";

        // The approved period is the requested one (D-03); see
        // PasAuthorizationMapper for the open-ended fallback. An adjudicator
        // that states its own period is respected.
        if (outcome == "Approved" && decision.EffectiveFrom is null)
        {
            decision.EffectiveFrom = mapping.ApprovedFrom;
            decision.EffectiveTo ??= mapping.ApprovedTo;
        }

        record.AuthorizationNumber = decision.AuthorizationNumber;
        record.Decision = new PasAutomatedDecision
        {
            Outcome = outcome,
            DenialReasonCode = outcome == "Denied" ? decision.DenialReasonCode : null,
            DenialReason = outcome == "Denied" ? decision.DenialReason : null,
            PendReason = outcome == "Pended" ? decision.PendReason ?? PendReason : null,
            ApprovedServiceDateFrom = outcome == "Approved" ? decision.EffectiveFrom : null,
            ApprovedServiceDateTo = outcome == "Approved" ? decision.EffectiveTo : null,
            DecidedAt = DateTime.UtcNow,
            Rule = decision.RuleName is { Length: > 100 } rule ? rule[..100] : decision.RuleName,
            // Only the basis the deciding path actually used (null = none).
            // authorization-service refuses a stated LOB that is not the
            // record's, and a missing one except for the rules that evaluate
            // none.
            LineOfBusiness = decision.LineOfBusiness,
            StateCode = decision.StateCode,
        };

        var stored = await _recorder.RecordAsync(record, TenantId, HttpContext.RequestAborted);
        if (!stored.Recorded)
        {
            RecordMetrics(sw, "error", "persist-failed");
            _logger.LogError(
                "PAS $submit for tenant {TenantId} failed: authorization {AuthNumber} was not stored ({Failure}); "
                + "no preAuthRef issued",
                LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(decision.AuthorizationNumber), stored.Failure);
            return NotRecorded(stored.Failure);
        }

        // The answer is built from the STORED record, never from the decision
        // sent: the record is what $inquire will return, and it can differ (a
        // plan exclusion in authorization-service denies a request the
        // adjudicator approved or pended; a repeat of a stored request returns
        // the record as stored). A status $submit has no answer for is a
        // failure, with no preAuthRef.
        var kept = stored.Stored!;
        var answer = new PasDecisionResult
        {
            AuthorizationNumber = kept.AuthorizationNumber,
            RuleName = decision.RuleName,
        };
        Bundle responseBundle;
        switch (kept.Status)
        {
            case PriorAuthorizationStatus.Approved:
                answer.HasDecision = true;
                answer.Decision = "approved";
                answer.EffectiveFrom = kept.ApprovedServiceDateFrom;
                answer.EffectiveTo = kept.ApprovedServiceDateTo;
                responseBundle = _responseBuilder.BuildApprovedResponse(claim, answer, kept.RequestedServices);
                break;
            case PriorAuthorizationStatus.Modified:
            case PriorAuthorizationStatus.Expired:
            case PriorAuthorizationStatus.Cancelled:
                responseBundle = _responseBuilder.BuildInquiryResponse(new PriorAuthorizationRecord
                {
                    AuthorizationNumber = kept.AuthorizationNumber,
                    PatientReference = claim.Patient?.Reference,
                    Status = kept.Status, ReviewDecision = kept.ReviewDecision,
                    DenialReasonCode = kept.DenialReasonCode, DenialReason = kept.DenialReason,
                    ApprovedServiceDateFrom = kept.ApprovedServiceDateFrom,
                    ApprovedServiceDateTo = kept.ApprovedServiceDateTo,
                    ExpirationDate = kept.ExpirationDate,
                    RequestedServices = kept.RequestedServices,
                });
                break;
            case PriorAuthorizationStatus.Denied:
                answer.HasDecision = true;
                answer.Decision = "denied";
                answer.DenialReasonCode = kept.DenialReasonCode;
                answer.DenialReason = kept.DenialReason;
                if (outcome != "Denied")
                    answer.RuleName = "benefit-exclusion";
                responseBundle = _responseBuilder.BuildDeniedResponse(claim, answer, kept.RequestedServices);
                break;
            case PriorAuthorizationStatus.Pended:
                responseBundle = _responseBuilder.BuildPendedResponse(claim, kept.AuthorizationNumber, kept.RequestedServices);
                break;
            default:
                RecordMetrics(sw, "error", "unanswerable-stored-status");
                _logger.LogError(
                    "PAS $submit for tenant {TenantId}: authorization {AuthNumber} was stored as {Status}, "
                    + "which $submit cannot answer; no preAuthRef issued",
                    LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(kept.AuthorizationNumber), kept.Status);
                return NotRecorded(PasRecordFailure.Rejected);
        }

        var answered = kept.Status.ToString();
        RecordMetrics(sw, answered.ToLowerInvariant(), answer.RuleName);

        _logger.LogInformation(
            "PAS $submit completed in {ElapsedMs}ms, authorization={AuthNumber}, decision={Decision}, rule={Rule}",
            sw.ElapsedMilliseconds, LogSanitizer.Sanitize(kept.AuthorizationNumber), answered,
            LogSanitizer.Sanitize(answer.RuleName));

        return Ok(responseBundle);
    }

    /// <summary>Recorded on a pended request; the ClaimResponse says the same (queued for review).</summary>
    private const string PendReason = "Pended for clinical review.";

    /// <summary>The rule an excluded provider's request is denied by, before any adjudication.</summary>
    public const string ProviderExclusionRule = "provider-exclusion-check";

    /// <summary>The "rule" a request with an undetermined line of business is pended by.</summary>
    public const string LineOfBusinessUndeterminedRule = "LineOfBusinessUndetermined";

    public const string LineOfBusinessUndeterminedPendReason =
        "The line of business could not be determined from Coverage.type, so the request was not "
        + "auto-adjudicated. Pended for review.";

    /// <summary>
    /// The rule engine's line of business for the record's, or false for
    /// <c>Unknown</c> (and anything else the engine has no value for).
    /// </summary>
    private static bool TryLineOfBusiness(string recorded, out PaLineOfBusiness lineOfBusiness)
    {
        lineOfBusiness = default;
        return !string.Equals(recorded, "Unknown", StringComparison.Ordinal)
               && Enum.TryParse(recorded, ignoreCase: false, out lineOfBusiness)
               && Enum.IsDefined(lineOfBusiness)
               && string.Equals(lineOfBusiness.ToString(), recorded, StringComparison.Ordinal);
    }

    /// <summary>422 listing every element the request lacks; nothing was decided or stored.</summary>
    private IActionResult IncompleteRequest(IReadOnlyList<PasMappingError> errors)
        => StatusCode(422, new OperationOutcome
        {
            Issue = errors.Select(e => new OperationOutcome.IssueComponent
            {
                Severity = OperationOutcome.IssueSeverity.Error,
                Code = OperationOutcome.IssueType.Required,
                Diagnostics = e.Message,
                Expression = [e.Expression],
            }).ToList(),
        });

    /// <summary>The request was valid but could not be stored: 503 when that may pass, 502 otherwise.</summary>
    private IActionResult NotRecorded(PasRecordFailure failure)
        => StatusCode(failure == PasRecordFailure.Unavailable ? 503 : 502, new OperationOutcome
        {
            Issue =
            [
                new OperationOutcome.IssueComponent
                {
                    Severity = OperationOutcome.IssueSeverity.Error,
                    Code = failure == PasRecordFailure.Unavailable
                        ? OperationOutcome.IssueType.Transient
                        : OperationOutcome.IssueType.Exception,
                    Diagnostics = "The prior authorization request could not be recorded, so no decision "
                                  + "or preAuthRef is issued. Resubmit the request.",
                },
            ],
        });


    /// <summary>
    /// Da Vinci PAS <c>Claim/$inquire</c> — prior-authorization status inquiry.
    ///
    /// Read-only. Projects the CURRENT committed authorization state held by
    /// authorization-service onto a PAS ClaimResponse bundle. It creates
    /// nothing, changes nothing, and never re-submits to a payer, so repeating
    /// it is free of consequence.
    ///
    /// Request: a Bundle carrying a Claim whose identifier names the
    /// authorization (the <c>preAuthRef</c> issued at submit) together with a
    /// corroborating key — the patient reference or the requesting provider's
    /// NPI. The identifier alone is not enough; see
    /// <see cref="IPriorAuthorizationInquiryService"/> for why.
    ///
    /// AUTHORIZATION. As a POST operation this route is governed by the same
    /// controls as <c>$submit</c>: authentication (<c>[Authorize]</c>), the SMART
    /// <c>*/Claim.read</c> scope check, and tenant from the authenticated
    /// context. It is deliberately NOT routed through the Provider Access
    /// consent gate: that gate governs a provider reading a member's clinical
    /// record, whereas PAS is a system-to-system transaction between the
    /// submitter and the payer about the submitter's own request — which is why
    /// the corroborating key, not a member consent, is what binds an inquiry to
    /// its authorization.
    /// </summary>
    [HttpPost("Claim/$inquire")]
    [FhirAccess(smart: true, cho: "authorizations:read")]
    [Consumes("application/fhir+json", "application/json")]
    [Produces("application/fhir+json")]
    public async Task<IActionResult> ClaimInquire([FromBody] Bundle requestBundle)
    {
        var (claim, validationError) = ValidateAndExtractInquiryClaim(requestBundle);
        if (validationError != null)
            return validationError;

        // Tenant from the authenticated context, caller from the validated token —
        // neither is read from the request body. Lifting the lookup keys out of
        // the Claim is the service's job, so this action stays routing plus
        // HTTP mapping.
        var request = _inquiry.FromInquiryClaim(claim!, TenantId, CallerId());

        var result = await _inquiry.InquireAsync(request, HttpContext.RequestAborted);

        AuditInquiry(result);

        if (!result.Found)
        {
            // A defect in the REQUEST is the caller's to fix and says nothing
            // about what exists, so it is reported plainly as a 400.
            if (result.FailureKind == PriorAuthorizationInquiryFailureKind.BadRequest)
            {
                return FhirBadRequest(
                    result.Outcome == PriorAuthorizationInquiryOutcome.MissingIdentifier
                        ? "Inquiry Claim must carry the authorization identifier "
                          + "(Claim.identifier or Claim.insurance.preAuthRef)."
                        : "Inquiry must also carry a corroborating key: the patient "
                          + "reference or the requesting provider identifier.");
            }

            // Everything about a RECORD gets ONE answer. "Wrong tenant", "not
            // yours" and "no such authorization" must be indistinguishable, or
            // the identifier space can be probed for what exists.
            return StatusCode(404, new OperationOutcome
            {
                Issue =
                [
                    new OperationOutcome.IssueComponent
                    {
                        Severity = OperationOutcome.IssueSeverity.Error,
                        Code = OperationOutcome.IssueType.NotFound,
                        Diagnostics =
                            "No prior authorization matching the supplied identifiers is available.",
                    }
                ]
            });
        }

        return Ok(_responseBuilder.BuildInquiryResponse(result.Authorization!));
    }

    /// <summary>
    /// Records the inquiry with safe identifiers only: tenant, caller, the
    /// authorization asked about, the outcome category and the status returned.
    /// Never the Claim, the ClaimResponse, demographics, or clinical content.
    /// </summary>
    private void AuditInquiry(PriorAuthorizationInquiryResult result)
    {
        var caller = CallerId();

        if (result.Found)
        {
            _logger.LogInformation(
                "PAS $inquire: tenant={Tenant} caller={Caller} authorization={Auth} "
                + "outcome={Outcome} status={Status} at={At}",
                LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(caller),
                LogSanitizer.Sanitize(result.Authorization!.AuthorizationNumber),
                result.Outcome, result.Authorization.Status, DateTime.UtcNow);
            return;
        }

        _logger.LogWarning(
            "PAS $inquire refused: tenant={Tenant} caller={Caller} authorization={Auth} "
            + "outcome={Outcome} at={At}",
            LogSanitizer.Sanitize(TenantId), LogSanitizer.Sanitize(caller),
            LogSanitizer.Sanitize(result.RequestedAuthorizationNumber),
            result.Outcome, DateTime.UtcNow);
    }

    /// <summary>
    /// The inquiry Claim, validated with the same bundle guards as $submit
    /// (size cap, resource-type allowlist) but without $submit's requirement for
    /// full provider/insurance detail — an inquiry names an authorization, it
    /// does not restate the request.
    /// </summary>
    private (Claim? claim, IActionResult? error) ValidateAndExtractInquiryClaim(Bundle? requestBundle)
    {
        if (requestBundle?.Entry == null || requestBundle.Entry.Count == 0)
            return (null, FhirBadRequest("Inquiry bundle must contain at least one entry"));

        if (requestBundle.Entry.Count > MaxBundleEntries)
            return (null, FhirBadRequest(
                $"Inquiry bundle exceeds maximum of {MaxBundleEntries} entries"));

        foreach (var entry in requestBundle.Entry)
        {
            if (entry.Resource != null && !AllowedResourceTypes.Contains(entry.Resource.TypeName))
            {
                _logger.LogWarning("PAS $inquire rejected unexpected resource type: {Type}",
                    LogSanitizer.Sanitize(entry.Resource.TypeName));
                return (null, FhirBadRequest(
                    $"Bundle contains disallowed resource type: {entry.Resource.TypeName}"));
            }
        }

        var claim = requestBundle.Entry
            .Select(e => e.Resource)
            .OfType<Claim>()
            .FirstOrDefault();

        if (claim == null)
            return (null, FhirBadRequest("Inquiry bundle must contain a Claim resource"));

        if (claim.Use != ClaimUseCode.Preauthorization)
            return (null, FhirBadRequest(
                "Inquiry Claim.use must be 'preauthorization'"));

        return (claim, null);
    }

    /// <summary>
    /// The authenticated caller, from the validated token: <c>sub</c>, else
    /// <c>client_id</c>, else <c>azp</c> (backend clients may carry only
    /// those). JWT claim names only: neither scheme maps inbound claims (D-32),
    /// so a <c>NameIdentifier</c> claim is not the token's subject. Never read
    /// from a header or the body.
    /// </summary>
    private string? CallerId() => AuthenticatedActorId;

    /// <summary>Tracking handle issued at submit and used to inquire later.</summary>
    private static string NewAuthorizationNumber()
        => $"PAS-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static void RecordMetrics(Stopwatch sw, string decision, string? rule)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("pas.decision", decision),
            new("pas.rule", rule ?? "none"),
        };

        ChoMetrics.PasSubmitDuration.Record(sw.Elapsed.TotalSeconds, tags);
        ChoMetrics.PasSubmitDecisions.Add(1, tags);
    }

    // ── Input validation ─────────────────────────────────────────────────────

    private static readonly HashSet<string> AllowedResourceTypes = new(StringComparer.Ordinal)
    {
        "Claim", "Patient", "Coverage", "Practitioner",
        "Organization", "Condition", "Observation", "ServiceRequest",
        "DocumentReference", "QuestionnaireResponse"
    };

    private static readonly Regex RelativeReferencePattern = new(
        @"^[A-Za-z]+/[A-Za-z0-9\-\.]+$", RegexOptions.Compiled);

    private const int MaxBundleEntries = 50;

    private (Claim? claim, IActionResult? error) ValidateAndExtractClaim(Bundle? requestBundle)
    {
        if (requestBundle?.Entry == null || requestBundle.Entry.Count == 0)
        {
            return (null, FhirBadRequest("Request bundle must contain at least one entry"));
        }

        if (requestBundle.Entry.Count > MaxBundleEntries)
        {
            return (null, FhirBadRequest(
                $"Request bundle exceeds maximum of {MaxBundleEntries} entries"));
        }

        // Reject unexpected resource types
        foreach (var entry in requestBundle.Entry)
        {
            if (entry.Resource != null && !AllowedResourceTypes.Contains(entry.Resource.TypeName))
            {
                _logger.LogWarning("PAS $submit rejected unexpected resource type: {Type}",
                    LogSanitizer.Sanitize(entry.Resource.TypeName));
                return (null, FhirBadRequest(
                    $"Bundle contains disallowed resource type: {entry.Resource.TypeName}"));
            }
        }

        var claim = requestBundle.Entry
            .Select(e => e.Resource)
            .OfType<Claim>()
            .FirstOrDefault();

        if (claim == null)
        {
            return (null, FhirBadRequest("Request bundle must contain a Claim resource"));
        }

        if (claim.Provider == null || claim.Patient == null ||
            claim.Insurance == null || claim.Insurance.Count == 0)
        {
            return (null, FhirBadRequest(
                "Claim must include provider, patient, and insurance references"));
        }

        // Ensure references are relative (e.g. "Patient/123"), not absolute URLs,
        // to prevent SSRF when references are forwarded to internal services.
        if (!IsRelativeReference(claim.Patient?.Reference) ||
            !IsRelativeReference(claim.Provider?.Reference) ||
            !IsRelativeReference(claim.Insurer?.Reference))
        {
            return (null, FhirBadRequest(
                "Claim references must be relative FHIR references (e.g. 'Patient/123')"));
        }

        return (claim, null);
    }

    private static bool IsRelativeReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return true; // null/empty handled by required-field checks above
        return RelativeReferencePattern.IsMatch(reference);
    }

    // ── Provider verification ────────────────────────────────────────────────

    private async Task<ProviderVerificationSummary> CheckProviderVerificationAsync(
        string npi, CancellationToken ct)
    {
        try
        {
            var response = await _providerVerificationClient.GetAsync(
                $"api/v1/providers/{Uri.EscapeDataString(npi)}/integrity-score?tier=Basic", ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ProviderIntegrityResponse>(
                    cancellationToken: ct);

                if (result != null)
                {
                    return new ProviderVerificationSummary
                    {
                        IntegrityScore = result.CompositeScore,
                        Rating = result.Rating,
                        IsExcluded = string.Equals(result.Status, "Excluded", StringComparison.OrdinalIgnoreCase),
                        ExclusionSource = result.Flags?
                            .FirstOrDefault(f => f.Code == "EXCLUDED")?.Source,
                        Status = result.Status,
                    };
                }
            }

            _logger.LogWarning(
                "Provider Verification Service returned {StatusCode} for NPI {Npi} — proceeding without verification",
                response.StatusCode, LogSanitizer.Sanitize(npi));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "Provider Verification Service unavailable for NPI {Npi} — proceeding without verification",
                LogSanitizer.Sanitize(npi));
        }

        // Graceful degradation: if verification service is down, proceed without blocking
        return new ProviderVerificationSummary
        {
            IntegrityScore = -1,
            Rating = "Unknown",
            IsExcluded = false,
            Status = "Unavailable",
        };
    }
}
