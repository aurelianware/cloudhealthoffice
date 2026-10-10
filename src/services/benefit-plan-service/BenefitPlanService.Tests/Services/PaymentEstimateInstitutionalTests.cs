using BenefitPlanService.Models.Estimate;
using BenefitPlanService.Services;
using CloudHealthOffice.BenefitEngine.Domain;
using CloudHealthOffice.BenefitEngine.Models;
using CloudHealthOffice.BenefitEngine.Services;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.OperatingMode;
using CloudHealthOffice.PriorAuthRuleEngine.Abstractions;
using CloudHealthOffice.PriorAuthRuleEngine.Domain;
using CloudHealthOffice.PriorAuthRuleEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BenefitPlanService.Tests.Services;

/// <summary>
/// Institutional estimate inputs — DRG, length of stay and line revenue codes — reach
/// the fee schedule engine the way adjudication sends them, and a stay the engine
/// prices as one claim-level amount (DRG case rate, all-inclusive per diem) is
/// cost-shared once by the benefit engine. Pricing runs on the real
/// <see cref="RateResolutionService"/> over in-memory schedules; the benefit engine is
/// mocked and its request captured.
/// </summary>
public class PaymentEstimateInstitutionalTests
{
    private const string Tenant = "tenant-A";
    private const string Npi = "1234567890";
    private static readonly Guid PlanId = Guid.NewGuid();

    private BenefitResolutionRequest? _benefitRequest;
    private List<PricingRequest>? _pricingRequests;

    // ── Inputs reach pricing ──────────────────────────────────────────

    [Fact]
    public async Task DrgLengthOfStayAndRevenueCodes_FlowToEveryPricingLine()
    {
        var sut = Build(DrgSchedule(12_000m));

        await sut.EstimateAsync(Tenant, Stay(drgCode: " 470 ", lengthOfStay: 4,
            Line(1, "", "0120", 12_000m), Line(2, "99223", " 0450 ", 1_500m)));

        _pricingRequests.Should().HaveCount(2);
        _pricingRequests.Should().OnlyContain(r => r.DrgCode == "470" && r.LengthOfStay == 4 && r.IsInstitutional);
        _pricingRequests!.Select(r => r.RevenueCode).Should().Equal("0120", "0450");
    }

    [Fact]
    public async Task NoInstitutionalInputs_PricingUnchanged()
    {
        var sut = Build(DrgSchedule(12_000m));

        await sut.EstimateAsync(Tenant, Stay(drgCode: null, lengthOfStay: null, Line(1, "99213", null, 200m)) with
        {
            ClaimType = "Professional",
            BillType = null,
        });

        var line = _pricingRequests.Should().ContainSingle().Subject;
        line.DrgCode.Should().BeNull();
        line.LengthOfStay.Should().BeNull();
        line.RevenueCode.Should().BeNull();
        _benefitRequest!.InpatientPricingMethod.Should().BeNull();
        _benefitRequest.DrgAllowedAmount.Should().BeNull();
    }

    // ── DRG case rate ─────────────────────────────────────────────────

    [Fact]
    public async Task DrgStay_CaseRatePaidOnce_AllocatedByBilled_CostSharedOncePerStay()
    {
        var sut = Build(DrgSchedule(12_000m));

        var resp = await sut.EstimateAsync(Tenant, Stay("470", 4,
            Line(1, "", "0120", 12_000m), Line(2, "", "0250", 1_500m), Line(3, "", "0360", 29_000m)));

        resp.Totals.AllowedAmount.Should().Be(12_000m);
        resp.Lines.Select(l => l.AllowedAmount).Should().Equal(3_388.23m, 423.52m, 8_188.25m);
        resp.Lines.Should().OnlyContain(l => l.Messages.Any(m => m.Code == "DRG_CASE_RATE_APPLIED"));
        resp.Lines.Select(l => l.RevenueCode).Should().Equal("0120", "0250", "0360");

        _benefitRequest!.InpatientPricingMethod.Should().Be(InpatientPricingMethod.DrgCaseRate);
        _benefitRequest.DrgAllowedAmount.Should().Be(12_000m);
        _benefitRequest.DrgCode.Should().Be("470");
        _benefitRequest.LengthOfStay.Should().Be(4);
        _benefitRequest.ExecutionMode.Should().Be(AdjudicationExecutionMode.Prospective);
    }

