using BenefitPlanService.Models.Estimate;
using BenefitPlanService.Services;
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
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PaymentEstimateService"/> — the orchestration,
/// mapping, confidence and totals logic on top of the (separately tested)
/// pricing and benefit engines. The engines are mocked so each scenario can
/// be driven precisely.
/// </summary>
public class PaymentEstimateServiceTests
{
    private const string Tenant = "tenant-A";
    private static readonly Guid PlanId = Guid.NewGuid();

    // ── Harness ─────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public Mock<IRateResolutionService> Rate { get; } = new();
        public Mock<IBenefitCalculationEngine> Benefit { get; } = new();
        public Mock<IProviderIntegrityGate> Integrity { get; } = new();
        public Mock<IPriorAuthRuleEngine> PriorAuth { get; } = new();
        public Mock<IOperatingModeProvider> OperatingMode { get; } = new();
        public IClaimTypeRouter Router { get; } = new ClaimTypeRouter();

        public List<PricingRequest>? CapturedPricingRequests { get; private set; }
        public BenefitResolutionRequest? CapturedBenefitRequest { get; private set; }

        public Harness()
        {
            // Sensible advisory defaults: provider passes, no PA required,
            // operating mode empty (→ Replace/authoritative by default).
            Integrity.Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ProviderIntegrityResult { Passed = true, IntegrityScore = 95, Rating = "Clear" });

            PriorAuth.Setup(p => p.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaRuleDecision
                {
                    Outcome = PaDecisionOutcome.Pend,
                    FiringRuleId = "NoRuleMatch",
                    FiringRuleName = "No rule matched",
                    ResolvedRuleSetKey = "TX/Medicaid"
                });

