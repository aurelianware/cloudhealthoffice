using BenefitPlanService.Models.Estimate;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;

namespace BenefitPlanService.Services;

/// <summary>
/// Default <see cref="IPaymentEstimateService"/>. Reuses the same fee-schedule
/// pricing (<see cref="IRateResolutionService"/>) and benefit-calculation
/// (<see cref="IBenefitCalculationEngine"/>) engines that real claim
/// adjudication uses, but runs the benefit engine in
/// <see cref="AdjudicationExecutionMode.Prospective"/> so nothing is
/// persisted. Provider-integrity and prior-auth checks are consulted in an
/// advisory (non-blocking) manner and surfaced as warnings rather than
/// denials — an estimate never rejects a request.
/// </summary>
public class PaymentEstimateService : IPaymentEstimateService
{
    private readonly IRateResolutionService _rateEngine;
    private readonly IBenefitCalculationEngine _benefitEngine;
    private readonly IProviderIntegrityGate _providerIntegrityGate;
    private readonly IPriorAuthRuleEngine _priorAuthEngine;
    private readonly IOperatingModeProvider _operatingModeProvider;
    private readonly IClaimTypeRouter _claimTypeRouter;
    private readonly ILogger<PaymentEstimateService> _logger;

    public PaymentEstimateService(
        IRateResolutionService rateEngine,
        IBenefitCalculationEngine benefitEngine,
        IProviderIntegrityGate providerIntegrityGate,
        IPriorAuthRuleEngine priorAuthEngine,
        IOperatingModeProvider operatingModeProvider,
        IClaimTypeRouter claimTypeRouter,
        ILogger<PaymentEstimateService> logger)
    {
        _rateEngine = rateEngine;
        _benefitEngine = benefitEngine;
        _providerIntegrityGate = providerIntegrityGate;
        _priorAuthEngine = priorAuthEngine;
        _operatingModeProvider = operatingModeProvider;
        _claimTypeRouter = claimTypeRouter;
        _logger = logger;
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var buffer = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
            buffer.Append(char.IsControl(ch) ? '_' : ch);
        if (buffer.Length > 256) buffer.Length = 256;
        return buffer.ToString();
    }