    [Fact]
    public async Task DrgSchedule_WithoutDrgCode_FallsBackToBilledCharges_NoGrouper()
    {
        // There is no MS-DRG grouper: without the DRG the stay finds no case rate.
        var sut = Build(DrgSchedule(12_000m));

        var resp = await sut.EstimateAsync(Tenant, Stay(drgCode: null, lengthOfStay: 4, Line(1, "", "0120", 9_000m)));

        resp.Lines.Single().AllowedAmount.Should().Be(9_000m);
        resp.Lines.Single().Messages.Should().Contain(m => m.Code == "BILLED_CHARGES_USED");
        _benefitRequest!.InpatientPricingMethod.Should().BeNull();
    }

    [Fact]
    public async Task DrgOnProfessionalClaim_NotCostSharedAsStay()
    {
        // Same rule as adjudication: only an 837I takes the per-stay benefit path.
        var sut = Build(DrgSchedule(12_000m));

        await sut.EstimateAsync(Tenant, Stay("470", null, Line(1, "99223", null, 15_000m)) with
        {
            ClaimType = "Professional",
            BillType = null,
        });

        _benefitRequest!.InpatientPricingMethod.Should().BeNull();
        _benefitRequest.DrgAllowedAmount.Should().BeNull();
    }

    // ── Per diem ──────────────────────────────────────────────────────

    [Fact]
    public async Task AllInclusivePerDiem_RateTimesLengthOfStay_PaidOncePerStay()
    {
        var sut = Build(new FeeSchedule
        {
            Id = "PD-ALL", TenantId = Tenant, Name = "Behavioral all-inclusive per diem",
            Type = FeeScheduleType.PerDiem, PerDiemRate = 1_500m,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines = [new FeeScheduleLine { RevenueCode = "0124", Rate = 1_500m }],
        });

        var resp = await sut.EstimateAsync(Tenant, Stay(null, 5,
            Line(1, "", "0124", 8_000m, units: 5), Line(2, "", "0250", 2_000m)));

        resp.Totals.AllowedAmount.Should().Be(7_500m); // 1,500 × 5 days, once
        resp.Lines.Select(l => l.AllowedAmount).Should().Equal(6_000m, 1_500m); // by billed share
        resp.Lines.Should().OnlyContain(l => l.Messages.Any(m => m.Code == "PER_DIEM_STAY_APPLIED"
            && m.Description.Contains("5 day(s)")));
        _benefitRequest!.InpatientPricingMethod.Should().Be(InpatientPricingMethod.PerDiem);
        _benefitRequest.DrgAllowedAmount.Should().Be(7_500m);
        _benefitRequest.DrgCode.Should().BeNull();
    }

    [Fact]
    public async Task RevenueCodeDailyRates_PricePerLineByUnits_NotAsAStay()
    {
        var sut = Build(new FeeSchedule
        {
            Id = "PD-REV", TenantId = Tenant, Name = "Room and board by revenue code",
            Type = FeeScheduleType.PerDiem,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine { RevenueCode = "0124", Rate = 1_450m },
                new FeeScheduleLine { RevenueCode = "0204", Rate = 2_875m },
            ],
        });

        var resp = await sut.EstimateAsync(Tenant, Stay(null, 7,
            Line(1, "", "0124", 11_000m, units: 5), Line(2, "", "0204", 9_400m, units: 2)));

