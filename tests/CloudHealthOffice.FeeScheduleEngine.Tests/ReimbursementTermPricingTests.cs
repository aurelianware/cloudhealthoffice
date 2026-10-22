using System.Globalization;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudHealthOffice.FeeScheduleEngine.Tests;

/// <summary>
/// The canonical contract model of ADR 016 §2: a contract's ordered
/// <see cref="ReimbursementTerm"/> list, evaluated by the one pricing engine.
///
/// <para>
/// Phase 2 is additive and changes no behaviour, so the load-bearing tests here
/// are the parity ones: a term-shaped contract and the legacy contract it was
/// built from must price a claim identically, line for line and adjustment for
/// adjustment. The rest cover what terms add — a fee schedule percentage, a
/// percentage of Medicare or of billed charges, capitation, scoping, mid-term
/// effective dates — and the fail-closed rules for a term the engine cannot
/// price.
/// </para>
/// </summary>
public class ReimbursementTermPricingTests
{
    private const string Tenant = "test-tenant";
    private const string PlanId = "plan-001";
    private const string ProviderNpi = "1234567890";
    private const string MainScheduleId = "comm-main";
    private const string CarveOutScheduleId = "comm-carveout";
    private const string MpfsScheduleId = "mpfs-2026";
    private const string PlanDefaultScheduleId = "plan-default";