    public async Task<PaymentEstimateResponse> EstimateAsync(
        string tenantId,
        PaymentEstimateRequest request,
        CancellationToken ct = default)
    {
        var claimTypeCode = NormalizeClaimType(request.ClaimType);
        var lobCode = MapLineOfBusinessToCode(request.LineOfBusiness);

        _logger.LogInformation(
            "Prospective estimate for member {MemberId}, plan {PlanId}, {LineCount} line(s), type {ClaimType}",
            SanitizeForLog(request.MemberId), request.BenefitPlanId,
            request.Lines.Count, SanitizeForLog(claimTypeCode));

        var warnings = new List<EstimateMessage>();

        // ── Authority: only claim CHO is the authoritative payer engine when
        //    the tenant's operating mode says so for this claim type/LOB.
        //    Any failure here degrades safely to Simulation. ──
        var authority = await ResolveAuthorityAsync(tenantId, request.ClaimType, lobCode, ct);

        // ── Step 1: Fee-schedule pricing (read-only) ──
        var pricingRequests = request.Lines.Select(line => new PricingRequest
        {
            TenantId = tenantId,
            ProcedureCode = line.ProcedureCode,
            Modifiers = line.Modifiers,
            ProviderNpi = request.ProviderNpi,
            PlaceOfServiceCode = string.IsNullOrWhiteSpace(line.PlaceOfService) ? "11" : line.PlaceOfService,
            ServiceDate = request.ServiceDate.ToDateTime(TimeOnly.MinValue),
            PlanId = request.BenefitPlanId.ToString(),
            BilledAmount = line.ChargeAmount,
            Units = line.Units,
            LineNumber = line.LineNumber
        }).ToList();

        var pricing = await _rateEngine.ResolveBatchAsync(pricingRequests, ct);

        // Index priced lines by line number. Built defensively (last wins)
        // rather than via ToDictionary so a pricing engine that ever returns
        // duplicate line numbers can never surface as a 500 on a valid request.
        var pricedByLine = new Dictionary<int, PricingResult>();
        foreach (var priced in pricing.LineResults)
            pricedByLine[priced.LineNumber] = priced;

        // ── Step 2: Benefit calculation in read-only PROSPECTIVE mode ──
        // Mirror the production adjudication seam: feed the priced allowed
        // amount as the benefit line's billed amount so the cost-sharing
        // waterfall operates on the allowed amount, exactly as
        // AdjudicationController.Adjudicate does.
        var benefitLines = request.Lines.Select(line =>
        {
            var priced = pricedByLine.GetValueOrDefault(line.LineNumber);
            var allowed = priced?.AllowedAmount ?? line.ChargeAmount;
            return new ClaimLineInput
            {
                LineNumber = line.LineNumber,
                ProcedureCode = line.ProcedureCode,
                CodeType = line.CodeType,
                Modifiers = line.Modifiers,
                RevenueCode = line.RevenueCode,
                PlaceOfService = string.IsNullOrWhiteSpace(line.PlaceOfService) ? "11" : line.PlaceOfService,
                BilledAmount = allowed,
                Units = line.Units,
                DiagnosisCodes = line.DiagnosisCodes
            };
        }).ToList();

        var benefitRequest = new BenefitResolutionRequest
        {
            MemberId = request.MemberId,
            SubscriberId = string.IsNullOrWhiteSpace(request.SubscriberId) ? request.MemberId : request.SubscriberId,
            BenefitPlanId = request.BenefitPlanId,
            ServiceDate = request.ServiceDate,
            NetworkTier = request.NetworkTier,
            LineOfBusiness = lobCode,
            ClaimType = claimTypeCode,
            ClaimId = request.RequestId ?? $"estimate-{Guid.NewGuid():N}",
            Lines = benefitLines,
            AllowedAmounts = pricedByLine.ToDictionary(kv => kv.Key, kv => kv.Value.AllowedAmount),
            ExecutionMode = AdjudicationExecutionMode.Prospective
        };

        var benefitResult = await _benefitEngine.CalculateAsync(benefitRequest, ct);

        // ── Insufficient data: plan not found / no lines processed. ──
        if (benefitResult.Lines.Count == 0)
        {
            warnings.Add(new EstimateMessage
            {
                Code = "BENEFIT_PLAN_UNRESOLVED",
                Severity = EstimateMessageSeverity.Warning,
                Description = benefitResult.DenialReasonDescription
                    ?? "Benefit plan or coverage could not be resolved for this estimate."
            });

            return new PaymentEstimateResponse
            {
                RequestId = request.RequestId,
                Status = "insufficient_data",
                Authority = authority,
                Totals = new EstimateTotals { BilledAmount = request.Lines.Sum(l => l.ChargeAmount) },
                Lines = [],
                Warnings = warnings,
                Confidence = new EstimateConfidence
                {
                    Level = EstimateConfidenceLevel.InsufficientData,
                    Reasons = [],
                    MissingData = ["Benefit plan / member coverage"]
                }
            };
        }

        // ── Step 3: Advisory provider-integrity check (non-blocking) ──
        var integrity = await SafeCheckIntegrityAsync(request.ProviderNpi, tenantId, warnings, ct);

        // ── Step 4: Advisory prior-auth evaluation (non-blocking) ──
        var priorAuthEvaluated = await SafeEvaluatePriorAuthAsync(
            tenantId, request, claimTypeCode, lobCode, warnings, ct);

        // ── Step 5: Map priced + adjudicated lines onto the estimate ──
        var estimateLines = new List<EstimateLine>();
        foreach (var reqLine in request.Lines.OrderBy(l => l.LineNumber))
        {
            var benefitLine = benefitResult.Lines.FirstOrDefault(b => b.LineNumber == reqLine.LineNumber);
            var priced = pricedByLine.GetValueOrDefault(reqLine.LineNumber);
            estimateLines.Add(MapLine(reqLine, benefitLine, priced));
        }

        var totals = SumLineTotals(estimateLines);

        // ── Step 6: Deterministic confidence ──
        var confidence = BuildConfidence(pricing, benefitResult, integrity, priorAuthEvaluated, estimateLines);

        return new PaymentEstimateResponse
        {
            RequestId = request.RequestId,
            Status = "estimated",
            Authority = authority,
            Totals = totals,
            Lines = estimateLines,
            Warnings = warnings,
            Confidence = confidence
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    // AUTHORITY
    // ═══════════════════════════════════════════════════════════════════

    private async Task<EstimateAuthority> ResolveAuthorityAsync(
        string tenantId, string claimType, int? lobCode, CancellationToken ct)
    {
        try
        {
            var config = await _operatingModeProvider.GetConfigurationAsync(tenantId, ct);
            var routing = _claimTypeRouter.Route(config, claimType, lobCode);

            // Only report AuthoritativePayer when CHO both processes and is
            // authoritative for this claim type/LOB. Everything else is a
            // simulation.
            return routing.Route != AdjudicationRoute.LegacyOnly && routing.OperatingMode.IsAuthoritative
                ? EstimateAuthority.AuthoritativePayer
                : EstimateAuthority.Simulation;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Operating-mode resolution failed for tenant {TenantId}; defaulting estimate authority to Simulation",
                SanitizeForLog(tenantId));
            return EstimateAuthority.Simulation;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ADVISORY CHECKS
    // ═══════════════════════════════════════════════════════════════════

    private async Task<ProviderIntegrityResult?> SafeCheckIntegrityAsync(
        string npi, string tenantId, List<EstimateMessage> warnings, CancellationToken ct)
    {
        try
        {
            var result = await _providerIntegrityGate.CheckAsync(npi, tenantId, ct: ct);

            if (result.IsExcluded)
            {
                warnings.Add(new EstimateMessage
                {
                    Code = "PROVIDER_EXCLUDED",
                    Severity = EstimateMessageSeverity.Warning,
                    Description = result.DenialReason
                        ?? "Provider appears on a federal exclusion list; a real claim would be denied."
                });
            }
            else if (!result.Passed)
            {
                warnings.Add(new EstimateMessage
                {
                    Code = "PROVIDER_REVIEW_REQUIRED",
                    Severity = EstimateMessageSeverity.Warning,
                    Description = "Provider integrity could not be confidently verified; a real claim may pend for review."
                });
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider integrity check unavailable during estimate");
            warnings.Add(new EstimateMessage
            {
                Code = "PROVIDER_INTEGRITY_UNAVAILABLE",
                Severity = EstimateMessageSeverity.Info,
                Description = "Provider integrity verification was unavailable; estimate does not reflect exclusion screening."
            });
            return null;
        }
    }

    /// <summary>
    /// Returns true when the prior-auth engine produced a determination
    /// (so confidence isn't penalized), false when it was unavailable.
    /// </summary>
    private async Task<bool> SafeEvaluatePriorAuthAsync(
        string tenantId, PaymentEstimateRequest request,
        string claimTypeCode, int? lobCode,
        List<EstimateMessage> warnings, CancellationToken ct)
    {
        try
        {
            var context = new PaRuleContext
            {
                TenantId = tenantId,
                StateCode = request.StateCode ?? "TX",
                Lob = MapToPaLineOfBusiness(lobCode),
                Program = MapToPaProgram(lobCode),
                RequestingProviderNpi = request.ProviderNpi,
                ServicingProviderNpi = request.ProviderNpi,
                ServicingProviderTaxonomy = request.ProviderTaxonomy,
                MemberId = request.MemberId,
                ServiceDate = request.ServiceDate,
                ProcedureCodes = request.Lines.Select(l => l.ProcedureCode).Distinct().ToList(),
                DiagnosisCodes = request.Lines.SelectMany(l => l.DiagnosisCodes).Distinct().ToList(),
                PlaceOfServiceCode = request.Lines.Select(l => l.PlaceOfService).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                EstimatedCost = request.Lines.Sum(l => l.ChargeAmount)
            };

            var decision = await _priorAuthEngine.EvaluateAsync(context, ct);

            if (decision.IsPriorAuthRequired() && string.IsNullOrEmpty(request.PriorAuthorizationNumber))
            {
                warnings.Add(new EstimateMessage
                {
                    Code = "PRIOR_AUTH_REQUIRED",
                    Severity = EstimateMessageSeverity.Warning,
                    Description = decision.DenialReason
                        ?? "Prior authorization may be required for one or more services on this estimate."
                });
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prior-auth evaluation unavailable during estimate");
            warnings.Add(new EstimateMessage
            {
                Code = "PRIOR_AUTH_UNAVAILABLE",
                Severity = EstimateMessageSeverity.Info,
                Description = "Prior-authorization rules were unavailable; estimate does not reflect authorization requirements."
            });
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // LINE MAPPING
    // ═══════════════════════════════════════════════════════════════════

    private static EstimateLine MapLine(
        PaymentEstimateLineRequest reqLine,
        LineBenefitResult? benefitLine,
        PricingResult? priced)
    {
        var billed = reqLine.ChargeAmount;
        var allowed = priced?.AllowedAmount ?? benefitLine?.AllowedAmount ?? billed;
        var contractual = Math.Max(0, billed - allowed);

        var messages = new List<EstimateMessage>();

        // Fee schedule / rate explainability.
        if (priced is not null)
        {
            var rateResolved = priced.RateSource != RateSource.BilledCharges;
            messages.Add(new EstimateMessage
            {
                Code = rateResolved ? "FEE_SCHEDULE_APPLIED" : "BILLED_CHARGES_USED",
                Severity = rateResolved ? EstimateMessageSeverity.Info : EstimateMessageSeverity.Warning,
                Description = rateResolved
                    ? $"Allowed amount from {DescribeFeeSchedule(priced)} ({priced.NetworkStatus})."
                    : "No contracted or fee-schedule rate matched; billed charges used as the allowed amount."
            });
        }

        if (contractual > 0)
        {
            messages.Add(new EstimateMessage
            {
                Code = "CONTRACTUAL_ADJUSTMENT",
                Severity = EstimateMessageSeverity.Info,
                Description = $"Contractual adjustment of {contractual:C} between billed and allowed amounts."
            });
        }

        // Denied / not-covered line.
        if (benefitLine is null || !benefitLine.IsCovered || benefitLine.DenialReasonCode is not null)
        {
            var (code, status) = MapDenial(benefitLine?.DenialReasonCode);
            messages.Add(new EstimateMessage
            {
                Code = code,
                Severity = EstimateMessageSeverity.Denial,
                Description = benefitLine?.DenialReasonDescription
                    ?? "Service is not expected to pay as submitted."
            });

            return new EstimateLine
            {
                LineNumber = reqLine.LineNumber,
                ProcedureCode = reqLine.ProcedureCode,
                ToothNumber = reqLine.ToothNumber,
                BilledAmount = billed,
                AllowedAmount = allowed,
                ContractualAdjustment = contractual,
                PayerResponsibility = 0m,
                PatientResponsibility = benefitLine?.MemberResponsibility ?? 0m,
                DeductibleAmount = 0m,
                CopayAmount = 0m,
                CoinsuranceAmount = 0m,
                Status = status,
                Messages = messages
            };
        }

        // Payable line — explain the cost-sharing waterfall.
        if (benefitLine.DeductibleAmount > 0)
            messages.Add(Info("DEDUCTIBLE_APPLIED", $"{benefitLine.DeductibleAmount:C} applied to the deductible."));
        if (benefitLine.CopayAmount > 0)
            messages.Add(Info("COPAY_APPLIED", $"Copay of {benefitLine.CopayAmount:C} applied."));
        if (benefitLine.CoinsuranceAmount > 0)
            messages.Add(Info("COINSURANCE_APPLIED",
                $"Coinsurance of {benefitLine.CoinsuranceAmount:C}" +
                (benefitLine.CoinsurancePercent > 0 ? $" ({benefitLine.CoinsurancePercent:P0})." : ".")));
        if (benefitLine.OopMaxReduction > 0)
            messages.Add(Info("OUT_OF_POCKET_MAX_APPLIED",
                "Out-of-pocket maximum reached; patient responsibility was capped."));
        if (benefitLine.AuthRequired)
            messages.Add(new EstimateMessage
            {
                Code = "PRIOR_AUTH_REQUIRED",
                Severity = EstimateMessageSeverity.Warning,
                Description = "This benefit typically requires prior authorization."
            });

        return new EstimateLine
        {
            LineNumber = reqLine.LineNumber,
            ProcedureCode = reqLine.ProcedureCode,
            ToothNumber = reqLine.ToothNumber,
            BilledAmount = billed,
            AllowedAmount = allowed,
            ContractualAdjustment = contractual,
            PayerResponsibility = benefitLine.PlanPaidAmount,
            PatientResponsibility = benefitLine.MemberResponsibility,
            DeductibleAmount = benefitLine.DeductibleAmount,
            CopayAmount = benefitLine.CopayAmount,
            CoinsuranceAmount = benefitLine.CoinsuranceAmount,
            Status = "payable",
            Messages = messages
        };
    }

    private static EstimateMessage Info(string code, string description) => new()
    {
        Code = code,
        Severity = EstimateMessageSeverity.Info,
        Description = description
    };

    private static (string code, string status) MapDenial(string? carc) => carc switch
    {
        "96" => ("NON_COVERED_SERVICE", "not_covered"),
        "119" => ("FREQUENCY_LIMITATION", "denied"),
        "16" or "18" => ("NO_BENEFIT_MAPPING", "needs_review"),
        null => ("NO_BENEFIT_MAPPING", "needs_review"),
        _ => ("SERVICE_DENIED", "denied")
    };

    private static string DescribeFeeSchedule(PricingResult priced)
    {
        if (!string.IsNullOrWhiteSpace(priced.FeeScheduleName))
            return priced.FeeScheduleName!;
        return priced.FeeScheduleType.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // TOTALS — sum of line amounts (invariant: totals == Σ lines)
    // ═══════════════════════════════════════════════════════════════════

    private static EstimateTotals SumLineTotals(IReadOnlyList<EstimateLine> lines) => new()
    {
        BilledAmount = lines.Sum(l => l.BilledAmount),
        AllowedAmount = lines.Sum(l => l.AllowedAmount),
        ContractualAdjustment = lines.Sum(l => l.ContractualAdjustment),
        PayerResponsibility = lines.Sum(l => l.PayerResponsibility),
        PatientResponsibility = lines.Sum(l => l.PatientResponsibility),
        DeductibleAmount = lines.Sum(l => l.DeductibleAmount),
        CopayAmount = lines.Sum(l => l.CopayAmount),
        CoinsuranceAmount = lines.Sum(l => l.CoinsuranceAmount)
    };

    // ═══════════════════════════════════════════════════════════════════
    // CONFIDENCE — deterministic, rule-based (no AI heuristics)
    // ═══════════════════════════════════════════════════════════════════

    private static EstimateConfidence BuildConfidence(
        PricingResultSet pricing,
        BenefitResolutionResult benefitResult,
        ProviderIntegrityResult? integrity,
        bool priorAuthEvaluated,
        IReadOnlyList<EstimateLine> estimateLines)
    {
        var reasons = new List<string>();
        var missing = new List<string>();

        // Plan resolved (we only reach here when lines were processed).
        reasons.Add("Benefit plan resolved");

        if (benefitResult.AccumulatorSnapshot.Count > 0)
            reasons.Add("Accumulator data available");

        // Fee schedule resolution per line.
        var unpriced = pricing.LineResults
            .Where(p => p.RateSource == RateSource.BilledCharges)
            .Select(p => p.LineNumber)
            .ToList();

        if (unpriced.Count == 0 && pricing.LineResults.Count > 0)
            reasons.Add("Provider fee schedule resolved");
        else
            missing.AddRange(unpriced.Select(n => $"Fee schedule for line {n} (billed charges used)"));

        // Provider integrity.
        if (integrity is null)
            missing.Add("Provider integrity verification");
        else if (integrity.Passed)
            reasons.Add("Provider integrity verified");

        if (!priorAuthEvaluated)
            missing.Add("Prior-authorization determination");

        // Deterministic level derivation.
        EstimateConfidenceLevel level;
        var anyProviderExcluded = integrity?.IsExcluded == true;
        var anyNeedsReview = estimateLines.Any(l => l.Status == "needs_review");

        if (anyProviderExcluded)
            level = EstimateConfidenceLevel.Low;
        else if (missing.Count == 0 && !anyNeedsReview)
            level = EstimateConfidenceLevel.High;
        else if (missing.Count <= 2 && !anyNeedsReview)
            level = EstimateConfidenceLevel.Medium;
        else
            level = EstimateConfidenceLevel.Low;

        return new EstimateConfidence { Level = level, Reasons = reasons, MissingData = missing };
    }

    // ═══════════════════════════════════════════════════════════════════
    // MAPPING HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private static string NormalizeClaimType(string? claimType) => claimType?.Trim() switch
    {
        var t when string.Equals(t, "Institutional", StringComparison.OrdinalIgnoreCase) => "837I",
        var t when string.Equals(t, "Dental", StringComparison.OrdinalIgnoreCase) => "837D",
        _ => "837P"
    };

    /// <summary>
    /// Maps a line-of-business name to the numeric code the router and benefit
    /// engine expect (mirrors ClaimTypeRouter's LOB name mapping in reverse).
    /// </summary>
    private static int? MapLineOfBusinessToCode(string? lob) => lob?.Trim().ToLowerInvariant() switch
    {
        "commercial" => 1,
        "medicare" => 2,
        "medicaid" => 3,
        "chip" => 4,
        "exchange" or "marketplace" => 5,
        _ => null
    };

    private static PaLineOfBusiness MapToPaLineOfBusiness(int? lob) => lob switch
    {
        1 => PaLineOfBusiness.Commercial,
        2 => PaLineOfBusiness.Medicare,
        3 => PaLineOfBusiness.Medicaid,
        4 => PaLineOfBusiness.Medicaid,   // CHIP → Medicaid rules
        5 => PaLineOfBusiness.Exchange,
        _ => PaLineOfBusiness.Medicaid     // Default for TX MCO tenants
    };

    private static string? MapToPaProgram(int? lob) => lob switch
    {
        3 or 4 => "STAR",
        _ => null
    };
}