            OperatingMode.Setup(o => o.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperatingModeConfiguration { TenantId = Tenant });
        }

        public void SetupPricing(PricingResultSet result)
        {
            Rate.Setup(r => r.ResolveBatchAsync(It.IsAny<IReadOnlyList<PricingRequest>>(), It.IsAny<CancellationToken>()))
                .Callback<IReadOnlyList<PricingRequest>, CancellationToken>((reqs, _) => CapturedPricingRequests = reqs.ToList())
                .ReturnsAsync(result);
        }

        public void SetupBenefit(BenefitResolutionResult result)
        {
            Benefit.Setup(b => b.CalculateAsync(It.IsAny<BenefitResolutionRequest>(), It.IsAny<CancellationToken>()))
                .Callback<BenefitResolutionRequest, CancellationToken>((req, _) => CapturedBenefitRequest = req)
                .ReturnsAsync(result);
        }

        public PaymentEstimateService Build() => new(
            Rate.Object, Benefit.Object, Integrity.Object, PriorAuth.Object,
            OperatingMode.Object, Router, NullLogger<PaymentEstimateService>.Instance);
    }

    // ── Builders ────────────────────────────────────────────────────────

    private static PricingResultSet Pricing(params (int line, decimal billed, decimal allowed, RateSource src)[] lines)
        => new()
        {
            LineResults = lines.Select(l => new PricingResult
            {
                LineNumber = l.line,
                ProcedureCode = "X",
                BilledAmount = l.billed,
                AllowedAmount = l.allowed,
                FeeScheduleType = FeeScheduleType.Commercial,
                FeeScheduleName = "Commercial PPO",
                NetworkStatus = NetworkStatus.InNetwork,
                RateSource = l.src
            }).ToList()
        };

    private static LineBenefitResult PayableLine(
        int line, decimal allowed, decimal deductible = 0, decimal copay = 0,
        decimal coinsurance = 0, decimal? planPaid = null)
    {
        var memberResp = deductible + copay + coinsurance;
        return new LineBenefitResult
        {
            LineNumber = line,
            IsCovered = true,
            ServiceTypeCode = "98",
            ServiceTypeDescription = "Office Visit",
            AllowedAmount = allowed,
            BilledAmount = allowed,
            DeductibleAmount = deductible,
            CopayAmount = copay,
            CoinsuranceAmount = coinsurance,
            CoinsurancePercent = 0.20m,
            MemberResponsibility = memberResp,
            PlanPaidAmount = planPaid ?? (allowed - memberResp)
        };
    }

    private static LineBenefitResult DeniedLine(int line, decimal allowed, string carc, string desc)
        => new()
        {
            LineNumber = line,
            IsCovered = false,
            ServiceTypeCode = "35",
            ServiceTypeDescription = "Dental",
            AllowedAmount = allowed,
            BilledAmount = allowed,
            DenialReasonCode = carc,
            DenialReasonDescription = desc,
            MemberResponsibility = 0,
            PlanPaidAmount = 0
        };

    private static BenefitResolutionResult Benefit(bool success, params LineBenefitResult[] lines)
        => new()
        {
            Success = success,
            Lines = lines.ToList(),
            Totals = new ClaimTotals
            {
                TotalBilled = lines.Sum(l => l.BilledAmount),
                TotalAllowed = lines.Sum(l => l.AllowedAmount),
                TotalDeductible = lines.Sum(l => l.DeductibleAmount),
                TotalCopay = lines.Sum(l => l.CopayAmount),
                TotalCoinsurance = lines.Sum(l => l.CoinsuranceAmount),
                TotalMemberResponsibility = lines.Sum(l => l.MemberResponsibility),
                TotalPlanPaid = lines.Sum(l => l.PlanPaidAmount)
            },
            AccumulatorSnapshot =
            [
                new AccumulatorState { Type = AccumulatorType.IndividualDeductible, Scope = AccumulatorScope.Individual }
            ]
        };

    private static PaymentEstimateRequest Request(params PaymentEstimateLineRequest[] lines)
        => new()
        {
            RequestId = "estimate-123",
            MemberId = "member-123",
            BenefitPlanId = PlanId,
            ProviderNpi = "1234567890",
            ServiceDate = new DateOnly(2026, 8, 15),
            ClaimType = "Dental",
            LineOfBusiness = "Dental",
            Lines = lines.ToList()
        };

    private static PaymentEstimateLineRequest Line(
        int n, string code, decimal charge, string? tooth = null)
        => new() { LineNumber = n, ProcedureCode = code, ChargeAmount = charge, ToothNumber = tooth, CodeType = "CDT" };

    // ── Tests ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SinglePayableLine_ProducesEstimatedResponse()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 275m, 210m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 210m, coinsurance: 42m)));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "D2392", 275m, tooth: "30")));

        resp.Status.Should().Be("estimated");
        resp.RequestId.Should().Be("estimate-123");
        resp.Currency.Should().Be("USD");

        var line = resp.Lines.Single();
        line.Status.Should().Be("payable");
        line.BilledAmount.Should().Be(275m);
        line.AllowedAmount.Should().Be(210m);
        line.ContractualAdjustment.Should().Be(65m);
        line.CoinsuranceAmount.Should().Be(42m);
        line.PayerResponsibility.Should().Be(168m);
        line.PatientResponsibility.Should().Be(42m);
        line.ToothNumber.Should().Be("30");
        line.Messages.Should().Contain(m => m.Code == "CONTRACTUAL_ADJUSTMENT");
        line.Messages.Should().Contain(m => m.Code == "COINSURANCE_APPLIED");
    }

    [Fact]
    public async Task ClaimTotals_EqualSumOfLineAmounts()
    {
        var h = new Harness();
        h.SetupPricing(
            Pricing((1, 275m, 210m, RateSource.ContractedRate),
                    (2, 100m, 80m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true,
            PayableLine(1, 210m, coinsurance: 42m),
            PayableLine(2, 80m, copay: 20m)));

        var resp = await h.Build().EstimateAsync(Tenant,
            Request(Line(1, "D2392", 275m), Line(2, "D0120", 100m)));

        resp.Totals.BilledAmount.Should().Be(resp.Lines.Sum(l => l.BilledAmount));
        resp.Totals.AllowedAmount.Should().Be(resp.Lines.Sum(l => l.AllowedAmount));
        resp.Totals.ContractualAdjustment.Should().Be(resp.Lines.Sum(l => l.ContractualAdjustment));
        resp.Totals.PayerResponsibility.Should().Be(resp.Lines.Sum(l => l.PayerResponsibility));
        resp.Totals.PatientResponsibility.Should().Be(resp.Lines.Sum(l => l.PatientResponsibility));
        resp.Totals.DeductibleAmount.Should().Be(resp.Lines.Sum(l => l.DeductibleAmount));
        resp.Totals.CopayAmount.Should().Be(resp.Lines.Sum(l => l.CopayAmount));
        resp.Totals.CoinsuranceAmount.Should().Be(resp.Lines.Sum(l => l.CoinsuranceAmount));

        resp.Totals.BilledAmount.Should().Be(375m);
        resp.Totals.CopayAmount.Should().Be(20m);
        resp.Totals.CoinsuranceAmount.Should().Be(42m);
    }

    [Fact]
    public async Task DeductibleAndCopay_SurfacedAsMessages()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, deductible: 100m, copay: 30m)));

        var line = (await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)))).Lines.Single();

        line.DeductibleAmount.Should().Be(100m);
        line.CopayAmount.Should().Be(30m);
        line.Messages.Should().Contain(m => m.Code == "DEDUCTIBLE_APPLIED");
        line.Messages.Should().Contain(m => m.Code == "COPAY_APPLIED");
    }

    [Fact]
    public async Task NonCoveredLine_MarkedNotCovered_WithDenialMessage()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 75m, 75m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(false, DeniedLine(1, 75m, "96", "Dental Care is not covered under this plan")));

        var line = (await h.Build().EstimateAsync(Tenant, Request(Line(1, "D0120", 75m)))).Lines.Single();

        line.Status.Should().Be("not_covered");
        line.PayerResponsibility.Should().Be(0m);
        line.Messages.Should().Contain(m => m.Code == "NON_COVERED_SERVICE" && m.Severity == EstimateMessageSeverity.Denial);
    }

    [Fact]
    public async Task InvalidProcedureCode_NoBenefitMapping_NeedsReview()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 100m, 100m, RateSource.BilledCharges)));
        h.SetupBenefit(Benefit(false, DeniedLine(1, 100m, "16", "No benefit category mapping for procedure code")));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "ZZZZZ", 100m)));

        resp.Lines.Single().Status.Should().Be("needs_review");
        resp.Lines.Single().Messages.Should().Contain(m => m.Code == "NO_BENEFIT_MAPPING");
        resp.Confidence.Level.Should().Be(EstimateConfidenceLevel.Low);
    }

    [Fact]
    public async Task MissingFeeSchedule_AddsWarningAndLowersConfidence()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 200m, RateSource.BilledCharges)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 200m, coinsurance: 40m)));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Lines.Single().Messages.Should().Contain(m => m.Code == "BILLED_CHARGES_USED");
        resp.Confidence.MissingData.Should().Contain(d => d.Contains("Fee schedule for line 1"));
        resp.Confidence.Level.Should().NotBe(EstimateConfidenceLevel.High);
    }

    [Fact]
    public async Task PriorAuthRequired_NoAuthNumber_AddsWarning()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 500m, 400m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 400m, coinsurance: 80m)));
        h.PriorAuth.Setup(p => p.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Deny,
                DenialCode = "197",
                DenialReason = "Prior authorization required",
                FiringRuleId = "PA-HIGH-COST",
                FiringRuleName = "High cost PA",
                ResolvedRuleSetKey = "TX/Medicaid"
            });

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "27447", 500m)));

        resp.Warnings.Should().Contain(w => w.Code == "PRIOR_AUTH_REQUIRED" && w.Severity == EstimateMessageSeverity.Warning);
    }

    [Fact]
    public async Task ProviderExcluded_AddsWarning_AndLowConfidence()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));
        h.Integrity.Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderIntegrityResult
            {
                Passed = false, IsExcluded = true,
                DenialReason = "Provider excluded from federal programs", DenialCode = "B7"
            });

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Warnings.Should().Contain(w => w.Code == "PROVIDER_EXCLUDED");
        resp.Confidence.Level.Should().Be(EstimateConfidenceLevel.Low);
    }

    [Fact]
    public async Task PlanNotFound_ReturnsInsufficientData()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 200m, RateSource.BilledCharges)));
        h.SetupBenefit(new BenefitResolutionResult
        {
            Success = false,
            DenialReasonCode = "16",
            DenialReasonDescription = "Benefit plan not found",
            Lines = []
        });

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Status.Should().Be("insufficient_data");
        resp.Confidence.Level.Should().Be(EstimateConfidenceLevel.InsufficientData);
        resp.Lines.Should().BeEmpty();
        resp.Warnings.Should().Contain(w => w.Code == "BENEFIT_PLAN_UNRESOLVED");
    }

    [Fact]
    public async Task BenefitEngine_InvokedInProspectiveReadOnlyMode()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));

        await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        h.CapturedBenefitRequest.Should().NotBeNull();
        h.CapturedBenefitRequest!.ExecutionMode.Should().Be(AdjudicationExecutionMode.Prospective);
    }

    [Fact]
    public async Task TenantContext_FromCaller_FlowsToPricingRequests()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));

        await h.Build().EstimateAsync("tenant-XYZ", Request(Line(1, "99213", 200m)));

        h.CapturedPricingRequests.Should().NotBeNull();
        h.CapturedPricingRequests!.Should().OnlyContain(r => r.TenantId == "tenant-XYZ");
    }

    [Fact]
    public async Task Authority_IsSimulation_WhenBenefitEngineInAugmentMode()
    {
        var h = new Harness();
        var config = new OperatingModeConfiguration { TenantId = Tenant };
        config.SetEngineMode(OperatingModeConfiguration.EngineNames.BenefitCalculation, EngineOperatingMode.Augment);
        h.OperatingMode.Setup(o => o.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Authority.Should().Be(EstimateAuthority.Simulation);
    }

    [Fact]
    public async Task Authority_IsAuthoritativePayer_WhenChoIsReplaceMode()
    {
        var h = new Harness(); // default config → Replace / authoritative
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Authority.Should().Be(EstimateAuthority.AuthoritativePayer);
    }

    [Fact]
    public async Task DuplicatePricingLineNumbers_DoNotThrow()
    {
        // Defense-in-depth: even if the pricing engine ever returns duplicate
        // line numbers, the service must produce a response rather than 500.
        var h = new Harness();
        h.SetupPricing(Pricing(
            (1, 275m, 210m, RateSource.ContractedRate),
            (1, 100m, 80m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 210m, coinsurance: 42m)));

        var act = async () => await h.Build().EstimateAsync(Tenant, Request(Line(1, "D2392", 275m)));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Disclaimer_AlwaysPresent()
    {
        var h = new Harness();
        h.SetupPricing(Pricing((1, 200m, 150m, RateSource.ContractedRate)));
        h.SetupBenefit(Benefit(true, PayableLine(1, 150m, coinsurance: 30m)));

        var resp = await h.Build().EstimateAsync(Tenant, Request(Line(1, "99213", 200m)));

        resp.Disclaimer.Should().NotBeNullOrWhiteSpace();
        resp.Disclaimer.Should().Contain("Estimate only");
    }
}