    // ═══════════════════════════════════════════════════════════════════
    // PARITY: A TERM-SHAPED CONTRACT PRICES LIKE ITS LEGACY EQUIVALENT
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("99213", 110m)]
    [InlineData("27447", 1500m)]
    public async Task DefaultSchedule_TermMatchesLegacy(string code, decimal expected)
    {
        var legacy = Legacy(contract => contract.FeeScheduleId = MainScheduleId);
        var canonical = Canonical(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var (fromLegacy, fromTerms) = await PriceBoth(legacy, canonical, Request(code));

        AssertSamePricing(fromLegacy, fromTerms);
        Assert.Equal(expected, fromTerms.AllowedAmount);
        Assert.Equal(MainScheduleId, fromTerms.FeeScheduleId);
    }

    [Theory]
    // 27400–27599 is routed to the carve-out schedule by both shapes; 99213 is not.
    [InlineData("27447", 1800m, CarveOutScheduleId)]
    [InlineData("99213", 110m, MainScheduleId)]
    public async Task CodeRangeCarveOut_TermMatchesLegacy(string code, decimal expected, string scheduleId)
    {
        var legacy = Legacy(contract =>
        {
            contract.FeeScheduleId = MainScheduleId;
            contract.ContractLines =
            [
                new ProviderContractLine
                {
                    ProcedureCodeFrom = "27400", ProcedureCodeTo = "27599",
                    FeeScheduleId = CarveOutScheduleId,
                },
            ];
        });

        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId, scope: new ReimbursementTermScope
            {
                ProcedureCodeFrom = "27400", ProcedureCodeTo = "27599",
            }),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var (fromLegacy, fromTerms) = await PriceBoth(legacy, canonical, Request(code));

        AssertSamePricing(fromLegacy, fromTerms);
        Assert.Equal(expected, fromTerms.AllowedAmount);
        Assert.Equal(scheduleId, fromTerms.FeeScheduleId);
    }

    [Fact]
    public async Task Modifiers_And_MultipleProcedureRanking_TermMatchesLegacy()
    {
        var legacy = Legacy(contract => contract.FeeScheduleId = MainScheduleId);
        var canonical = Canonical(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        // Two surgical lines, both indicator 2: the higher is paid in full, the
        // lower at 50%, and the lower also carries a bilateral modifier.
        var claim = new List<PricingRequest>
        {
            Request("27447", billed: 4000m, lineNumber: 1, totalLines: 2),
            Request("29881", billed: 1200m, lineNumber: 2, totalLines: 2, modifiers: ["50"]),
        };

        var fromLegacy = await Engine(legacy).ResolveBatchAsync(claim);
        var fromTerms = await Engine(canonical).ResolveBatchAsync(claim);

        AssertSamePricing(fromLegacy, fromTerms);
        Assert.Equal(1500m, fromTerms.LineResults[0].AllowedAmount);
        Assert.Equal(600m, fromTerms.LineResults[1].AllowedAmount); // 800 × 150% × 50%
    }

    [Fact]
    public async Task LesserOfBilled_ContractLevel_TermMatchesLegacy()
    {
        var legacy = Legacy(contract =>
        {
            contract.FeeScheduleId = MainScheduleId;
            contract.LesserOfBilledCharges = true;
        });

        var canonical = Canonical(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));
        canonical.LesserOfBilledCharges = true;

        var (fromLegacy, fromTerms) = await PriceBoth(legacy, canonical, Request("27447", billed: 900m));

        AssertSamePricing(fromLegacy, fromTerms);
        Assert.Equal(900m, fromTerms.AllowedAmount);
        Assert.True(fromTerms.LesserOfBilledApplied);
    }

    [Fact]
    public async Task DrgCaseRate_TermMatchesLegacy_AndIsStillPaidOncePerStay()
    {
        var legacy = Legacy(contract => contract.FeeScheduleId = DrgScheduleId);
        var canonical = Canonical(Term(ReimbursementMethod.CaseRate, DrgScheduleId));

        var claim = new List<PricingRequest>
        {
            InpatientRequest("99231", billed: 6000m, lineNumber: 1, totalLines: 2),
            InpatientRequest("99232", billed: 2000m, lineNumber: 2, totalLines: 2),
        };

        var fromLegacy = await Engine(legacy).ResolveBatchAsync(claim);
        var fromTerms = await Engine(canonical).ResolveBatchAsync(claim);

        AssertSamePricing(fromLegacy, fromTerms);
        // One $12,000 case rate for the stay, allocated 75% / 25% by billed charges.
        Assert.Equal(12_000m, fromTerms.TotalAllowedAmount);
        Assert.Equal(9_000m, fromTerms.LineResults[0].AllowedAmount);
        Assert.Equal(3_000m, fromTerms.LineResults[1].AllowedAmount);
        Assert.All(fromTerms.LineResults, r => Assert.True(r.IsPerStayRate));
    }

    [Fact]
    public async Task ContractWithTerms_DoesNotReadItsLegacyFields()
    {
        // The legacy fields point at the carve-out schedule, the term at the
        // main one. Terms are authoritative, so the main schedule's rate wins.
        var canonical = Canonical(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));
        canonical.FeeScheduleId = CarveOutScheduleId;
        canonical.ContractLines =
        [
            new ProviderContractLine { ProcedureCodeFrom = "27447", FeeScheduleId = CarveOutScheduleId },
        ];

        var result = await Engine(canonical).ResolveAsync(Request("27447"));

        Assert.Equal(1500m, result.AllowedAmount);
        Assert.Equal(MainScheduleId, result.FeeScheduleId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // METHODS
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FeeScheduleRef_Multiplier_PaysThatPercentageOfTheSchedule()
    {
        // The FfsRateConfig shape: 110% of a fee schedule.
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.Multiplier = 1.10m;

        var result = await Engine(Canonical(term)).ResolveAsync(Request("99213"));

        Assert.Equal(121m, result.AllowedAmount);
        Assert.Equal(121m, result.BaseAmount);
        var adjustment = Assert.Single(result.Adjustments);
        Assert.Equal(1.10m, adjustment.AdjustmentFactor);
        Assert.Equal(11m, adjustment.AdjustmentAmount);
    }

    [Fact]
    public async Task FeeScheduleRef_Multiplier_AppliesBeforeModifiersAndUnits()
    {
        // $110 × 110% = $121 base; × 150% bilateral = $181.50; × 2 units = $363.
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.Multiplier = 1.10m;

        var result = await Engine(Canonical(term))
            .ResolveAsync(Request("99213", billed: 900m, units: 2m, modifiers: ["50"]));

        Assert.Equal(363m, result.AllowedAmount);
        Assert.Equal(121m, result.BaseAmount);
        Assert.Collection(result.Adjustments,
            a => Assert.Equal(1.10m, a.AdjustmentFactor),
            a => Assert.Equal(PaymentModifiers.Bilateral, a.Modifier));
    }

    [Fact]
    public async Task PercentOfMedicare_PricesFromTheBaseScheduleAndAppliesThePercentage()
    {
        var term = Term(ReimbursementMethod.PercentOfMedicare, feeScheduleId: null);
        term.BaseScheduleId = MpfsScheduleId;
        term.Percent = 0.80m;

        var result = await Engine(Canonical(term)).ResolveAsync(Request("99213"));

        // The MPFS schedule allows $100 for 99213; the contract pays 80%.
        Assert.Equal(80m, result.AllowedAmount);
        Assert.Equal(MpfsScheduleId, result.FeeScheduleId);
        Assert.Equal(RateSource.MedicareMpfs, result.RateSource);
    }

    [Fact]
    public async Task PercentOfBilled_PaysThatShareOfTheLineCharge_AndReadsNoSchedule()
    {
        var term = Term(ReimbursementMethod.PercentOfBilled, feeScheduleId: null);
        term.Percent = 0.65m;

        var result = await Engine(Canonical(term), withSchedules: false)
            .ResolveAsync(Request("99213", billed: 400m, units: 3m));

        // Billed is the line total, so units are not applied again.
        Assert.Equal(260m, result.AllowedAmount);
        Assert.Equal(260m, result.BaseAmount);
        Assert.Equal(RateSource.ContractedRate, result.RateSource);
        Assert.Null(result.FeeScheduleId);
        Assert.Equal(140m, result.ContractualAdjustment);
    }

    [Fact]
    public async Task Capitation_PricesAtZero_AndIsReportedAsCapitated()
    {
        var result = await Engine(Canonical(Term(ReimbursementMethod.Capitation, feeScheduleId: null)),
                withSchedules: false)
            .ResolveAsync(Request("99213", billed: 400m));

        Assert.Equal(0m, result.AllowedAmount);
        Assert.Equal(RateSource.Capitation, result.RateSource);
        Assert.Equal(FeeScheduleType.Capitation, result.FeeScheduleType);
        Assert.Null(result.UnresolvedReason);
        Assert.Contains("capitation", Assert.Single(result.Adjustments).Description);
    }

    [Fact]
    public async Task Capitation_IsNotLoweredByLesserOfBilled()
    {
        var canonical = Canonical(Term(ReimbursementMethod.Capitation, feeScheduleId: null));
        canonical.LesserOfBilledCharges = true;

        var result = await Engine(canonical, withSchedules: false).ResolveAsync(Request("99213", billed: 400m));

        Assert.Equal(0m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
    }

    // ═══════════════════════════════════════════════════════════════════
    // FAIL CLOSED
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StopLossProvision_PendsTheLineRatherThanIgnoringIt()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.StopLoss = new StopLossTerm { ThresholdBilled = 100_000m, PercentAboveThreshold = 0.80m };

        var result = await Engine(Canonical(term)).ResolveAsync(Request("99213"));

        Assert.Equal(0m, result.AllowedAmount);
        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Contains("stop-loss", result.UnresolvedReason);
    }

    [Fact]
    public async Task OutlierProvision_PendsTheLine()
    {
        var term = Term(ReimbursementMethod.CaseRate, DrgScheduleId);
        term.Outlier = new OutlierTerm { FixedLossThreshold = 50_000m, MarginalCostFactor = 0.80m };

        var result = await Engine(Canonical(term)).ResolveAsync(InpatientRequest("99231"));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Contains("cost outlier", result.UnresolvedReason);
    }

    [Fact]
    public async Task TermNamingAMissingSchedule_PendsRatherThanPayingThePlanDefault()
    {
        var result = await Engine(Canonical(Term(ReimbursementMethod.FeeScheduleRef, "no-such-schedule")))
            .ResolveAsync(Request("99213"));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Contains("no-such-schedule", result.UnresolvedReason);
    }

    [Fact]
    public async Task TermWithNoFeeSchedule_PendsTheLine()
    {
        var result = await Engine(Canonical(Term(ReimbursementMethod.FeeScheduleRef, feeScheduleId: null)))
            .ResolveAsync(Request("99213"));

        Assert.Equal(RateSource.Unresolved, result.RateSource);
        Assert.Contains("names no fee schedule", result.UnresolvedReason);
    }

    [Fact]
    public async Task LegacyContractNamingAMissingSchedule_StillFallsBackToThePlanDefault()
    {
        // Unchanged legacy behaviour: only a term pends on a missing schedule.
        var result = await Engine(Legacy(contract => contract.FeeScheduleId = "no-such-schedule"))
            .ResolveAsync(Request("99213"));

        Assert.Equal(PlanDefaultScheduleId, result.FeeScheduleId);
        Assert.Equal(95m, result.AllowedAmount);
    }

    [Fact]
    public async Task NoTermCoversTheLine_FallsThroughToThePlanDefault()
    {
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId, scope: new ReimbursementTermScope
            {
                ProcedureCodeFrom = "27400", ProcedureCodeTo = "27599",
            }));

        var result = await Engine(canonical).ResolveAsync(Request("99213"));

        Assert.Equal(PlanDefaultScheduleId, result.FeeScheduleId);
        Assert.Equal(95m, result.AllowedAmount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SCOPE AND ORDER
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task FirstMatchingTermWins()
    {
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var result = await Engine(canonical).ResolveAsync(Request("27447"));

        Assert.Equal(CarveOutScheduleId, result.FeeScheduleId);
    }

    [Theory]
    [InlineData("0120", true)]
    [InlineData("120", true)]   // keyed without the leading zero: the same code
    [InlineData("0450", false)]
    [InlineData(null, false)]
    public async Task RevenueCodeScope_MatchesOnTheFourDigitForm(string? revenueCode, bool covered)
    {
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId, scope: new ReimbursementTermScope
            {
                RevenueCodes = ["0120"],
            }),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var result = await Engine(canonical).ResolveAsync(Request("27447", revenueCode: revenueCode));

        Assert.Equal(covered ? CarveOutScheduleId : MainScheduleId, result.FeeScheduleId);
    }

    [Theory]
    [InlineData("11", true)]
    [InlineData("22", false)]
    public async Task PlaceOfServiceScope_MatchesAProfessionalLinesPos(string pos, bool covered)
    {
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId, scope: new ReimbursementTermScope
            {
                PlacesOfService = ["11", "49"],
            }),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var result = await Engine(canonical).ResolveAsync(Request("27447", pos: pos));

        Assert.Equal(covered ? CarveOutScheduleId : MainScheduleId, result.FeeScheduleId);
    }

    [Fact]
    public async Task PlaceOfServiceScope_NeverCoversAnInstitutionalLine()
    {
        // An institutional line's PlaceOfServiceCode holds the 837I facility
        // type code ("11" = hospital inpatient), not CMS place of service 11.
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId, scope: new ReimbursementTermScope
            {
                PlacesOfService = ["11"],
            }),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var result = await Engine(canonical)
            .ResolveAsync(Request("27447", pos: "11", billType: "111"));

        Assert.Equal(MainScheduleId, result.FeeScheduleId);
    }

    [Theory]
    [InlineData(ContractClaimType.Professional, null, false, true)]
    [InlineData(ContractClaimType.Professional, "111", false, false)]
    [InlineData(ContractClaimType.Institutional, "131", false, true)]
    [InlineData(ContractClaimType.Institutional, null, true, true)]
    [InlineData(ContractClaimType.Institutional, null, false, false)]
    [InlineData(ContractClaimType.InstitutionalInpatient, "111", false, true)]
    [InlineData(ContractClaimType.InstitutionalInpatient, "121", false, true)]
    [InlineData(ContractClaimType.InstitutionalInpatient, "131", false, false)]
    [InlineData(ContractClaimType.InstitutionalOutpatient, "131", false, true)]
    [InlineData(ContractClaimType.InstitutionalOutpatient, "111", false, false)]
    // Institutional with no readable type of bill: inpatient and outpatient
    // cannot be told apart, so neither scoped term covers the line.
    [InlineData(ContractClaimType.InstitutionalInpatient, null, true, false)]
    [InlineData(ContractClaimType.InstitutionalOutpatient, null, true, false)]
    public async Task ClaimTypeScope(
        ContractClaimType claimType, string? billType, bool institutionalFlag, bool covered)
    {
        var canonical = Canonical(
            Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId, scope: new ReimbursementTermScope
            {
                ClaimType = claimType,
            }),
            Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId));

        var request = Request("27447", pos: "21", billType: billType) with
        {
            IsInstitutional = institutionalFlag,
        };

        var result = await Engine(canonical).ResolveAsync(request);

        Assert.Equal(covered ? CarveOutScheduleId : MainScheduleId, result.FeeScheduleId);
    }

    [Theory]
    // A mid-term rate amendment: the carve-out schedule from 1 June 2026.
    [InlineData("2026-03-08", MainScheduleId)]
    [InlineData("2026-06-01", CarveOutScheduleId)]
    [InlineData("2026-12-31", CarveOutScheduleId)]
    public async Task TermEffectiveDates_SelectTheTermInForceOnTheServiceDate(
        string serviceDate, string expectedSchedule)
    {
        var amended = Term(ReimbursementMethod.FeeScheduleRef, CarveOutScheduleId);
        amended.EffectiveDate = new DateTime(2026, 6, 1);

        var original = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        original.TermDate = new DateTime(2026, 5, 31);

        var canonical = Canonical(amended, original);

        var result = await Engine(canonical)
            .ResolveAsync(Request("27447") with
            {
                ServiceDate = DateTime.Parse(serviceDate, CultureInfo.InvariantCulture),
            });

        Assert.Equal(expectedSchedule, result.FeeScheduleId);
    }

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIER RULES (THE FfsRateConfig TOGGLES)
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task BilateralAdjustmentOff_SuppressesTheModifier50Adjustment()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.ModifierRules = new TermModifierRules { BilateralAdjustment = false };

        var result = await Engine(Canonical(term))
            .ResolveAsync(Request("99213", billed: 900m, modifiers: ["50"]));

        Assert.Equal(110m, result.AllowedAmount);
        Assert.Empty(result.Adjustments);
    }

    [Fact]
    public async Task MultipleProcedureReductionOff_LeavesEveryLineAtFullRate()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.ModifierRules = new TermModifierRules { MultipleProcedureReduction = false };

        var claim = new List<PricingRequest>
        {
            Request("27447", billed: 4000m, lineNumber: 1, totalLines: 2),
            Request("29881", billed: 1200m, lineNumber: 2, totalLines: 2),
        };

        var withReduction = await Engine(Canonical(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId)))
            .ResolveBatchAsync(claim);
        var withoutReduction = await Engine(Canonical(term)).ResolveBatchAsync(claim);

        Assert.Equal(400m, withReduction.LineResults[1].AllowedAmount);
        Assert.Equal(800m, withoutReduction.LineResults[1].AllowedAmount);
        Assert.Equal(1500m, withoutReduction.LineResults[0].AllowedAmount);
    }

    [Fact]
    public async Task TermLesserOfBilled_OverridesTheContractSetting()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.LesserOfBilled = false;

        var canonical = Canonical(term);
        canonical.LesserOfBilledCharges = true;

        var result = await Engine(canonical).ResolveAsync(Request("27447", billed: 900m));

        Assert.Equal(1500m, result.AllowedAmount);
        Assert.False(result.LesserOfBilledApplied);
    }

    [Fact]
    public async Task TermMethodNotMatchingItsSchedule_WarnsWithoutChangingTheAmount()
    {
        // A case-rate term pointing at a commercial schedule: the schedule still
        // decides how the line prices, but the mismatch is a loading error.
        var result = await Engine(Canonical(Term(ReimbursementMethod.CaseRate, MainScheduleId)))
            .ResolveAsync(Request("99213"));

        Assert.Equal(110m, result.AllowedAmount);
        Assert.Contains(result.Warnings, w => w.Contains("CaseRate") && w.Contains("Commercial"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // VALIDATION AND CLAIM-KIND READING
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_AcceptsAWellFormedTerm()
        => Assert.Empty(Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId).Validate());

    [Fact]
    public void Validate_RejectsAFeeScheduleRefWithNoSchedule()
    {
        var errors = Term(ReimbursementMethod.FeeScheduleRef, feeScheduleId: null).Validate();
        Assert.Contains(errors, e => e.Contains("feeScheduleId is required"));
    }

    [Fact]
    public void Validate_RejectsFieldsThatDoNotBelongToTheMethod()
    {
        var term = Term(ReimbursementMethod.PercentOfBilled, feeScheduleId: MainScheduleId);
        term.Percent = 0.8m;

        Assert.Contains(term.Validate(), e => e.Contains("feeScheduleId is set"));
    }

    [Fact]
    public void Validate_RejectsAPlaceOfServiceScopeOnAnInstitutionalTerm()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId, scope: new ReimbursementTermScope
        {
            PlacesOfService = ["21"],
            ClaimType = ContractClaimType.InstitutionalInpatient,
        });

        Assert.Contains(term.Validate(), e => e.Contains("would never match"));
    }

    [Fact]
    public void Validate_RejectsAnInvertedDateWindow()
    {
        var term = Term(ReimbursementMethod.FeeScheduleRef, MainScheduleId);
        term.EffectiveDate = new DateTime(2026, 6, 1);
        term.TermDate = new DateTime(2026, 1, 1);

        Assert.Contains(term.Validate(), e => e.Contains("termDate is before effectiveDate"));
    }

    [Theory]
    [InlineData("111", true)]    // hospital inpatient Part A
    [InlineData("121", true)]    // hospital inpatient Part B
    [InlineData("0211", true)]   // skilled nursing inpatient Part A
    [InlineData("131", false)]   // hospital outpatient
    [InlineData("141", false)]   // hospital "other" outpatient
    [InlineData("231", false)]   // skilled nursing outpatient
    [InlineData("711", null)]    // clinic: no inpatient / outpatient reading
    [InlineData("181", null)]    // hospital swing bed
    [InlineData(null, null)]
    [InlineData("N/A", null)]
    public void InstitutionalClaimKind_ReadsInpatientFromTheTypeOfBill(string? billType, bool? expected)
        => Assert.Equal(expected, InstitutionalClaimKind.IsInpatient(Request("99213", billType: billType)));

    // ═══════════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════════

    private const string DrgScheduleId = "drg-2026";

    private static ReimbursementTerm Term(
        ReimbursementMethod method, string? feeScheduleId, ReimbursementTermScope? scope = null) => new()
    {
        Method = method,
        FeeScheduleId = feeScheduleId,
        Scope = scope ?? new ReimbursementTermScope(),
    };

    /// <summary>A contract in the legacy shape: no terms, FeeScheduleId and ContractLines.</summary>
    private static ProviderContract Legacy(Action<ProviderContract> configure)
    {
        var contract = BareContract();
        configure(contract);
        return contract;
    }

    /// <summary>A contract in the canonical shape: an ordered list of terms.</summary>
    private static ProviderContract Canonical(params ReimbursementTerm[] terms)
    {
        var contract = BareContract();
        contract.ReimbursementTerms = terms.ToList();
        contract.LegalContractId = "legal-contract-1";
        contract.ContractNumber = $"CTR-{ProviderNpi}-2026";
        contract.LineOfBusiness = "Commercial";
        contract.PlanIds = [PlanId];
        return contract;
    }

    private static ProviderContract BareContract() => new()
    {
        Id = ProviderContract.MakeId(Tenant, ProviderNpi, PlanId),
        TenantId = Tenant,
        ProviderNpi = ProviderNpi,
        PlanId = PlanId,
        NetworkStatus = NetworkStatus.InNetwork,
        EffectiveDate = new DateTime(2026, 1, 1),
    };

    private static RateResolutionService Engine(ProviderContract contract, bool withSchedules = true)
    {
        // The plan default is what a line with no contracted schedule falls back to.
        var repo = new InMemoryFeeScheduleRepo(withSchedules ? PlanDefaultSchedule() : null);
        if (withSchedules)
        {
            repo.AddSchedule(MainSchedule());
            repo.AddSchedule(CarveOutSchedule());
            repo.AddSchedule(MpfsSchedule());
            repo.AddSchedule(DrgSchedule());
        }

        return new RateResolutionService(
            repo, new FixedContractRepo(contract), NullLogger<RateResolutionService>.Instance);
    }

    private static async Task<(PricingResult Legacy, PricingResult Terms)> PriceBoth(
        ProviderContract legacy, ProviderContract canonical, PricingRequest request)
        => (await Engine(legacy).ResolveAsync(request), await Engine(canonical).ResolveAsync(request));

    /// <summary>
    /// Everything the 835 and the benefit engine read from a priced line, so a
    /// term-shaped contract that agrees here is indistinguishable downstream.
    /// </summary>
    private static void AssertSamePricing(PricingResult legacy, PricingResult terms)
    {
        Assert.Equal(legacy.AllowedAmount, terms.AllowedAmount);
        Assert.Equal(legacy.BaseAmount, terms.BaseAmount);
        Assert.Equal(legacy.RateSource, terms.RateSource);
        Assert.Equal(legacy.FeeScheduleType, terms.FeeScheduleType);
        Assert.Equal(legacy.FeeScheduleId, terms.FeeScheduleId);
        Assert.Equal(legacy.NetworkStatus, terms.NetworkStatus);
        Assert.Equal(legacy.IsPerStayRate, terms.IsPerStayRate);
        Assert.Equal(legacy.LesserOfBilledApplied, terms.LesserOfBilledApplied);
        Assert.Equal(legacy.UnresolvedReason, terms.UnresolvedReason);
        Assert.Equal(
            legacy.Adjustments.Select(a => (a.Modifier, a.AdjustmentFactor, a.AdjustmentAmount)),
            terms.Adjustments.Select(a => (a.Modifier, a.AdjustmentFactor, a.AdjustmentAmount)));
        Assert.Equal(legacy.Warnings, terms.Warnings);
    }

    private static void AssertSamePricing(PricingResultSet legacy, PricingResultSet terms)
    {
        Assert.Equal(legacy.LineResults.Count, terms.LineResults.Count);
        for (var i = 0; i < legacy.LineResults.Count; i++)
            AssertSamePricing(legacy.LineResults[i], terms.LineResults[i]);
    }

    private static FeeSchedule MainSchedule() => Commercial(
        MainScheduleId, "Commercial Main", ("99213", 110m), ("27447", 1500m), ("29881", 800m));

    private static FeeSchedule CarveOutSchedule() => Commercial(
        CarveOutScheduleId, "Commercial Orthopaedic Carve-out", ("27447", 1800m), ("29881", 900m));

    private static FeeSchedule PlanDefaultSchedule()
    {
        var schedule = Commercial(PlanDefaultScheduleId, "Plan Default", ("99213", 95m), ("27447", 1200m));
        schedule.DefaultForPlanId = PlanId;
        return schedule;
    }

    private static FeeSchedule Commercial(
        string id, string name, params (string code, decimal rate)[] lines) => new()
    {
        Id = id, TenantId = Tenant, Name = name,
        Type = FeeScheduleType.Commercial,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = lines
            .Select(l => new FeeScheduleLine
            {
                ProcedureCode = l.code,
                Rate = l.rate,
                // 99213 is an E&M visit (indicator 0); the surgical codes take
                // the standard multiple surgery rule.
                MultipleProcedureIndicator = l.code == "99213"
                    ? MultipleProcedureIndicator.NoReduction
                    : MultipleProcedureIndicator.StandardSurgery,
            })
            .ToList(),
    };

    private static FeeSchedule MpfsSchedule() => new()
    {
        Id = MpfsScheduleId, TenantId = Tenant, Name = "Medicare MPFS 2026",
        Type = FeeScheduleType.MedicareMpfs,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines =
        [
            new FeeScheduleLine
            {
                ProcedureCode = "99213", Rate = 100m,
                MultipleProcedureIndicator = MultipleProcedureIndicator.NoReduction,
            },
        ],
    };

    private static FeeSchedule DrgSchedule() => new()
    {
        Id = DrgScheduleId, TenantId = Tenant, Name = "MS-DRG 2026",
        Type = FeeScheduleType.Drg,
        EffectiveDate = new DateTime(2026, 1, 1),
        Lines = [new FeeScheduleLine { ProcedureCode = "470", Rate = 12_000m }],
    };

    private static PricingRequest Request(
        string procedureCode,
        string pos = "11",
        decimal billed = 200m,
        int lineNumber = 1,
        int totalLines = 1,
        List<string>? modifiers = null,
        string? revenueCode = null,
        decimal units = 1m,
        string? billType = null) => new()
    {
        TenantId = Tenant,
        ProcedureCode = procedureCode,
        Modifiers = modifiers ?? [],
        ProviderNpi = ProviderNpi,
        PlaceOfServiceCode = pos,
        ServiceDate = new DateTime(2026, 3, 8),
        PlanId = PlanId,
        BilledAmount = billed,
        Units = units,
        LineNumber = lineNumber,
        TotalLineCount = totalLines,
        RevenueCode = revenueCode,
        BillType = billType,
    };

    private static PricingRequest InpatientRequest(
        string procedureCode, decimal billed = 200m, int lineNumber = 1, int totalLines = 1)
        => Request(procedureCode, pos: "11", billed: billed, lineNumber: lineNumber,
                totalLines: totalLines, billType: "111") with
        {
            DrgCode = "470",
            IsInstitutional = true,
        };
}
