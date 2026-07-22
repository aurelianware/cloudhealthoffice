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
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using CloudHealthOffice.ProviderEnrollmentService.Abstractions;
using CloudHealthOffice.ProviderEnrollmentService.Models;
using BenefitPlanService.Middleware;
using BenefitPlanService.Services;
using Microsoft.AspNetCore.Mvc;

namespace BenefitPlanService.Controllers;

/// <summary>
/// Adjudication endpoints called by the Argo claims-adjudication workflow.
///
/// These replace the inline Python scripts in the workflow steps with C#
/// endpoints that call the BenefitCalculationEngine and FeeScheduleEngine
/// directly via DI — no serialization overhead, full access to Redis
/// accumulators, fee schedule lookups, and service category resolution.
///
/// Argo workflow step mapping:
///   Step 6 (get-benefits) + Step 8 (calculate-cost-sharing)
///     → POST /api/v1/adjudication/calculate-benefits
///     → Calls IBenefitCalculationEngine.CalculateAsync()
///
///   Step 7 (get-rates)
///     → POST /api/v1/adjudication/resolve-rates
///     → Calls IRateResolutionService.ResolveBatchAsync()
///
///   Combined (single call replaces steps 6+7+8):
///     → POST /api/v1/adjudication/adjudicate
///     → Calls both engines and returns a merged result
///
/// All endpoints expect X-Tenant-ID header (set by TenantMiddleware).
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
    private readonly IPriorAuthRuleEngine _priorAuthEngine;
    private readonly IProviderIntegrityGate _providerIntegrityGate;
    private readonly ITerminologyCrosswalkClient _terminologyClient;
    private readonly IOperatingModeProvider _operatingModeProvider;
    private readonly IClaimTypeRouter _claimTypeRouter;
    private readonly ILogger<AdjudicationController> _logger;

    public AdjudicationController(
        IClaimRoutingService scrubEngine,
        IBenefitCalculationEngine benefitEngine,
        IRateResolutionService rateEngine,
        INcciEditService ncciEngine,
        IEnrollmentDecisionGate enrollmentGate,
        IPriorAuthRuleEngine priorAuthEngine,
        IProviderIntegrityGate providerIntegrityGate,
        ITerminologyCrosswalkClient terminologyClient,
        IOperatingModeProvider operatingModeProvider,
        IClaimTypeRouter claimTypeRouter,
        ILogger<AdjudicationController> logger)
    {
        _scrubEngine = scrubEngine;
        _benefitEngine = benefitEngine;
        _rateEngine = rateEngine;
        _ncciEngine = ncciEngine;
        _enrollmentGate = enrollmentGate;
        _priorAuthEngine = priorAuthEngine;
        _providerIntegrityGate = providerIntegrityGate;
        _terminologyClient = terminologyClient;
        _operatingModeProvider = operatingModeProvider;
        _claimTypeRouter = claimTypeRouter;
        _logger = logger;
    }

    private string TenantId => HttpContext.GetTenantId()
        ?? throw new InvalidOperationException("Tenant context missing");

    /// <summary>
    /// Replace control characters (CR, LF, tab, etc.) with underscore so
    /// caller-controlled string values cannot forge log lines or split a
    /// single log entry into multiple. Mirrors
    /// <c>RelationshipShim.SanitizeForLog</c> in member-service so CodeQL's
    /// cs/log-forging rule recognizes it as a sanitizer (the prior
    /// CR/LF-only Replace pattern was not picked up as a sanitizer).
    /// </summary>
    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            buffer.Append(char.IsControl(ch) ? '_' : ch);
        }
        if (buffer.Length > 256) buffer.Length = 256;
        return buffer.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/adjudicate
    //
    // The "one call to rule them all" — replaces Argo steps 6, 7, and 8.
    // Takes claim data + provider/coverage context, returns fully
    // adjudicated result with allowed amounts, cost sharing, CAS segments.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full adjudication: pricing + benefit calculation in one call.
    /// Replaces the get-rates, get-benefits, and calculate-cost-sharing
    /// workflow steps with a single HTTP round-trip.
    /// </summary>
    [HttpPost("adjudicate")]
    [ProducesResponseType(typeof(AdjudicationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AdjudicationResponse>> Adjudicate(
        [FromBody] AdjudicationRequest request,
        CancellationToken ct)
    {
        var claimTypeCode = NormalizeClaimType(request.ClaimType);

        using var adjudicationSpan = ChoActivitySource.StartActivity(
            "claim.adjudication",
            ActivityKind.Internal,
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId);

        adjudicationSpan?.SetTag("cho.benefit_plan_id", request.BenefitPlanId.ToString());
        adjudicationSpan?.SetTag("cho.line_count", request.Lines.Count);
        adjudicationSpan?.SetTag("cho.claim_type", request.ClaimType);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stageTimings = new Dictionary<string, double>(StringComparer.Ordinal);

        async Task<T> MeasureStageAsync<T>(string stage, Func<Task<T>> action)
        {
            var stageWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                return await action();
            }
            finally
            {
                stageWatch.Stop();
                stageTimings[stage] = stageWatch.Elapsed.TotalMilliseconds;
            }
        }

        // ── Routing decision: determine operating mode for this claim type/LOB ──
        var operatingModeConfig = await MeasureStageAsync(
            "routing",
            () => _operatingModeProvider.GetConfigurationAsync(TenantId, ct));
        var routingDecision = _claimTypeRouter.Route(
            operatingModeConfig, request.ClaimType, request.LineOfBusiness);

        adjudicationSpan?.SetTag("cho.routing.route", routingDecision.Route.ToString());
        adjudicationSpan?.SetTag("cho.routing.key", routingDecision.ResolvedKey);
        adjudicationSpan?.SetTag("cho.operating_mode",
            routingDecision.Route == AdjudicationRoute.LegacyOnly
                ? "LegacyOnly"
                : routingDecision.OperatingMode.Mode.ToString());

        // If routed to legacy only, return immediately — CHO does not process this claim type.
        if (routingDecision.Route == AdjudicationRoute.LegacyOnly)
        {
            RecordLatency(sw, claimTypeCode, "legacy_routed");

            _logger.LogInformation(
                "Claim {ClaimId} routed to legacy system (type={ClaimType}, LOB={Lob}, key={Key})",
                SanitizeForLog(request.ClaimId), SanitizeForLog(claimTypeCode),
                request.LineOfBusiness, SanitizeForLog(routingDecision.ResolvedKey));

            return Ok(new AdjudicationResponse
            {
                ClaimId = request.ClaimId,
                Success = false,
                DenialReasonCode = "LEGACY_ROUTED",
                DenialReasonDescription = $"Claim type {request.ClaimType} routed to legacy system per tenant configuration",
                OperatingMode = "LegacyOnly",
                RoutingKey = routingDecision.ResolvedKey,
                IsAuthoritative = false,
                Timings = stageTimings
            });
        }

        _logger.LogInformation(
            "Adjudicating claim {ClaimId} for member {MemberId}, plan {PlanId}, {LineCount} lines (type={ClaimType}, mode={Mode})",
            SanitizeForLog(request.ClaimId), SanitizeForLog(request.MemberId),
            request.BenefitPlanId, request.Lines.Count, SanitizeForLog(claimTypeCode),
            routingDecision.OperatingMode.Mode);

        // ── Step 0a: Claims scrub validation ──
        ClaimsScrubResponse scrubResponse;
        using (var scrubSpan = ChoActivitySource.StartActivity(
            "claim.scrub",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            var scrubClaim = MapToScrubClaim(request);
            scrubResponse = await MeasureStageAsync(
                "scrub",
                () => _scrubEngine.ScrubAndRouteAsync(new ClaimsScrubRequest { Claim = scrubClaim }, ct));

            scrubSpan?.SetTag("cho.scrub.passed", scrubResponse.Result.Routing.Destination == "adjudication");
            scrubSpan?.SetTag("cho.scrub.error_count", scrubResponse.Result.ErrorCount);
            scrubSpan?.SetTag("cho.scrub.warning_count", scrubResponse.Result.WarningCount);
        }

        if (scrubResponse.Result.Routing.Destination != "adjudication")
        {
            adjudicationSpan?.SetTag("cho.outcome", "scrub_failure");
            adjudicationSpan?.SetStatus(ActivityStatusCode.Error, "Scrub validation failed");

            RecordLatency(sw, claimTypeCode, "scrub_failure");

            _logger.LogWarning(
                "Claim {ClaimId} failed scrub validation: {ErrorCount} error(s), {WarningCount} warning(s)",
                SanitizeForLog(request.ClaimId), scrubResponse.Result.ErrorCount, scrubResponse.Result.WarningCount);

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "SCRUB_VALIDATION_FAILURE",
                message = $"Claim failed scrub validation with {scrubResponse.Result.ErrorCount} error(s) " +
                          $"and {scrubResponse.Result.WarningCount} warning(s). " +
                          scrubResponse.Result.Routing.Reason,
                status = scrubResponse.Result.Status,
                routing = scrubResponse.Result.Routing,
                validationResults = scrubResponse.Result.Results.Where(r => !r.Passed).ToList(),
                timings = stageTimings,
            });
        }

        // ── Step 0b: NCCI/MUE pre-payment edit check ──
        NcciScrubResult ncciResult;
        using (var ncciSpan = ChoActivitySource.StartActivity(
            "claim.ncci",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            var ncciRequest = new NcciScrubRequest
            {
                TenantId = TenantId,
                ClaimId = request.ClaimId,
                ClaimType = claimTypeCode,
                EffectiveDate = request.ServiceDate,
                ServiceLines = request.Lines.Select(l => new ClaimServiceLine
                {
                    LineNumber = l.LineNumber,
                    ProcedureCode = l.ProcedureCode,
                    Modifiers = l.Modifiers,
                    Units = l.Units,
                    ServiceDate = request.ServiceDate,
                    PlaceOfServiceCode = l.PlaceOfService,
                }).ToList(),
            };

            ncciResult = await MeasureStageAsync(
                "ncci",
                () => _ncciEngine.ScrubAsync(ncciRequest, ct));

            ncciSpan?.SetTag("cho.ncci.passed", ncciResult.Passed);
            ncciSpan?.SetTag("cho.ncci.failure_count", ncciResult.EditFailures.Count);
        }

        if (!ncciResult.Passed)
        {
            adjudicationSpan?.SetTag("cho.outcome", "ncci_failure");
            adjudicationSpan?.SetStatus(ActivityStatusCode.Error, "NCCI/MUE edit failed");

            RecordLatency(sw, claimTypeCode, "ncci_failure");

            _logger.LogWarning(
                "Claim {ClaimId} failed NCCI/MUE edits: {FailureCount} failure(s)",
                SanitizeForLog(request.ClaimId), ncciResult.EditFailures.Count);

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "NCCI_MUE_EDIT_FAILURE",
                message = $"Claim failed {ncciResult.EditFailures.Count} NCCI/MUE edit(s). " +
                          "Review edit failures and resubmit or override.",
                editFailures = ncciResult.EditFailures,
                timings = stageTimings,
            });
        }

        // ── Step 0c: Provider integrity verification (OIG/LEIE/SAM.gov) ──
        ProviderIntegrityResult? providerIntegrity = null;
        using (var integritySpan = ChoActivitySource.StartActivity(
            "claim.provider-integrity",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            providerIntegrity = await MeasureStageAsync(
                "providerIntegrity",
                () => _providerIntegrityGate.CheckAsync(request.ProviderNpi, TenantId, ct: ct));

            integritySpan?.SetTag("cho.integrity.passed", providerIntegrity.Passed);
            integritySpan?.SetTag("cho.integrity.score", providerIntegrity.IntegrityScore ?? -1);
            integritySpan?.SetTag("cho.integrity.excluded", providerIntegrity.IsExcluded);
        }

        if (providerIntegrity.IsExcluded)
        {
            adjudicationSpan?.SetTag("cho.outcome", "provider_excluded");
            adjudicationSpan?.SetStatus(ActivityStatusCode.Error, "Provider excluded from federal programs");

            RecordLatency(sw, claimTypeCode, "provider_excluded");

            _logger.LogWarning(
                "Claim {ClaimId} denied: provider NPI {Npi} excluded from federal programs (integrity score: {Score})",
                SanitizeForLog(request.ClaimId), SanitizeForLog(request.ProviderNpi),
                providerIntegrity.IntegrityScore);

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "PROVIDER_EXCLUDED",
                message = providerIntegrity.DenialReason ?? "Provider excluded from federal healthcare programs",
                carc = providerIntegrity.DenialCode,
                integrityScore = providerIntegrity.IntegrityScore,
                rating = providerIntegrity.Rating,
                timings = stageTimings,
            });
        }

        if (!providerIntegrity.Passed)
        {
            // Not a confirmed exclusion -- either RequiresManualReview (verification
            // unavailable or inconclusive) or a defensive Passed=false with neither
            // flag set. Either way this must not be reported as PROVIDER_EXCLUDED:
            // the provider hasn't actually appeared on an exclusion list.
            adjudicationSpan?.SetTag("cho.outcome", "provider_verification_review_required");
            adjudicationSpan?.SetStatus(
                ActivityStatusCode.Error, "Provider integrity could not be confidently verified");

            RecordLatency(sw, claimTypeCode, "provider_verification_review_required");

            _logger.LogWarning(
                "Claim {ClaimId} pended: provider NPI {Npi} integrity could not be confidently verified",
                SanitizeForLog(request.ClaimId), SanitizeForLog(request.ProviderNpi));

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = providerIntegrity.DenialCode ?? "PROVIDER_VERIFICATION_UNAVAILABLE",
                message = providerIntegrity.DenialReason
                    ?? "Provider integrity could not be confidently verified; manual review required",
                integrityScore = providerIntegrity.IntegrityScore,
                rating = providerIntegrity.Rating,
                timings = stageTimings,
            });
        }

        if (!IsMemberEligibleForServiceDate(request, out var eligibilityReason))
        {
            adjudicationSpan?.SetTag("cho.outcome", "member_not_eligible");
            adjudicationSpan?.SetStatus(ActivityStatusCode.Error, eligibilityReason);

            RecordLatency(sw, claimTypeCode, "member_not_eligible");

            var serviceDateForLog = SanitizeForLog(
                request.ServiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            _logger.LogWarning(
                "Claim {ClaimId} denied: member {MemberId} not eligible on service date {ServiceDate}: {Reason}",
                SanitizeForLog(request.ClaimId), SanitizeForLog(request.MemberId),
                serviceDateForLog, SanitizeForLog(eligibilityReason));

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "CARC_27",
                message = eligibilityReason,
                carc = "27",
                timings = stageTimings,
            });
        }

        // ── Step 0d: Prior auth rule evaluation ──
        // Evaluates whether the procedures on this claim require prior authorization
        // and whether the provided auth (if any) satisfies the requirement.
        PaRuleDecision? priorAuthDecision = null;
        using (var paSpan = ChoActivitySource.StartActivity(
            "claim.prior-auth-eval",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            var paContext = new PaRuleContext
            {
                TenantId = TenantId,
                StateCode = request.StateCode ?? "TX",
                Lob = MapToPaLineOfBusiness(request.LineOfBusiness),
                Program = MapToPaProgram(request.LineOfBusiness),
                RequestingProviderNpi = request.ProviderNpi,
                ServicingProviderNpi = request.ProviderNpi,
                ServicingProviderTaxonomy = request.ProviderTaxonomy,
                MemberId = request.MemberId,
                ServiceDate = request.ServiceDate,
                ProcedureCodes = request.Lines.Select(l => l.ProcedureCode).Distinct().ToList(),
                DiagnosisCodes = request.Lines.SelectMany(l => l.DiagnosisCodes).Distinct().ToList(),
                PlaceOfServiceCode = request.Lines.Select(l => l.PlaceOfService).FirstOrDefault(),
                EstimatedCost = request.Lines.Sum(l => l.BilledAmount),
            };

            priorAuthDecision = await MeasureStageAsync(
                "priorAuth",
                () => _priorAuthEngine.EvaluateAsync(paContext, ct));

            paSpan?.SetTag("cho.pa.outcome", priorAuthDecision.Outcome.ToString());
            paSpan?.SetTag("cho.pa.rule_id", priorAuthDecision.FiringRuleId);
        }

        // If PA rule engine says auth/review is required and no auth exists,
        // deny the claim unless the provider already has an authorization on file.
        if (priorAuthDecision.IsPriorAuthRequired()
            && string.IsNullOrEmpty(request.PriorAuthorizationNumber))
        {
            adjudicationSpan?.SetTag("cho.outcome", "pa_denied");
            adjudicationSpan?.SetStatus(ActivityStatusCode.Error, "Prior authorization required but not provided");

            RecordLatency(sw, claimTypeCode, "pa_denied");

            _logger.LogWarning(
                "Claim {ClaimId} denied: prior authorization required by rule {RuleId} ({RuleName})",
                SanitizeForLog(request.ClaimId),
                priorAuthDecision.FiringRuleId, priorAuthDecision.FiringRuleName);

            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "PRIOR_AUTH_REQUIRED",
                message = priorAuthDecision.DenialReason ?? "Prior authorization required but not provided",
                carc = priorAuthDecision.DenialCode ?? "197",
                firingRule = priorAuthDecision.FiringRuleName,
                ruleSetKey = priorAuthDecision.ResolvedRuleSetKey,
                timings = stageTimings,
            });
        }

        // ── Step 0e: Terminology crosswalk (plan-specific code mappings) ──
        // Resolves plan-specific procedure code overrides before pricing.
        // Essential for TX Medicaid rate accuracy where plan codes differ from standard CPT.
        var crosswalkResults = await MeasureStageAsync(
            "terminology",
            () => _terminologyClient.TranslateBatchAsync(
                TenantId,
                request.Lines.Select(l => new CodeCrosswalkRequest
                {
                    LineNumber = l.LineNumber,
                    ProcedureCode = l.ProcedureCode,
                    CodeType = l.CodeType ?? "CPT"
                }).ToList(),
                ct));

        var crosswalkMap = crosswalkResults.ToDictionary(r => r.LineNumber, r => r.ResolvedCode);

        // ── Step 1: Resolve rates (fee schedule engine) ──
        // Uses crosswalk-resolved codes when available (Gap 4: TerminologyService).
        PricingResultSet pricingResults;
        using (var rateSpan = ChoActivitySource.StartActivity(
            "claim.rate-resolution",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            var pricingRequests = request.Lines.Select(line => new PricingRequest
            {
                TenantId = TenantId,
                ProcedureCode = crosswalkMap.GetValueOrDefault(line.LineNumber, line.ProcedureCode),
                Modifiers = line.Modifiers,
                ProviderNpi = request.ProviderNpi,
                PlaceOfServiceCode = line.PlaceOfService,
                ServiceDate = request.ServiceDate.ToDateTime(TimeOnly.MinValue),
                PlanId = request.BenefitPlanId.ToString(),
                BilledAmount = line.BilledAmount,
                Units = line.Units,
                LineNumber = line.LineNumber
            }).ToList();

            pricingResults = await MeasureStageAsync(
                "rateResolution",
                () => _rateEngine.ResolveBatchAsync(pricingRequests, ct));

            rateSpan?.SetTag("cho.rate.line_count", pricingResults.LineResults.Count);
        }

        // ── Step 2: Build benefit request with allowed amounts from pricing ──
        // Uses CalculateWithModeAsync when operating in Augment mode (Gap 1).
        BenefitResolutionResult benefitResult;
        string[] augmentDiscrepancies = [];
        using (var benefitSpan = ChoActivitySource.StartActivity(
            "claim.benefit-calc",
            tenantId: TenantId,
            claimId: request.ClaimId,
            claimType: claimTypeCode,
            memberId: request.MemberId))
        {
            var benefitLines = request.Lines.Select(line =>
            {
                var priced = pricingResults.LineResults
                    .FirstOrDefault(p => p.LineNumber == line.LineNumber);

                return new ClaimLineInput
                {
                    LineNumber = line.LineNumber,
                    ProcedureCode = crosswalkMap.GetValueOrDefault(line.LineNumber, line.ProcedureCode),
                    CodeType = line.CodeType,
                    Modifiers = line.Modifiers,
                    RevenueCode = line.RevenueCode,
                    PlaceOfService = line.PlaceOfService,
                    BilledAmount = priced?.AllowedAmount ?? line.BilledAmount,
                    Units = line.Units,
                    DiagnosisCodes = line.DiagnosisCodes
                };
            }).ToList();

            var benefitRequest = new BenefitResolutionRequest
            {
                MemberId = request.MemberId,
                SubscriberId = request.SubscriberId,
                BenefitPlanId = request.BenefitPlanId,
                ServiceDate = request.ServiceDate,
                NetworkTier = request.NetworkTier,
                LineOfBusiness = request.LineOfBusiness,
                ClaimId = request.ClaimId,
                Lines = benefitLines,
                Cob = request.Cob is null ? null : new CobInfo
                {
                    PayerSequence              = request.Cob.PayerSequence,
                    UseComplementaryModel      = request.Cob.UseComplementaryModel,
                    PrimaryPayerId             = request.Cob.PrimaryPayerId,
                    PrimaryPayerName           = request.Cob.PrimaryPayerName,
                    PrimaryPayerPaymentByLine  = request.Cob.PrimaryPayerPaymentByLine,
                    PrimaryAllowedByLine       = request.Cob.PrimaryAllowedByLine
                }
            };

            // Use mode-aware calculation when in Augment mode to capture discrepancies
            var augmentResult = await MeasureStageAsync(
                "benefitCalculation",
                () => _benefitEngine.CalculateWithModeAsync(
                    benefitRequest,
                    routingDecision.OperatingMode,
                    TenantId,
                    legacyResult: null, // Legacy result injected by workflow when available
                    ct));

            benefitResult = augmentResult.ChoResult;
            augmentDiscrepancies = augmentResult.Discrepancies;
            foreach (var timing in benefitResult.Timings)
            {
                stageTimings[$"benefitCalculation.{timing.Key}"] = timing.Value;
            }

            benefitSpan?.SetTag("cho.benefit.success", benefitResult.Success);
            benefitSpan?.SetTag("cho.benefit.authoritative", augmentResult.Authoritative);
            benefitSpan?.SetTag("cho.benefit.discrepancy_count", augmentDiscrepancies.Length);
            if (benefitResult.DenialReasonCode is not null)
                benefitSpan?.SetTag("cho.benefit.denial_code", benefitResult.DenialReasonCode);
        }

        // ── Step 2b: COB (if applicable) ──
        if (request.Cob is not null)
        {
            using var cobSpan = ChoActivitySource.StartActivity(
                "claim.cob",
                tenantId: TenantId,
                claimId: request.ClaimId,
                claimType: claimTypeCode,
                memberId: request.MemberId);

            cobSpan?.SetTag("cho.cob.payer_sequence", request.Cob.PayerSequence);
            cobSpan?.SetTag("cho.cob.model", request.Cob.UseComplementaryModel ? "complementary" : "non-duplication");
            // COB reduction is already applied by the benefit engine when Cob is provided.
            // This span captures that the COB path was taken for trace visibility.
        }

        // ── Step 3: Merge pricing + benefit results ──
        var response = new AdjudicationResponse
        {
            ClaimId = request.ClaimId,
            Success = benefitResult.Success,
            DenialReasonCode = benefitResult.DenialReasonCode,
            DenialReasonDescription = benefitResult.DenialReasonDescription,
            OperatingMode = routingDecision.OperatingMode.Mode.ToString(),
            RoutingKey = routingDecision.ResolvedKey,
            IsAuthoritative = routingDecision.OperatingMode.IsAuthoritative,
            Discrepancies = augmentDiscrepancies.ToList(),
            ProviderIntegrityScore = providerIntegrity?.IntegrityScore,
            Totals = new AdjudicationTotals
            {
                BilledAmount = request.Lines.Sum(l => l.BilledAmount),
                AllowedAmount = benefitResult.Totals.TotalAllowed,
                DeductibleAmount = benefitResult.Totals.TotalDeductible,
                CopayAmount = benefitResult.Totals.TotalCopay,
                CoinsuranceAmount = benefitResult.Totals.TotalCoinsurance,
                MemberResponsibility = benefitResult.Totals.TotalMemberResponsibility,
                PlanPayment = benefitResult.Totals.TotalPlanPaid,
                ContractualAdjustment = request.Lines.Sum(l => l.BilledAmount)
                    - benefitResult.Totals.TotalAllowed
            },
            Lines = benefitResult.Lines.Select(bl =>
            {
                var priced = pricingResults.LineResults
                    .FirstOrDefault(p => p.LineNumber == bl.LineNumber);
                var reqLine = request.Lines.First(l => l.LineNumber == bl.LineNumber);

                return new AdjudicationLineResponse
                {
                    LineNumber = bl.LineNumber,
                    ProcedureCode = reqLine.ProcedureCode,
                    BilledAmount = reqLine.BilledAmount,
                    AllowedAmount = priced?.AllowedAmount ?? bl.AllowedAmount,
                    DeductibleAmount = bl.DeductibleAmount,
                    CopayAmount = bl.CopayAmount,
                    CoinsuranceAmount = bl.CoinsuranceAmount,
                    MemberResponsibility = bl.MemberResponsibility,
                    PlanPayment = bl.PlanPaidAmount,
                    ContractualAdjustment = priced?.ContractualAdjustment ?? 0,
                    FeeScheduleType = priced?.FeeScheduleType.ToString(),
                    FeeScheduleId = priced?.FeeScheduleId,
                    NetworkStatus = priced?.NetworkStatus.ToString(),
                    ServiceTypeCode = bl.ServiceTypeCode,
                    IsCovered = bl.IsCovered,
                    AdjustmentReasons = bl.Adjustments
                };
            }).ToList(),
            Accumulators = benefitResult.AccumulatorSnapshot,
            Timings = stageTimings
        };

        var outcome = benefitResult.Success ? "approved" : "denied";
        adjudicationSpan?.SetTag("cho.outcome", outcome);
        adjudicationSpan?.SetTag("cho.plan_payment", response.Totals.PlanPayment);

        RecordLatency(sw, claimTypeCode, outcome);
        ChoMetrics.AdjudicationOutcome.Add(1,
            new KeyValuePair<string, object?>("cho.outcome", outcome),
            new KeyValuePair<string, object?>("cho.claim_type", claimTypeCode),
            new KeyValuePair<string, object?>("cho.operating_mode", routingDecision.OperatingMode.Mode.ToString()));

        _logger.LogInformation(
            "Adjudication complete for claim {ClaimId}: allowed={Allowed}, plan={Plan}, member={Member}",
            SanitizeForLog(request.ClaimId), response.Totals.AllowedAmount,
            response.Totals.PlanPayment, response.Totals.MemberResponsibility);

        return Ok(response);
    }

    private static void RecordLatency(System.Diagnostics.Stopwatch sw, string claimType, string step)
    {
        sw.Stop();
        ChoMetrics.ClaimProcessingLatency.Record(
            sw.Elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("cho.claim_type", claimType),
            new KeyValuePair<string, object?>("cho.adjudication_step", step));
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/calculate-benefits
    //
    // Standalone benefit calculation (replaces Argo steps 6+8).
    // Use when pricing is handled separately or already done.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Calculate benefit cost-sharing for a claim.
    /// Calls the BenefitCalculationEngine with Redis-backed accumulators.
    /// </summary>
    [HttpPost("calculate-benefits")]
    [ProducesResponseType(typeof(BenefitResolutionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BenefitResolutionResult>> CalculateBenefits(
        [FromBody] BenefitResolutionRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Calculating benefits for member {MemberId}, plan {PlanId}, {LineCount} lines",
            SanitizeForLog(request.MemberId), request.BenefitPlanId, request.Lines.Count);

        var result = await _benefitEngine.CalculateAsync(request, ct);
        return Ok(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // GET /api/v1/adjudication/provider-integrity/{npi}
    //
    // Standalone provider-integrity check, side-effect-free. Exposes
    // IProviderIntegrityGate.CheckAsync over HTTP so claims-service's
    // ProviderIntegrityStage can run the same federal-exclusion check
    // AdjudicationController.Adjudicate already runs internally, without
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
            SanitizeForLog(request.OriginalClaimId),
            SanitizeForLog(request.MemberId),
            request.BenefitPlanId,
            request.ServiceDate);

        await _benefitEngine.ReverseClaimAsync(
            request.MemberId,
            request.SubscriberId ?? string.Empty,
            request.BenefitPlanId,
            request.ServiceDate,
            request.OriginalClaimId,
            ct);

        return NoContent();
    }

    // ═══════════════════════════════════════════════════════════════════
    // POST /api/v1/adjudication/resolve-rates
    //
    // Standalone rate resolution (replaces Argo step 7).
    // Use when benefit calculation is handled separately.
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve allowed rates for claim lines using the Fee Schedule Engine.
    /// Looks up provider contracts, fee schedules, and applies modifier adjustments.
    /// </summary>
    [HttpPost("resolve-rates")]
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
    [ProducesResponseType(typeof(NcciScrubResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<NcciScrubResult>> NcciCheck(
        [FromBody] NcciScrubRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Running NCCI/MUE check for claim {ClaimId}, {LineCount} lines",
            SanitizeForLog(request.ClaimId), request.ServiceLines.Count);

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
    [ProducesResponseType(typeof(ClaimsScrubResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ClaimsScrubResponse>> ScrubCheck(
        [FromBody] ClaimsScrubRequest request,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Running scrub validation for claim {ClaimId}",
            SanitizeForLog(request.Claim.ClaimId));

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
            SanitizeForLog(request.ProviderNpi),
            SanitizeForLog(request.StateCode),
            SanitizeForLog(request.LineOfBusiness),
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

    // ═══════════════════════════════════════════════════════════════════
    // Helper: Normalize claim type string → X12 transaction code
    // ═══════════════════════════════════════════════════════════════════

    private static string NormalizeClaimType(string? claimType)
    {
        return claimType?.Trim() switch
        {
            var t when string.Equals(t, "Institutional", StringComparison.OrdinalIgnoreCase) => "837I",
            var t when string.Equals(t, "Dental", StringComparison.OrdinalIgnoreCase) => "837D",
            _ => "837P" // Professional is the default
        };
    }

    private static ClaimType ParseScrubClaimType(string claimTypeCode)
    {
        return claimTypeCode switch
        {
            "837I" => ClaimType.Institutional,
            "837D" => ClaimType.Dental,
            _ => ClaimType.Professional
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper: Map line-of-business int to PaLineOfBusiness enum
    // ═══════════════════════════════════════════════════════════════════

    private static PaLineOfBusiness MapToPaLineOfBusiness(int? lob) => lob switch
    {
        1 => PaLineOfBusiness.Commercial,
        2 => PaLineOfBusiness.Medicare,
        3 => PaLineOfBusiness.Medicaid,
        4 => PaLineOfBusiness.Medicaid,  // CHIP → Medicaid rules
        5 => PaLineOfBusiness.Exchange,
        _ => PaLineOfBusiness.Medicaid   // Default to Medicaid for TX MCO tenants
    };

    private static string? MapToPaProgram(int? lob) => lob switch
    {
        3 or 4 => "STAR",
        _ => null
    };

    private static bool IsMemberEligibleForServiceDate(
        AdjudicationRequest request,
        out string reason)
    {
        if (request.MemberEffectiveDate is DateOnly effectiveDate
            && request.ServiceDate < effectiveDate)
        {
            reason = "Service date before member coverage effective date";
            return false;
        }

        if (request.MemberTerminationDate is DateOnly terminationDate
            && request.ServiceDate > terminationDate)
        {
            reason = "Service date after member coverage termination date";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.MemberEnrollmentStatus)
            && !request.MemberEnrollmentStatus.Equals("Active", StringComparison.OrdinalIgnoreCase)
            && !request.MemberEnrollmentStatus.Equals("Terminated", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"Member status is {request.MemberEnrollmentStatus}";
            return false;
        }

        if (request.MemberEnrollmentStatus?.Equals("Terminated", StringComparison.OrdinalIgnoreCase) is true
            && request.MemberTerminationDate is null)
        {
            reason = "Member coverage terminated";
            return false;
        }

        reason = "Active coverage";
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helper: Map AdjudicationRequest → X12837Claim for scrub engine
    // ═══════════════════════════════════════════════════════════════════

    private static X12837Claim MapToScrubClaim(AdjudicationRequest request) => new()
    {
        ClaimId = request.ClaimId,
        ClaimType = ParseScrubClaimType(NormalizeClaimType(request.ClaimType)),
        TransactionControlNumber = request.ClaimId,
        InterchangeControlNumber = request.ClaimId,
        TransactionDate = request.ServiceDate.ToString("yyyyMMdd"),
        Submitter = new CloudHealthOffice.ClaimsScrubEngine.Models.ClaimSubmitter
        {
            Name = "Adjudication Pipeline",
            IdentificationCode = "ADJ",
            IdentificationQualifier = "46",
        },
        Receiver = new CloudHealthOffice.ClaimsScrubEngine.Models.ClaimReceiver
        {
            Name = "Internal",
            IdentificationCode = "INT",
            IdentificationQualifier = "PI",
        },
        BillingProvider = new CloudHealthOffice.ClaimsScrubEngine.Models.BillingProvider
        {
            Npi = request.ProviderNpi,
            Name = "Provider",
            EntityType = "2",
            Address = new CloudHealthOffice.ClaimsScrubEngine.Models.ProviderAddress
            {
                Line1 = "", City = "", State = "", PostalCode = "",
            },
        },
        Subscriber = new CloudHealthOffice.ClaimsScrubEngine.Models.ClaimSubscriber
        {
            MemberId = request.MemberId,
            FirstName = "N/A",
            LastName = "N/A",
            DateOfBirth = "19000101", // Not available from AdjudicationRequest
        },
        ClaimHeader = new CloudHealthOffice.ClaimsScrubEngine.Models.ClaimHeader
        {
            PatientControlNumber = request.ClaimId,
            TotalChargeAmount = request.Lines.Sum(l => l.BilledAmount),
            PlaceOfServiceCode = request.Lines.Select(l => l.PlaceOfService).FirstOrDefault(),
            DiagnosisCodes = request.Lines
                .SelectMany(l => l.DiagnosisCodes)
                .Distinct()
                .Select(c => new CloudHealthOffice.ClaimsScrubEngine.Models.DiagnosisCode
                {
                    Code = c,
                    Qualifier = "ABK",
                })
                .ToList(),
        },
        ServiceLines = request.Lines.Select(l => new CloudHealthOffice.ClaimsScrubEngine.Models.ServiceLine
        {
            LineNumber = l.LineNumber,
            ProcedureCode = l.ProcedureCode,
            Modifiers = l.Modifiers,
            ServiceDate = request.ServiceDate.ToString("yyyyMMdd"),
            ChargeAmount = l.BilledAmount,
            Units = l.Units,
            PlaceOfService = l.PlaceOfService,
            RevenueCode = l.RevenueCode,
        }).ToList(),
        TotalClaimedAmount = request.Lines.Sum(l => l.BilledAmount),
        ParsedAt = DateTime.UtcNow.ToString("o"),
    };
}

// ═══════════════════════════════════════════════════════════════════
// REQUEST / RESPONSE DTOs
//
// These are the contract between the Argo workflow and the
// adjudication endpoints. They're intentionally separate from
// the engine's internal models — the controller maps between them.
// ═══════════════════════════════════════════════════════════════════

/// <summary>
/// Combined adjudication request — everything the workflow knows
/// about the claim after steps 1-5 (get-claim, verify-coverage,
/// validate-provider, validate-codes, check-prior-auth).
/// </summary>
public record AdjudicationRequest
{
    public string ClaimId { get; init; } = default!;
    public string MemberId { get; init; } = default!;
    public string SubscriberId { get; init; } = default!;
    public Guid BenefitPlanId { get; init; }
    public DateOnly ServiceDate { get; init; }
    public DateOnly? MemberEffectiveDate { get; init; }
    public DateOnly? MemberTerminationDate { get; init; }
    public string? MemberEnrollmentStatus { get; init; }
    public string ProviderNpi { get; init; } = default!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NetworkTier NetworkTier { get; init; }

    /// <summary>
    /// Line of business from coverage verification (1=Commercial, 2=Medicare, 3=Medicaid, etc.).
    /// Passed through to the benefit engine for LOB-specific adjudication rules.
    /// </summary>
    public int? LineOfBusiness { get; init; }

    /// <summary>
    /// Claim type: "Professional" (837P), "Institutional" (837I), or "Dental" (837D).
    /// Determines pipeline routing and pricing method selection.
    /// Defaults to Professional when not specified for backward compatibility.
    /// </summary>
    public string ClaimType { get; init; } = "Professional";

    /// <summary>
    /// State code for the claim's jurisdiction (e.g., "TX", "FL", "CA").
    /// Used for state-specific prior auth rules and provider enrollment checks.
    /// </summary>
    public string? StateCode { get; init; }

    /// <summary>
    /// Provider taxonomy code. Used for prior auth rule evaluation
    /// (e.g., TX gold card exemption by provider type).
    /// </summary>
    public string? ProviderTaxonomy { get; init; }

    /// <summary>
    /// Prior authorization number if one was provided on the claim.
    /// Cross-referenced during PA rule evaluation.
    /// </summary>
    public string? PriorAuthorizationNumber { get; init; }

    public List<AdjudicationLineRequest> Lines { get; init; } = [];

    /// <summary>
    /// COB context. Null for primary claims; required for secondary/tertiary.
    /// When present, the benefit engine applies COB reduction after its own
    /// cost-sharing waterfall.
    /// </summary>
    public AdjudicationCobInfo? Cob { get; init; }
}

public record AdjudicationCobInfo
{
    /// <summary>1 = Primary, 2 = Secondary, 3 = Tertiary.</summary>
    public int PayerSequence { get; init; } = 1;

    /// <summary>true = Complementary (default for commercial), false = Non-duplication.</summary>
    public bool UseComplementaryModel { get; init; } = true;

    public string? PrimaryPayerId { get; init; }
    public string? PrimaryPayerName { get; init; }

    /// <summary>Primary payer payment per line (key = line number).</summary>
    public Dictionary<int, decimal> PrimaryPayerPaymentByLine { get; init; } = [];

    /// <summary>Primary payer allowed per line (non-duplication model).</summary>
    public Dictionary<int, decimal> PrimaryAllowedByLine { get; init; } = [];
}

public record AdjudicationLineRequest
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public string? CodeType { get; init; } = "CPT";
    public List<string> Modifiers { get; init; } = [];
    public string? RevenueCode { get; init; }
    public string PlaceOfService { get; init; } = default!;
    public decimal BilledAmount { get; init; }
    public decimal Units { get; init; } = 1;
    public List<string> DiagnosisCodes { get; init; } = [];
}

/// <summary>
/// Fully adjudicated result — the workflow uses this to update the
/// claim with final amounts and generate 835/CAS segments.
/// </summary>
public record AdjudicationResponse
{
    public string ClaimId { get; init; } = default!;
    public bool Success { get; init; }
    public string? DenialReasonCode { get; init; }
    public string? DenialReasonDescription { get; init; }
    public AdjudicationTotals Totals { get; init; } = new();
    public List<AdjudicationLineResponse> Lines { get; init; } = [];
    public List<AccumulatorState>? Accumulators { get; init; }

    /// <summary>
    /// Operating mode under which this claim was adjudicated.
    /// "Replace" = CHO is authoritative; "Augment" = shadow mode alongside QNXT.
    /// </summary>
    public string? OperatingMode { get; init; }

    /// <summary>
    /// Routing decision key that determined pipeline behavior for this claim.
    /// Example: "professional-medicaid", "institutional", "benefitCalculation".
    /// </summary>
    public string? RoutingKey { get; init; }

    /// <summary>
    /// Whether CHO's result is authoritative (true) or advisory (false, augment mode).
    /// </summary>
    public bool IsAuthoritative { get; init; } = true;

    /// <summary>
    /// Discrepancies between CHO and legacy results (augment mode only).
    /// Empty in Replace mode.
    /// </summary>
    public List<string> Discrepancies { get; init; } = [];

    /// <summary>
    /// Provider integrity score from the ProviderVerificationEngine.
    /// Null when the verification service was not consulted.
    /// </summary>
    public int? ProviderIntegrityScore { get; init; }

    /// <summary>
    /// Elapsed milliseconds for adjudication sub-steps. Used by local MCC
    /// benchmarking to identify the next tuning target.
    /// </summary>
    public IReadOnlyDictionary<string, double>? Timings { get; init; }
}

public record AdjudicationTotals
{
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public decimal MemberResponsibility { get; init; }
    public decimal PlanPayment { get; init; }
}

public record AdjudicationLineResponse
{
    public int LineNumber { get; init; }
    public string ProcedureCode { get; init; } = default!;
    public decimal BilledAmount { get; init; }
    public decimal AllowedAmount { get; init; }
    public decimal ContractualAdjustment { get; init; }
    public decimal DeductibleAmount { get; init; }
    public decimal CopayAmount { get; init; }
    public decimal CoinsuranceAmount { get; init; }
    public decimal MemberResponsibility { get; init; }
    public decimal PlanPayment { get; init; }
    public string? FeeScheduleType { get; init; }
    public string? FeeScheduleId { get; init; }
    public string? NetworkStatus { get; init; }
    public string? ServiceTypeCode { get; init; }
    public bool IsCovered { get; init; }
    public List<AdjustmentReason> AdjustmentReasons { get; init; } = [];
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
}
