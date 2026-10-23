using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.FeeScheduleEngine.Domain;
using CloudHealthOffice.FeeScheduleEngine.Models;
using CloudHealthOffice.FeeScheduleEngine.Persistence;
using CloudHealthOffice.FeeScheduleEngine.Services;
using CloudHealthOffice.GrouperEngine.Data;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Integration;

/// <summary>
/// ADR 024 step 8 end to end, through the real grouper and the real fee
/// schedule engine: one stay, one DRG rate table, and the plan setting as the
/// only thing that differs between the two runs.
///
/// <para>
/// The stay is a knee replacement with no complication on the claim. The
/// facility billed DRG 469, the with-MCC row; the grouper assigns 470, the
/// without-MCC row, because the claim carries no MCC to split on. The DRG
/// table pays 469 at 32,000 and 470 at 19,500, so the two runs differ by
/// 12,500 on one claim. That difference is the whole reason this stage exists,
/// and it is also why the setting is off until a payer turns it on.
/// </para>
///
/// <para>
/// What this file does not cover is the present-on-admission gate. The stage
/// tells the grouper <c>HospitalPoaReporting.Unknown</c>, because CHO holds
/// nothing that says whether a facility reports present-on-admission
/// indicators — there is no field on the provider record — and that value
/// refuses a stay carrying a hospital-acquired condition rather than guessing
/// which way it should be paid. That refusal lives in
/// <c>FormulaMsDrgGrouper</c> over the imported CMS tables, not in
/// <see cref="TableDrivenMsDrgGrouper"/> over the seed slice used here, and
/// <c>MsDrgHacProcessorTests</c> covers it directly.
/// </para>
///
/// <para>
/// The grouper here runs over
/// <see cref="MsDrgSeedTables"/>, which is a hand-built slice and not the CMS
/// table set; it carries the four families the seed covers and is nowhere near
/// enough to pay a claim on. What this test proves is the wiring — which DRG
/// reaches the fee schedule — not the grouping, which has its own parity suite
/// against CMS's own grouper.
/// </para>
/// </summary>
public class AdjudicationGroupsInpatientDrgEndToEndTests
{
    private const string Tenant = "tenant-1";
    private const string PlanId = "PLAN-IP";
    private const decimal WithMccCaseRate = 32_000m;
    private const decimal WithoutMccCaseRate = 19_500m;

    private readonly InMemoryPlanSettings _settings = new();
    private readonly IMsDrgGrouper _grouper = new TableDrivenMsDrgGrouper(
        new InMemoryMsDrgTableProvider(MsDrgSeedTables.Build()),
        NullLogger<TableDrivenMsDrgGrouper>.Instance);

    [Fact]
    public async Task With_the_setting_off_the_stay_pays_the_case_rate_the_facility_billed()
    {
        var context = KneeReplacementStay();

        var allowed = await RunAsync(context);

        Assert.Equal("469", context.InpatientGrouping!.PricedDrg);
        Assert.False(context.InpatientGrouping.Grouped);
        Assert.Equal(WithMccCaseRate, allowed);
    }

    [Fact]
    public async Task With_the_setting_on_the_stay_pays_the_case_rate_the_grouper_assigned()
    {
        var context = KneeReplacementStay();
        _settings.Set(Tenant, InpatientGroupingStage.SettingKey,
            SettingLevel.LineOfBusiness, LinesOfBusiness.Medicare, "true");

        var allowed = await RunAsync(context);

        Assert.Equal("470", context.InpatientGrouping!.PricedDrg);
        Assert.Equal("469", context.InpatientGrouping.BilledDrg);
        Assert.True(context.InpatientGrouping.Grouped);
        Assert.True(context.InpatientGrouping.DisagreesWithFacility);
        Assert.Equal(WithoutMccCaseRate, allowed);
        Assert.Equal(WithMccCaseRate - WithoutMccCaseRate, 12_500m);
    }

    /// <summary>
    /// Runs the two real stages in pipeline order and returns what the stay
    /// was allowed in total.
    /// </summary>
    private async Task<decimal> RunAsync(ClaimAdjudicationContext context)
    {
        var grouping = await GroupingStage().ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, grouping.Outcome);