        resp.Lines.Select(l => l.AllowedAmount).Should().Equal(5 * 1_450m, 2 * 2_875m);
        resp.Lines.Should().OnlyContain(l => l.Messages.Any(m => m.Code == "FEE_SCHEDULE_APPLIED"));
        resp.Lines.Should().NotContain(l => l.Messages.Any(m => m.Code == "PER_DIEM_STAY_APPLIED"));
        _benefitRequest!.InpatientPricingMethod.Should().BeNull();
    }

    [Fact]
    public async Task RevenueCodeOnlyLine_ExcludedFromPriorAuthProcedureCodes()
    {
        PaRuleContext? context = null;
        var sut = Build(DrgSchedule(12_000m), onPriorAuth: c => context = c);

        await sut.EstimateAsync(Tenant, Stay("470", 3, Line(1, "", "0120", 5_000m), Line(2, "99223", null, 400m)));

        context!.ProcedureCodes.Should().Equal("99223");
    }

    // ── Harness ───────────────────────────────────────────────────────

    private static FeeSchedule DrgSchedule(decimal caseRate) => new()
    {
        Id = "DRG-1", TenantId = Tenant, Name = "Hospital DRG",
        Type = FeeScheduleType.Drg,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = [new FeeScheduleLine { ProcedureCode = "470", Rate = caseRate }],
    };

    private PaymentEstimateService Build(FeeSchedule planDefault, Action<PaRuleContext>? onPriorAuth = null)
    {
        var schedules = new Mock<IFeeScheduleRepository>();
        schedules.Setup(r => r.GetDefaultForPlanAsync(Tenant, It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(planDefault);
        var contracts = new Mock<IProviderContractRepository>();
        var engine = new RateResolutionService(schedules.Object, contracts.Object, NullLogger<RateResolutionService>.Instance);

        var rate = new Mock<IRateResolutionService>();
        rate.Setup(r => r.ResolveBatchAsync(It.IsAny<IReadOnlyList<PricingRequest>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<PricingRequest> reqs, CancellationToken ct) =>
            {
                _pricingRequests = reqs.ToList();
                return engine.ResolveBatchAsync(reqs, ct);
            });

        var benefit = new Mock<IBenefitCalculationEngine>();
        benefit.Setup(b => b.CalculateAsync(It.IsAny<BenefitResolutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<BenefitResolutionRequest, CancellationToken>((req, _) => _benefitRequest = req)
            .ReturnsAsync((BenefitResolutionRequest req, CancellationToken _) => new BenefitResolutionResult
            {
                Success = true,
                Lines = req.Lines.Select(l => new LineBenefitResult
                {
                    LineNumber = l.LineNumber,
                    IsCovered = true,
                    ServiceTypeCode = "48",
                    AllowedAmount = req.AllowedAmounts.GetValueOrDefault(l.LineNumber, l.BilledAmount),
                    BilledAmount = l.BilledAmount,
                    PlanPaidAmount = req.AllowedAmounts.GetValueOrDefault(l.LineNumber, l.BilledAmount),
                }).ToList(),
            });

        var integrity = new Mock<IProviderIntegrityGate>();
        integrity.Setup(g => g.CheckAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderIntegrityResult { Passed = true, IntegrityScore = 95, Rating = "Clear" });

        var priorAuth = new Mock<IPriorAuthRuleEngine>();
        priorAuth.Setup(p => p.EvaluateAsync(It.IsAny<PaRuleContext>(), It.IsAny<CancellationToken>()))
            .Callback<PaRuleContext, CancellationToken>((c, _) => onPriorAuth?.Invoke(c))
            .ReturnsAsync(new PaRuleDecision
            {
                Outcome = PaDecisionOutcome.Pend,
                FiringRuleId = "NoRuleMatch",
                FiringRuleName = "No rule matched",
                ResolvedRuleSetKey = "TX/Medicaid",
            });

        var operatingMode = new Mock<IOperatingModeProvider>();
        operatingMode.Setup(o => o.GetConfigurationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperatingModeConfiguration { TenantId = Tenant });

        return new PaymentEstimateService(
            rate.Object, benefit.Object, integrity.Object, priorAuth.Object,
            operatingMode.Object, new ClaimTypeRouter(), NullLogger<PaymentEstimateService>.Instance);
    }

    private static PaymentEstimateRequest Stay(string? drgCode, int? lengthOfStay, params PaymentEstimateLineRequest[] lines) => new()
    {
        MemberId = "member-1",
        BenefitPlanId = PlanId,
        ProviderNpi = Npi,
        ServiceDate = new DateOnly(2026, 3, 2),
        ClaimType = "Institutional",
        BillType = "111",
        DrgCode = drgCode,
        LengthOfStay = lengthOfStay,
        Lines = lines.ToList(),
    };

    private static PaymentEstimateLineRequest Line(
        int n, string procedureCode, string? revenueCode, decimal charge, decimal units = 1) => new()
    {
        LineNumber = n,
        ProcedureCode = procedureCode,
        RevenueCode = revenueCode,
        ChargeAmount = charge,
        Units = units,
        PlaceOfService = "21",
    };
}
