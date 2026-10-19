using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.ClaimsScrubEngine.Models;
using CloudHealthOffice.ClaimsScrubEngine.Services;
using CloudHealthOffice.NcciEngine.Models;
using CloudHealthOffice.NcciEngine.Services;
using CloudHealthOffice.Infrastructure.Observability;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using BenefitPlanService.Middleware;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;
using CloudHealthOffice.Infrastructure.Logging;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Benefit-plan building blocks of claim adjudication, called over HTTP by
/// the claims-service adjudication pipeline (ClaimAdjudicationOrchestrator),
/// which is the only adjudicator: resolve-rates, calculate-benefits,
/// provider-integrity, commit-accumulators, accumulator-commits/lookup and
/// reverse-claim, plus the standalone ncci-check, scrub-check and
/// validate-provider-enrollment checks. What-if pricing is
/// POST /api/v1/adjudication/estimate (EstimateController).
///
/// The former combined dry run, POST /api/v1/adjudication/adjudicate, was
/// removed: it ran its own engine chain and its answers diverged from the
/// pipeline's (docs/architecture/adjudicate-endpoint-decision.md).
///
/// Every endpoint requires a CHO token; the tenant comes from the token
/// (shared TenantMiddleware). Permissions:
///   - pure calculations that persist nothing (resolve-rates, ncci-check,
///     scrub-check, validate-provider-enrollment) — claims:work or benefits:read;
///   - calculate-benefits — claims:work, because in the default Production
///     execution mode it writes accumulator updates;
///   - reverse-claim — the service default write permission (settings:manage);
///     claims-service calls it with a service token;
///   - commit-accumulators, accumulator-commits/lookup — claims-service's
///     service principal only;
///   - GET provider-integrity — the default read permission (benefits:read).
/// </summary>
[ApiController]
[Route("api/v1/adjudication")]
public class AdjudicationController : ControllerBase
{
    private readonly IClaimRoutingService _scrubEngine;
    private readonly IBenefitCalculationEngine _benefitEngine;
    private readonly IRateResolutionService _rateEngine;
    private readonly INcciEditService _ncciEngine;
    private readonly IEnrollmentDecisionGate _enrollmentGate;
    private readonly IProviderIntegrityGate _providerIntegrityGate;
    private readonly ILogger<AdjudicationController> _logger;

    public AdjudicationController(
        IClaimRoutingService scrubEngine,
        IBenefitCalculationEngine benefitEngine,
        IRateResolutionService rateEngine,
        INcciEditService ncciEngine,
        IEnrollmentDecisionGate enrollmentGate,
        IProviderIntegrityGate providerIntegrityGate,
        ILogger<AdjudicationController> logger)
    {
        _scrubEngine = scrubEngine;
        _benefitEngine = benefitEngine;
        _rateEngine = rateEngine;
        _ncciEngine = ncciEngine;
        _enrollmentGate = enrollmentGate;
        _providerIntegrityGate = providerIntegrityGate;
        _logger = logger;
    }

