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
using BenefitPlanService.Middleware;
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
    private readonly ILogger<AdjudicationController> _logger;

    public AdjudicationController(
        IClaimRoutingService scrubEngine,
        IBenefitCalculationEngine benefitEngine,
        IRateResolutionService rateEngine,
        INcciEditService ncciEngine,
        ILogger<AdjudicationController> logger)
    {
        _scrubEngine = scrubEngine;
        _benefitEngine = benefitEngine;
        _rateEngine = rateEngine;
        _ncciEngine = ncciEngine;
        _logger = logger;
    }

    private string TenantId => HttpContext.GetTenantId()
        ?? throw new InvalidOperationException("Tenant context missing");

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return value.Replace("\r", string.Empty).Replace("\n", string.Empty);
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
        _logger.LogInformation(
            "Adjudicating claim {ClaimId} for member {MemberId}, plan {PlanId}, {LineCount} lines",
            request.ClaimId, request.MemberId, request.BenefitPlanId, request.Lines.Count);

        // ── Step 0a: Claims scrub validation ──
        // Run scrub rules (data completeness, code validation, date logic,
        // amount logic, provider validation, modifier validation) before
        // NCCI edits so obviously bad data is caught early.
        var scrubClaim = MapToScrubClaim(request);
        var scrubResponse = await _scrubEngine.ScrubAndRouteAsync(
            new ClaimsScrubRequest { Claim = scrubClaim }, ct);

        if (scrubResponse.Result.Routing.Destination != "adjudication")
        {
            _logger.LogWarning(
                "Claim {ClaimId} failed scrub validation: {ErrorCount} error(s), {WarningCount} warning(s)",
                request.ClaimId, scrubResponse.Result.ErrorCount, scrubResponse.Result.WarningCount);

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
            });
        }

        // ── Step 0b: NCCI/MUE pre-payment edit check ──
        // Runs before pricing so bundled/excess-unit lines are caught before
        // accumulators are touched or rates are resolved.
        var ncciRequest = new NcciScrubRequest
        {
            TenantId = TenantId,
            ClaimId = request.ClaimId,
            ClaimType = "837P",
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

        var ncciResult = await _ncciEngine.ScrubAsync(ncciRequest, ct);

        if (!ncciResult.Passed)
        {
            _logger.LogWarning(
                "Claim {ClaimId} failed NCCI/MUE edits: {FailureCount} failure(s)",
                request.ClaimId, ncciResult.EditFailures.Count);

            // Surface failures as a 422 so the Argo workflow can route to the
            // NCCI work queue rather than proceeding to payment.
            return UnprocessableEntity(new
            {
                claimId = request.ClaimId,
                error = "NCCI_MUE_EDIT_FAILURE",
                message = $"Claim failed {ncciResult.EditFailures.Count} NCCI/MUE edit(s). " +
                          "Review edit failures and resubmit or override.",
                editFailures = ncciResult.EditFailures,
            });
        }

        // ── Step 1: Resolve rates (fee schedule engine) ──
        var pricingRequests = request.Lines.Select(line => new PricingRequest
        {
            TenantId = TenantId,
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers,
            ProviderNpi = request.ProviderNpi,
            PlaceOfServiceCode = line.PlaceOfService,
            ServiceDate = request.ServiceDate.ToDateTime(TimeOnly.MinValue),
            PlanId = request.BenefitPlanId.ToString(),
            BilledAmount = line.BilledAmount,
            Units = line.Units,
            LineNumber = line.LineNumber
        }).ToList();

        var pricingResults = await _rateEngine.ResolveBatchAsync(pricingRequests, ct);

        // ── Step 2: Build benefit request with allowed amounts from pricing ──
        var benefitLines = request.Lines.Select(line =>
        {
            var priced = pricingResults.LineResults
                .FirstOrDefault(p => p.LineNumber == line.LineNumber);

            return new ClaimLineInput
            {
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
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

        var benefitResult = await _benefitEngine.CalculateAsync(benefitRequest, ct);

        // ── Step 3: Merge pricing + benefit results ──
        var response = new AdjudicationResponse
        {
            ClaimId = request.ClaimId,
            Success = benefitResult.Success,
            DenialReasonCode = benefitResult.DenialReasonCode,
            DenialReasonDescription = benefitResult.DenialReasonDescription,
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
            Accumulators = benefitResult.AccumulatorSnapshot
        };

        _logger.LogInformation(
            "Adjudication complete for claim {ClaimId}: allowed={Allowed}, plan={Plan}, member={Member}",
            request.ClaimId, response.Totals.AllowedAmount,
            response.Totals.PlanPayment, response.Totals.MemberResponsibility);

        return Ok(response);
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
            request.MemberId, request.BenefitPlanId, request.Lines.Count);

        var result = await _benefitEngine.CalculateAsync(request, ct);
        return Ok(result);
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
    // Helper: Map AdjudicationRequest → X12837Claim for scrub engine
    // ═══════════════════════════════════════════════════════════════════

    private static X12837Claim MapToScrubClaim(AdjudicationRequest request) => new()
    {
        ClaimId = request.ClaimId,
        ClaimType = ClaimType.Professional,
        TransactionControlNumber = request.ClaimId,
        InterchangeControlNumber = request.ClaimId,
        TransactionDate = request.ServiceDate.ToString("yyyyMMdd"),
        Submitter = new ClaimsScrubEngine.Models.ClaimSubmitter
        {
            Name = "Adjudication Pipeline",
            IdentificationCode = "ADJ",
            IdentificationQualifier = "46",
        },
        Receiver = new ClaimsScrubEngine.Models.ClaimReceiver
        {
            Name = "Internal",
            IdentificationCode = "INT",
            IdentificationQualifier = "PI",
        },
        BillingProvider = new ClaimsScrubEngine.Models.BillingProvider
        {
            Npi = request.ProviderNpi,
            Name = "Provider",
            EntityType = "2",
            Address = new ClaimsScrubEngine.Models.ProviderAddress
            {
                Line1 = "", City = "", State = "", PostalCode = "",
            },
        },
        Subscriber = new ClaimsScrubEngine.Models.ClaimSubscriber
        {
            MemberId = request.MemberId,
            FirstName = "N/A",
            LastName = "N/A",
            DateOfBirth = "19000101", // Not available from AdjudicationRequest
        },
        ClaimHeader = new ClaimsScrubEngine.Models.ClaimHeader
        {
            PatientControlNumber = request.ClaimId,
            TotalChargeAmount = request.Lines.Sum(l => l.BilledAmount),
            DiagnosisCodes = request.Lines
                .SelectMany(l => l.DiagnosisCodes)
                .Distinct()
                .Select(c => new ClaimsScrubEngine.Models.DiagnosisCode
                {
                    Code = c,
                    Qualifier = "ABK",
                })
                .ToList(),
        },
        ServiceLines = request.Lines.Select(l => new ClaimsScrubEngine.Models.ServiceLine
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
    public string ProviderNpi { get; init; } = default!;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NetworkTier NetworkTier { get; init; }

    /// <summary>
    /// Line of business from coverage verification (1=Commercial, 2=Medicare, 3=Medicaid, etc.).
    /// Passed through to the benefit engine for LOB-specific adjudication rules.
    /// </summary>
    public int? LineOfBusiness { get; init; }

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