        var pricing = await PricingStage().ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, pricing.Outcome);
        Assert.True(context.PricingResult!.IsFullyPriced);
        return context.PricingResult.AllowedAmounts.Values.Sum();
    }

    private InpatientGroupingStage GroupingStage() => new(
        _grouper,
        _settings,
        NullLogger<InpatientGroupingStage>.Instance,
        // A host with tables loaded, as one that groups would be.
        Substitute.For<IMsDrgReleaseProvider>());

    /// <summary>
    /// The real <see cref="PricingStage"/> over the real
    /// <see cref="RateResolutionService"/>, with the resolve-rates HTTP hop
    /// stood in for in process.
    /// </summary>
    private static PricingStage PricingStage()
    {
        var schedule = new FeeSchedule
        {
            Id = "ip-drg-2026",
            TenantId = Tenant,
            Name = "Inpatient MS-DRG case rates 2026",
            Type = FeeScheduleType.Drg,
            EffectiveDate = new DateTime(2026, 1, 1),
            Lines =
            [
                new FeeScheduleLine
                {
                    ProcedureCode = "469", RateType = FeeScheduleRateType.FlatRate, Rate = WithMccCaseRate,
                },
                new FeeScheduleLine
                {
                    ProcedureCode = "470", RateType = FeeScheduleRateType.FlatRate, Rate = WithoutMccCaseRate,
                },
            ],
        };
        var schedules = Substitute.For<IFeeScheduleRepository>();
        schedules.GetDefaultForPlanAsync(Tenant, Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(schedule);
        var contracts = Substitute.For<IProviderContractRepository>();
        contracts.GetContractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((ProviderContract?)null);
        var engine = new RateResolutionService(
            schedules, contracts, NullLogger<RateResolutionService>.Instance);

        var client = Substitute.For<IFeeSchedulePricingClient>();
        client.ResolveBatchAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<PricingRequest>>(), Arg.Any<CancellationToken>())
            .Returns(async call => (PricingResultSet?)await engine.ResolveBatchAsync(
                call.ArgAt<IReadOnlyList<PricingRequest>>(1)));
        return new PricingStage(client, NullLogger<PricingStage>.Instance);
    }

    /// <summary>
    /// A four-day knee replacement: osteoarthritis of the right knee as the
    /// principal diagnosis, a right total knee replacement as the procedure,
    /// and DRG 469 billed.
    /// </summary>
    private static ClaimAdjudicationContext KneeReplacementStay()
    {
        var admit = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var discharge = admit.AddDays(4);
        var claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "claim-ip-1",
            ClaimVersionId = "claim-ip-1",
            MemberId = "MEM-1",
            BenefitPlanId = PlanId,
            BillingProviderNPI = "1234567893",
            ClaimType = ClaimType.Institutional,
            LineOfBusiness = LineOfBusiness.Medicare,
            ClaimFrequencyCode = "1",
            ServiceDateFrom = admit,
            ServiceDateTo = discharge,
            DiagnosisCodes =
            [
                new AdapterDiagnosisCode { Code = "M17.11", CodeQualifier = "ABK", PresentOnAdmission = "Y" },
            ],
            Institutional = new InstitutionalClaimDetails
            {
                FacilityTypeCode = "11",
                AdmissionDate = admit,
                DischargeDate = discharge,
                StatementFromDate = admit,
                StatementToDate = discharge,
                PatientStatusCode = "01",
                DrgCode = "469",
                PrincipalProcedure = new InstitutionalProcedureCode
                {
                    Code = "0SRC0J9", CodeQualifier = "BBR", Date = admit.AddDays(1),
                },
            },
            ClaimLines =
            [
                new() { LineNumber = 1, RevenueCode = "0120", ChargeAmount = 12_000m, Units = 4m, ServiceDateFrom = admit },
                new() { LineNumber = 2, RevenueCode = "0360", ProcedureCode = "27447", ChargeAmount = 41_000m, Units = 1m, ServiceDateFrom = admit },
            ],
        };

        return new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            ResolvedPlan = new ResolvedBenefitPlan { Id = PlanId, PlanGuid = Guid.NewGuid() },
            ResolvedMember = new ResolvedMember
            {
                MemberId = "MEM-1",
                DateOfBirth = new DateTime(1958, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                Gender = "F",
            },
        };
    }
}