    private string TenantId => HttpContext.GetTenantId()
        ?? throw new InvalidOperationException("Tenant context missing");

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/calculate-benefits
    //
    // Benefit cost-sharing for priced lines. claims-service's
    // BenefitCalculationStage calls it (Prospective) after PricingStage.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculate benefit cost-sharing for a claim.
    /// Calls the BenefitCalculationEngine with Redis-backed accumulators.
    /// </summary>
    [HttpPost("calculate-benefits")]
    // Writes accumulator updates in Production execution mode, so a benefits reader alone may not call it.
    [RequirePermission("claims:work")]
    [ProducesResponseType(typeof(BenefitResolutionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BenefitResolutionResult>> CalculateBenefits(
        [FromBody] BenefitResolutionRequest request,
        CancellationToken ct)
    {
        if (request.Cob is { } cob
            && CobRequestValidation.Validate(
                cob.PayerSequence, cob.PriorPayers, cob.PrimaryPayerPaymentByLine.Count) is { } cobError)
        {
            ModelState.AddModelError("cob", cobError);
            return ValidationProblem(ModelState);
        }

        _logger.LogInformation(
            "Calculating benefits for member {MemberId}, plan {PlanId}, {LineCount} lines",
            LogSanitizer.MaskId(request.MemberId), request.BenefitPlanId, request.Lines.Count);

        var result = await _benefitEngine.CalculateAsync(request, ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/v1/adjudication/provider-integrity/{npi}
    //
    // Standalone provider-integrity check, side-effect-free. Exposes
    // IProviderIntegrityGate.CheckAsync over HTTP so claims-service's
    // ProviderIntegrityStage can run the federal-exclusion check without
    // going through calculate-benefits (which stays exclusion-check-free
    // by design -- it's also called by portal/preview features that must
    // not be blocked by a live exclusion check on a hypothetical
    // calculation). See docs/architecture/integrity-score-consumption.md.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Check a provider's federal-exclusion integrity status.
    /// Read-only; does not affect any claim or provider record.
    /// </summary>
    [HttpGet("provider-integrity/{npi}")]
    [ProducesResponseType(typeof(ProviderIntegrityResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<ProviderIntegrityResult>> CheckProviderIntegrity(
        string npi,
        CancellationToken ct)
    {
        var result = await _providerIntegrityGate.CheckAsync(npi, TenantId, ct: ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/reverse-claim
    //
    // Capability 5.12a — exposes the existing
    // IBenefitCalculationEngine.ReverseClaimAsync surface over HTTP.
    // The engine method has been wired through
    // ChoAccumulatorService.ReverseAsync with IsReversed=true journaling
    // since BP 5.10; only the HTTP surface was missing. claims-service
    // calls this endpoint via HttpBenefitCalculationEngineClient.
    //
    // Idempotent: the engine's ReverseAsync call is keyed on
    // OriginalClaimId; a second call against the same claim is a no-op.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reverse the accumulator impact of a previously adjudicated claim
    /// (capability 5.12). Idempotent on <c>OriginalClaimId</c>. Returns
    /// 204 No Content on success.
    /// </summary>
    [HttpPost("reverse-claim")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ReverseClaim(
        [FromBody] ReverseClaimRequest request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required" });
        }
        if (string.IsNullOrWhiteSpace(request.MemberId))
        {
            return BadRequest(new { error = "MemberId is required" });
        }
        if (string.IsNullOrWhiteSpace(request.OriginalClaimId))
        {
            return BadRequest(new { error = "OriginalClaimId is required" });
        }
        if (request.BenefitPlanId == Guid.Empty)
        {
            return BadRequest(new { error = "BenefitPlanId is required" });
        }

        _logger.LogInformation(
            "Reversing claim {OriginalClaimId} for member {MemberId}, plan {PlanId}, service date {ServiceDate}",
            LogSanitizer.Sanitize(request.OriginalClaimId, 256),
            LogSanitizer.MaskId(request.MemberId),
            request.BenefitPlanId,
            request.ServiceDate);

        // Terminal (the default): a void, a superseded version — the claim id
        // is fenced. Non-terminal: a Pay claim moved to Deny before the
        // payment cycle, which may be reprocessed back to Pay and commit again.
        if (request.Terminal)
        {
            await _benefitEngine.ReverseClaimAsync(
                request.MemberId,
                request.SubscriberId ?? string.Empty,
                request.BenefitPlanId,
                request.ServiceDate,
                request.OriginalClaimId,
                ct);
        }
        else
        {
            // Commit-scoped: reverses only while CommitId is the claim's
            // applied commit (Superseded otherwise) and fences that commit id.
            var outcome = await _benefitEngine.ReverseClaimForReprocessingAsync(
                request.MemberId,
                request.SubscriberId ?? string.Empty,
                request.BenefitPlanId,
                request.ServiceDate,
                request.OriginalClaimId,
                string.IsNullOrWhiteSpace(request.CommitId) ? null : request.CommitId,
                ct);
            return Ok(new ReverseClaimResponse { Outcome = outcome });
        }

        return NoContent();
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/commit-accumulators
    //
    // Writes the accumulator updates a Prospective calculate-benefits call
    // prepared (BenefitResolutionResult.PreparedAccumulatorCommit), once the
    // claim is finally adjudicated. claims-service prices every claim
    // Prospective and commits only a claim that passes: a pended claim
    // writes nothing, an examiner approval commits after its lock-fenced
    // final write. Same permission as reverse-claim (the service default
    // write permission; claims-service calls it with a service token).
    // Only claims-service's service principal may call it: a commit adds
    // to a member's accumulators, so no user token — whatever its
    // permissions — and no other service can.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>claims-service's service client id (its ServiceToken:ClientId).</summary>
    public const string ClaimsServiceClientId = "claims-service";

    /// <summary>
    /// Commit a prepared accumulator write. Idempotent on <c>CommitId</c>.
    /// Returns the outcome; <c>RefusedClaimReversed</c> when the claim was
    /// voided or denied (its accumulators reversed terminally) first, and
    /// every update the store clamped at a limit.
    /// </summary>
    [HttpPost("commit-accumulators")]
    [RequireServiceClient(ClaimsServiceClientId)]
    [ProducesResponseType(typeof(AccumulatorCommitResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AccumulatorCommitResponse>> CommitAccumulators(
        [FromBody] AccumulatorCommit commit,
        CancellationToken ct)
    {
        if (commit is null)
            return BadRequest(new { error = "Request body is required" });
        if (string.IsNullOrWhiteSpace(commit.CommitId) || string.IsNullOrWhiteSpace(commit.ClaimId)
            || string.IsNullOrWhiteSpace(commit.MemberId) || string.IsNullOrWhiteSpace(commit.PlanYear)
            || commit.BenefitPlanId == Guid.Empty)
            return BadRequest(new { error = "CommitId, ClaimId, MemberId, BenefitPlanId and PlanYear are required" });
        if (commit.Updates.Any(u => u.Amount < 0))
            return BadRequest(new { error = "A commit never carries a negative amount" });

        var caller = ChoPrincipal.ServiceClientId(User) ?? "unknown";
        var result = await _benefitEngine.CommitAccumulatorsAsync(
            commit with { SubscriberId = string.IsNullOrWhiteSpace(commit.SubscriberId) ? commit.MemberId : commit.SubscriberId },
            ct);
        // Audit: who wrote what to whose accumulators (ids only, no amounts).
        _logger.LogInformation(
            "AUDIT accumulator commit {CommitId} for claim {ClaimId} by service {Caller} (tenant {TenantId}): {Outcome}, {Updates} update(s), {Clamped} clamped",
            LogSanitizer.Sanitize(commit.CommitId, 256), LogSanitizer.Sanitize(commit.ClaimId, 256), LogSanitizer.Sanitize(caller, 256), LogSanitizer.Sanitize(TenantId, 256),
            result.Outcome, commit.Updates.Count, result.Clamped.Count);
        return Ok(new AccumulatorCommitResponse { Outcome = result.Outcome, Clamped = result.Clamped });
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/accumulator-commits/lookup
    //
    // Read-only: which of the given claims the accumulator store holds a
    // write record for. claims-service's post-rollout sweep lists finalized
    // claims with no record (and no outbox entry). claims-service only.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Most claims one lookup accepts.</summary>
    public const int MaxCommitLookups = 500;

    /// <summary>Which claims have an accumulator write record in the store.</summary>
    [HttpPost("accumulator-commits/lookup")]
    [RequireServiceClient(ClaimsServiceClientId)]
    [ProducesResponseType(typeof(List<AccumulatorCommitLookupResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<AccumulatorCommitLookupResult>>> LookupAccumulatorCommits(
        [FromBody] List<AccumulatorCommitLookup> claims,
        [FromServices] IAccumulatorService accumulators,
        CancellationToken ct)
    {
        if (claims is null || claims.Count == 0)
            return BadRequest(new { error = "At least one claim is required" });
        if (claims.Count > MaxCommitLookups)
            return BadRequest(new { error = $"At most {MaxCommitLookups} claims per lookup" });
        if (claims.Any(c => string.IsNullOrWhiteSpace(c.ClaimId) || string.IsNullOrWhiteSpace(c.MemberId)
                            || c.BenefitPlanId == Guid.Empty || string.IsNullOrWhiteSpace(c.PlanYear)))
            return BadRequest(new { error = "ClaimId, MemberId, BenefitPlanId and PlanYear are required" });

        var results = new List<AccumulatorCommitLookupResult>(claims.Count);
        foreach (var c in claims)
        {
            var subscriber = string.IsNullOrWhiteSpace(c.SubscriberId) ? c.MemberId : c.SubscriberId!;
            results.Add(new AccumulatorCommitLookupResult
            {
                ClaimId = c.ClaimId,
                Recorded = await accumulators.HasClaimRecordAsync(c.MemberId, subscriber, c.BenefitPlanId, c.PlanYear, c.ClaimId, ct),
            });
        }
        return Ok(results);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/resolve-rates
    //
    // Rate resolution for claim lines. claims-service's PricingStage
    // calls it.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve allowed rates for claim lines using the Fee Schedule Engine.
    /// Looks up provider contracts, fee schedules, and applies modifier adjustments.
    /// </summary>
    [HttpPost("resolve-rates")]
    [RequirePermission("claims:work,benefits:read")]
    [ProducesResponseType(typeof(PricingResultSet), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PricingResultSet>> ResolveRates(
        [FromBody] List<PricingRequest> requests,
        CancellationToken ct)
    {
        _logger.LogInformation("Resolving rates for {LineCount} claim lines", requests.Count);

        // Inject tenant ID into each request
        var tenantedRequests = requests.Select(r => r with { TenantId = TenantId }).ToList();

        var result = await _rateEngine.ResolveBatchAsync(tenantedRequests, ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/ncci-check
    //
    // Standalone NCCI/MUE pre-payment edit check.
    // Use when you want to validate a claim before full adjudication.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run NCCI Column 1/Column 2 and MUE edits on a claim.
    /// Returns the scrub result indicating whether the claim passed.
    /// </summary>
    [HttpPost("ncci-check")]
    [RequirePermission("claims:work,benefits:read")]
    [ProducesResponseType(typeof(NcciScrubResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NcciScrubResult>> NcciCheck(
        [FromBody] NcciScrubRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Running NCCI/MUE check for claim {ClaimId}, {LineCount} lines",
            LogSanitizer.Sanitize(request.ClaimId, 256), request.ServiceLines.Count);

        // Inject tenant ID from middleware
        request.TenantId = TenantId;

        var result = await _ncciEngine.ScrubAsync(request, ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/scrub-check
    //
    // Standalone claims scrub validation.
    // Use when you want to run scrub rules without full adjudication.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Run claims scrub validation rules on a claim.
    /// Returns the validation result with routing decision.
    /// </summary>
    [HttpPost("scrub-check")]
    [RequirePermission("claims:work,benefits:read")]
    [ProducesResponseType(typeof(ClaimsScrubResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClaimsScrubResponse>> ScrubCheck(
        [FromBody] ClaimsScrubRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Running scrub validation for claim {ClaimId}",
            LogSanitizer.Sanitize(request.Claim.ClaimId, 256));

        var result = await _scrubEngine.ScrubAndRouteAsync(request, ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/validate-provider-enrollment
    //
    // Called by Argo workflow Step 3 (validate-provider) in parallel with
    // verify-coverage and validate-codes. Replaces the Python inline script
    // that only checked credentialingStatus and providerStatus.
    // ═══════════════════════════════════════════════════════════════════

    [HttpPost("validate-provider-enrollment")]
    [RequirePermission("claims:work,benefits:read")]
    [ProducesResponseType(typeof(BenefitPlanService.Models.ProviderEnrollmentValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BenefitPlanService.Models.ProviderEnrollmentValidationResponse>> ValidateProviderEnrollment(
        [FromBody] BenefitPlanService.Models.ProviderEnrollmentValidationRequest request,
        CancellationToken ct)
    {
        using var span = ChoActivitySource.StartActivity(
            "claim.validate-provider-enrollment",
            System.Diagnostics.ActivityKind.Internal,
            tenantId: TenantId,
            claimId: request.ClaimId);

        if (string.IsNullOrEmpty(request.ProviderNpi))
            return BadRequest("ProviderNpi is required.");

        // Map string LOB from the Argo workflow payload to the enrollment service enum
        var lob = request.LineOfBusiness?.ToUpperInvariant() switch
        {
            "MEDICAID"    => LineOfBusiness.Medicaid,
            "STAR"        => LineOfBusiness.STAR,
            "STARPLUS"    => LineOfBusiness.STARPlus,
            "STARKIDS"    => LineOfBusiness.STARKids,
            "CHIP"        => LineOfBusiness.CHIP,
            "MARKETPLACE" or "EXCHANGE" => LineOfBusiness.Marketplace,
            "MEDICARE"    => LineOfBusiness.Medicare,
            _             => LineOfBusiness.None
        };

        var gateResult = await _enrollmentGate.EvaluateAsync(
            npi:         request.ProviderNpi,
            taxonomy:    request.ProviderTaxonomy ?? string.Empty,
            stateCode:   request.StateCode ?? "TX",
            serviceDate: request.ServiceDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            lob:         lob,
            ct:          ct);

        span?.SetTag("cho.enrollment.passed",      gateResult.Passed);
        span?.SetTag("cho.enrollment.denial_code", gateResult.DenialCode ?? string.Empty);

        _logger.LogInformation(
            "Provider enrollment validation: NPI={Npi} State={State} LOB={Lob} Passed={Passed}",
            LogSanitizer.Sanitize(request.ProviderNpi, 256),
            LogSanitizer.Sanitize(request.StateCode, 256),
            LogSanitizer.Sanitize(request.LineOfBusiness, 256),
            gateResult.Passed);

        return Ok(new BenefitPlanService.Models.ProviderEnrollmentValidationResponse
        {
            ClaimId    = request.ClaimId,
            Status     = gateResult.Passed ? "APPROVED" : "DENIED",
            DenialCode = gateResult.DenialCode,
            Reason     = gateResult.DenialReason,
            // CARC 185: Provider not enrolled in Medicaid
            Carc       = gateResult.Passed ? null : "185"
        });
    }
}

/// <summary>
/// Shape checks for a COB request on the adjudication APIs: a later payer
/// (sequence ≥ 2) needs what the earlier payers did, or the engine would
/// price the claim as if they paid nothing.
/// </summary>
public static class CobRequestValidation
{
    /// <summary>Null when valid; otherwise the validation message.</summary>
    public static string? Validate(
        int payerSequence,
        IReadOnlyCollection<CloudHealthOffice.CobEngine.Domain.PriorPayerAdjudication>? priorPayers,
        int legacyPrimaryLineCount)
    {
        if (payerSequence < 1)
            return $"cob.payerSequence must be 1 (primary) or more; got {payerSequence}.";
        if (payerSequence < 2)
            return null;

        var earlier = (priorPayers ?? []).Where(p => p.Sequence >= 1 && p.Sequence < payerSequence).ToList();
        if (earlier.Count == 0)
        {
            // The secondary-only legacy input (primary payment by line) is
            // still accepted for payer sequence 2.
            if (payerSequence == 2 && legacyPrimaryLineCount > 0)
                return null;
            return $"cob.payerSequence {payerSequence} requires cob.priorPayers: the adjudication (2320/2430) of " +
                   $"each payer sequenced before it (1..{payerSequence - 1}).";
        }
        var duplicate = earlier.GroupBy(p => p.Sequence).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return $"cob.priorPayers lists payer sequence {duplicate.Key} more than once.";
        // Every earlier payer, not just some: a tertiary claim with only the
        // primary's adjudication would be priced as if the secondary paid
        // nothing (PR #1278 re-review).
        var missing = Enumerable.Range(1, payerSequence - 1).Where(s => earlier.All(p => p.Sequence != s)).ToList();
        if (missing.Count > 0)
            return $"cob.payerSequence {payerSequence} requires the adjudication of every earlier payer " +
                   $"(1..{payerSequence - 1}); cob.priorPayers is missing sequence {string.Join(", ", missing)}.";
        return null;
    }
}

/// <summary>Response of <c>POST /api/v1/adjudication/commit-accumulators</c>.</summary>
public class AccumulatorCommitResponse
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccumulatorCommitOutcome Outcome { get; set; }

    /// <summary>Updates the store wrote for less than requested (a limit was reached at write time).</summary>
    public List<AccumulatorClamp> Clamped { get; set; } = [];
}

/// <summary>One claim of <c>POST /api/v1/adjudication/accumulator-commits/lookup</c>.</summary>
public class AccumulatorCommitLookup
{
    public string ClaimId { get; set; } = string.Empty;
    public string MemberId { get; set; } = string.Empty;
    public string? SubscriberId { get; set; }
    public Guid BenefitPlanId { get; set; }
    public string PlanYear { get; set; } = string.Empty;
}

public class AccumulatorCommitLookupResult
{
    public string ClaimId { get; set; } = string.Empty;
    public bool Recorded { get; set; }
}

/// <summary>
/// Wire payload for <c>POST /api/v1/adjudication/reverse-claim</c>
/// (capability 5.12). Mirrors the
/// <see cref="IBenefitCalculationEngine.ReverseClaimAsync"/> signature
/// directly so the controller is a thin adapter over the engine call.
/// </summary>
public class ReverseClaimRequest
{
    public string MemberId { get; set; } = string.Empty;
    public string? SubscriberId { get; set; }
    public Guid BenefitPlanId { get; set; }
    public DateOnly ServiceDate { get; set; }
    public string OriginalClaimId { get; set; } = string.Empty;

    /// <summary>
    /// Default true (and when absent, as from older callers): fence the claim
    /// id so a later commit of it is refused (void / replacement). False:
    /// reverse only — the claim may commit again (Pay → Deny before the
    /// payment cycle, then reprocessed).
    /// </summary>
    public bool Terminal { get; set; } = true;

    /// <summary>
    /// Non-terminal only: the commit being reversed (compare-and-reverse).
    /// Null / empty: unknown (legacy) — reverse whatever the claim holds.
    /// </summary>
    public string? CommitId { get; set; }
}

/// <summary>Response of a non-terminal <c>POST /api/v1/adjudication/reverse-claim</c>.</summary>
public class ReverseClaimResponse
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AccumulatorReversalOutcome Outcome { get; set; }
}
