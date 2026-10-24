using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.GrouperEngine.Domain;
using CloudHealthOffice.GrouperEngine.Hac;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// The stage that decides which DRG an inpatient claim prices by (ADR 024
/// step 9). Two facts carry most of these tests: the stage does nothing at
/// all unless a payer turned the plan setting on, and once it is on a claim
/// that cannot be grouped pends rather than falling back to the DRG the
/// facility billed.
/// </summary>
public class InpatientGroupingStageTests
{
    private const string Tenant = "tenant-1";

    private readonly IMsDrgGrouper _grouper = Substitute.For<IMsDrgGrouper>();
    private readonly InMemoryPlanSettings _settings = new();

    private InpatientGroupingStage Stage(bool hostCanGroup = true) => new(
        _grouper,
        _settings,
        NullLogger<InpatientGroupingStage>.Instance,
        hostCanGroup ? Substitute.For<IMsDrgReleaseProvider>() : null);

    private void TurnGroupingOn(string lineOfBusiness = "Medicare") => _settings.Set(
        Tenant, InpatientGroupingStage.SettingKey, SettingLevel.LineOfBusiness, lineOfBusiness, "true");

    [Fact]
    public void Stage_runs_before_pricing_and_is_not_required()
    {
        var stage = Stage();
        var pricing = new PricingStage(
            Substitute.For<IFeeSchedulePricingClient>(), NullLogger<PricingStage>.Instance);

        Assert.Equal("InpatientGrouping", stage.Name);
        Assert.True(stage.Order < pricing.Order);
        Assert.False(stage.IsRequired);
    }

    [Fact]
    public async Task A_professional_claim_is_not_grouped_and_does_not_read_the_setting()
    {
        var context = ProfessionalContext();

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.False(context.InpatientGrouping!.Grouped);
        Assert.Equal(0, _settings.Calls);
        await _grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    [Fact]
    public async Task An_outpatient_institutional_claim_has_no_stay_to_group()
    {
        var context = InpatientContext();
        context.Claim.Institutional!.AdmissionDate = null;

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal("470", context.InpatientGrouping!.PricedDrg);
        Assert.False(context.InpatientGrouping.Grouped);
        await _grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    /// <summary>
    /// The default everywhere. The claim prices exactly as it did before this
    /// stage existed, and the grouper is not even asked.
    /// </summary>
    [Fact]
    public async Task With_the_setting_off_the_billed_DRG_is_the_one_priced()
    {
        var context = InpatientContext();

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal("470", context.InpatientGrouping!.PricedDrg);
        Assert.Equal("470", context.InpatientGrouping.BilledDrg);
        Assert.False(context.InpatientGrouping.Grouped);
        Assert.False(context.InpatientGrouping.DisagreesWithFacility);
        Assert.Null(context.PendDetails);
        await _grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
        Assert.Equal("470", PricingStage.BuildRequests(context)[0].DrgCode);
    }

    /// <summary>
    /// The money test: with the setting on, the DRG that reaches the fee
    /// schedule is the grouper's, not the facility's.
    /// </summary>
    [Fact]
    public async Task With_the_setting_on_the_grouped_DRG_is_the_one_priced()
    {
        var context = InpatientContext();
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("469"));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal("469", context.InpatientGrouping!.PricedDrg);
        Assert.Equal("470", context.InpatientGrouping.BilledDrg);
        Assert.True(context.InpatientGrouping.Grouped);
        Assert.True(context.InpatientGrouping.DisagreesWithFacility);
        Assert.Null(context.PendDetails);
        Assert.All(PricingStage.BuildRequests(context), request => Assert.Equal("469", request.DrgCode));
    }

    [Fact]
    public async Task Agreeing_with_the_facility_is_not_a_disagreement()
    {
        var context = InpatientContext();
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("470"));

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.True(context.InpatientGrouping!.Grouped);
        Assert.False(context.InpatientGrouping.DisagreesWithFacility);
    }

    /// <summary>
    /// A claim the facility billed no DRG on still groups: the grouper's
    /// answer is the only one there is, and nothing is being disagreed with.
    /// </summary>
    [Fact]
    public async Task A_claim_with_no_billed_DRG_groups_and_disagrees_with_nothing()
    {
        var context = InpatientContext();
        context.Claim.Institutional!.DrgCode = "   ";
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("871"));

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("871", context.InpatientGrouping!.PricedDrg);
        Assert.Null(context.InpatientGrouping.BilledDrg);
        Assert.False(context.InpatientGrouping.DisagreesWithFacility);
    }

    /// <summary>
    /// Fail closed. The billed DRG is in the context for the record, but it is
    /// not what gets priced, because the whole point of turning grouping on is
    /// that the facility's own number is not the one being paid.
    /// </summary>
    [Fact]
    public async Task A_stay_the_grouper_refuses_pends_instead_of_pricing_the_billed_DRG()
    {
        var context = InpatientContext();
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(MsDrgGroupingResult.NotGrouped(
                GroupingStatus.InvalidPrincipalDiagnosis, "V44", "470",
                "E119 is not in the V44 code set"));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GroupingPendCode, context.PendDetails!.PendCode);
        Assert.Contains("InvalidPrincipalDiagnosis", result.Reason);
        Assert.Contains("not in the V44 code set", result.Reason);
        Assert.Null(context.InpatientGrouping!.PricedDrg);
        Assert.Equal("470", context.InpatientGrouping.BilledDrg);
        Assert.False(context.InpatientGrouping.Grouped);
        // The grouper's reasoning is kept whole, because an appeal is answered
        // from it and not from the three-character code.
        Assert.Equal(GroupingStatus.InvalidPrincipalDiagnosis, context.InpatientGrouping.Result!.Status);
    }

    [Fact]
    public async Task A_grouper_that_throws_pends_rather_than_failing_the_claim()
    {
        var context = InpatientContext();
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the table store is down"));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GroupingPendCode, context.PendDetails!.PendCode);
        Assert.Equal("470", context.InpatientGrouping!.PricedDrg);
        Assert.False(context.InpatientGrouping.Grouped);
    }

    [Fact]
    public async Task A_stay_with_no_principal_diagnosis_pends_without_asking_the_grouper()
    {
        var context = InpatientContext();
        // Only a secondary diagnosis. Grouping a stay on a secondary is the
        // kind of near-miss that pays a plausible wrong DRG.
        context.Claim.DiagnosisCodes = [new AdapterDiagnosisCode { Code = "I5021", CodeQualifier = "ABF" }];
        TurnGroupingOn();

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GroupingPendCode, context.PendDetails!.PendCode);
        Assert.Contains("no principal diagnosis", result.Reason);
        await _grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    /// <summary>
    /// The host's own capability answers a question the settings service
    /// cannot: a deployment with CMS tables loaded may be one that groups, so
    /// an unknown setting is not safe to read as off.
    /// </summary>
    [Fact]
    public async Task An_unreadable_setting_pends_on_a_host_that_can_group()
    {
        var context = InpatientContext();
        _settings.Unavailable = true;

        var result = await Stage(hostCanGroup: true).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.SettingUnavailablePendCode, context.PendDetails!.PendCode);
        await _grouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    /// <summary>
    /// And costs nothing on a host that has no tables: it could not have
    /// grouped whatever the setting said, so the claim prices as it always has.
    /// </summary>
    [Fact]
    public async Task An_unreadable_setting_passes_on_a_host_that_cannot_group()
    {
        var context = InpatientContext();
        _settings.Unavailable = true;

        var result = await Stage(hostCanGroup: false).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Null(context.PendDetails);
        Assert.Equal("470", context.InpatientGrouping!.PricedDrg);
        Assert.False(context.InpatientGrouping.Grouped);
    }

    /// <summary>
    /// The setting is read at the claim's own line of business, so one payer
    /// can group its Medicare book without grouping its commercial one.
    /// </summary>
    [Fact]
    public async Task The_setting_is_read_at_the_claims_line_of_business()
    {
        TurnGroupingOn("Medicare");
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("469"));

        var medicare = InpatientContext();
        medicare.Claim.LineOfBusiness = LineOfBusiness.Medicare;
        await Stage().ExecuteAsync(medicare, CancellationToken.None);

        var commercial = InpatientContext();
        commercial.Claim.LineOfBusiness = LineOfBusiness.Commercial;
        await Stage().ExecuteAsync(commercial, CancellationToken.None);

        Assert.True(medicare.InpatientGrouping!.Grouped);
        Assert.Equal("469", medicare.InpatientGrouping.PricedDrg);
        Assert.False(commercial.InpatientGrouping!.Grouped);
        Assert.Equal("470", commercial.InpatientGrouping.PricedDrg);
    }

    [Fact]
    public async Task An_earlier_stages_pend_is_the_one_the_work_queue_sees()
    {
        var context = InpatientContext();
        var earlier = new PendDetails { PendCode = "MEDREVIEW", PendReason = "integrity" };
        context.PendDetails = earlier;
        TurnGroupingOn();
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(MsDrgGroupingResult.NotGrouped(GroupingStatus.NoDrgRuleMatched, "V44", "470"));

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Same(earlier, context.PendDetails);
    }

    [Fact]
    public async Task The_grouper_is_handed_the_stay_as_the_tables_expect_it()
    {
        var context = InpatientContext();
        var institutional = context.Claim.Institutional!;
        institutional.DischargeDate = new DateTime(2026, 2, 14, 0, 0, 0, DateTimeKind.Utc);
        institutional.PrincipalProcedure = new InstitutionalProcedureCode
        {
            Code = "0sr9019", Date = new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc),
        };
        institutional.OtherProcedures = [new InstitutionalProcedureCode { Code = "02100Z9" }];
        institutional.ValueCodes = [new ValueCode { Code = "54", Amount = 1250.75m }];
        context.Claim.DiagnosisCodes =
        [
            new AdapterDiagnosisCode { Code = "e11.65", CodeQualifier = "ABK", PresentOnAdmission = "Y" },
            new AdapterDiagnosisCode { Code = "i50.21", CodeQualifier = "ABF", PresentOnAdmission = "N" },
        ];
        context.ResolvedMember = new ResolvedMember
        {
            MemberId = "MEM-1",
            DateOfBirth = new DateTime(1958, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            Gender = "female",
        };
        TurnGroupingOn();
        MsDrgGroupingRequest? captured = null;
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<MsDrgGroupingRequest>();
                return Task.FromResult(Grouped("469"));
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.NotNull(captured);
        // Decimal points removed and upper-cased: the tables are keyed that way.
        Assert.Equal("E1165", captured!.PrincipalDiagnosis.Code);
        Assert.Equal("Y", captured.PrincipalDiagnosis.PresentOnAdmission);
        var secondary = Assert.Single(captured.SecondaryDiagnoses);
        Assert.Equal("I5021", secondary.Code);
        Assert.Equal("N", secondary.PresentOnAdmission);
        // Principal procedure first: the surgical hierarchy runs over all of
        // them, but the principal one is the claim's own ordering.
        Assert.Equal(new[] { "0SR9019", "02100Z9" }, captured.Procedures.Select(p => p.Code));
        Assert.Equal(new DateOnly(2026, 2, 11), captured.Procedures[0].Date);
        // Discharge date, not admission: a stay is grouped by the tables in
        // force when it ended.
        Assert.Equal(new DateOnly(2026, 2, 14), captured.DischargeDate);
        Assert.Equal(67, captured.AgeYears);
        Assert.Equal(1250, captured.BirthWeightGrams);
        Assert.Equal("F", captured.Sex);
        Assert.Equal(4, captured.LengthOfStayDays);
        Assert.Equal("01", captured.DischargeStatus);
        Assert.Equal("470", captured.BilledDrg);
        // Nothing in CHO holds whether a facility reports present-on-admission
        // indicators, and Unknown is CMS's own answer to not knowing.
        Assert.Equal(HospitalPoaReporting.Unknown, captured.PoaReporting);
    }

    /// <summary>
    /// Discharge date selects the grouper version, so it has to be the best
    /// available: the discharge, then the statement through date on an interim
    /// bill. A default DateTime would group a 1-January-0001 stay against no
    /// tables at all.
    /// </summary>
    [Fact]
    public async Task Without_a_discharge_date_the_statement_through_date_selects_the_version()
    {
        var context = InpatientContext();
        context.Claim.Institutional!.DischargeDate = null;
        context.Claim.ServiceDateTo = default;
        TurnGroupingOn();
        MsDrgGroupingRequest? captured = null;
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<MsDrgGroupingRequest>();
                return Task.FromResult(Grouped("469"));
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 2, 14), captured!.DischargeDate);
    }

    /// <summary>
    /// A sex code CHO cannot interpret is null, not U: U is a claim that says
    /// the sex is unknown, and the two are different facts to anyone reading
    /// the grouping afterwards.
    /// </summary>
    [Theory]
    [InlineData("M", "M")]
    [InlineData("male", "M")]
    [InlineData("F", "F")]
    [InlineData("U", "U")]
    [InlineData("unknown", "U")]
    [InlineData("X", null)]
    [InlineData(null, null)]
    public async Task An_uninterpretable_sex_code_is_absent_rather_than_unknown(string? gender, string? expected)
    {
        var context = InpatientContext();
        context.ResolvedMember = new ResolvedMember { MemberId = "MEM-1", Gender = gender };
        TurnGroupingOn();
        MsDrgGroupingRequest? captured = null;
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<MsDrgGroupingRequest>();
                return Task.FromResult(Grouped("469"));
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(expected, captured!.Sex);
    }

    private static MsDrgGroupingResult Grouped(string drg) => new()
    {
        Status = GroupingStatus.Grouped,
        Source = DrgSource.ChoGrouper,
        Drg = drg,
        GrouperVersion = "V44",
        BilledDrg = "470",
    };

    private static ClaimAdjudicationContext ProfessionalContext()
    {
        var serviceDate = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            TenantId = Tenant,
            Id = "claim-1",
            ClaimVersionId = "claim-1",
            MemberId = "MEM-1",
            ClaimType = ClaimType.Professional,
            LineOfBusiness = LineOfBusiness.Medicare,
            ServiceDateFrom = serviceDate,
            ServiceDateTo = serviceDate,
            BillingProviderNPI = "1234567893",
            ClaimLines =
            [
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1m,
                    ServiceDateFrom = serviceDate,
                },
            ],
        };
        return new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
        };
    }

    private static ClaimAdjudicationContext InpatientContext()
    {
        var context = ProfessionalContext();
        var admit = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var claim = context.Claim;
        claim.ClaimType = ClaimType.Institutional;
        claim.ClaimFrequencyCode = "1";
        claim.ServiceDateFrom = admit;
        claim.ServiceDateTo = admit.AddDays(4);
        claim.DiagnosisCodes =
        [
            new AdapterDiagnosisCode { Code = "E1165", CodeQualifier = "ABK", PresentOnAdmission = "Y" },
        ];
        claim.Institutional = new InstitutionalClaimDetails
        {
            FacilityTypeCode = "11",
            AdmissionDate = admit,
            DischargeDate = admit.AddDays(4),
            StatementFromDate = admit,
            StatementToDate = admit.AddDays(4),
            PatientStatusCode = "01",
            DrgCode = "470",
        };
        claim.ClaimLines =
        [
            new() { LineNumber = 1, RevenueCode = "0120", ChargeAmount = 12000m, Units = 4m, ServiceDateFrom = admit },
            new() { LineNumber = 2, RevenueCode = "0360", ProcedureCode = "27447", ChargeAmount = 29000m, Units = 1m, ServiceDateFrom = admit },
        ];
        return context;
    }
}

/// <summary>
/// The scope adjudication resolves plan settings in
/// (plan-configuration.md §12.16).
///
/// <para>
/// The stage passed only the claim's line of business, so a value entered on
/// a benefit plan, a sponsor or one of that sponsor's policies was invisible
/// to adjudication however deliberately it had been set — and invisible
/// silently, because the resolver answers the tenant default rather than
/// failing. These tests set a value at each level and check the claim is
/// priced by it.
/// </para>
/// </summary>
public class InpatientGroupingSettingScopeTests
{
    private const string Tenant = "tenant-1";
    private const string Group = "GRP0001";
    private const string Policy = "POL-9";
    private const string PlanId = "plan-guid-1";

    private readonly IMsDrgGrouper _grouper = Substitute.For<IMsDrgGrouper>();
    private readonly InMemoryPlanSettings _settings = new();

    private InpatientGroupingStage Stage() => new(
        _grouper,
        _settings,
        NullLogger<InpatientGroupingStage>.Instance,
        Substitute.For<IMsDrgReleaseProvider>());

    private void Set(SettingLevel level, string scopeId, string value) =>
        _settings.Set(Tenant, InpatientGroupingStage.SettingKey, level, scopeId, value);

    private static ClaimAdjudicationContext Context(
        string? groupNumber = Group,
        string? policyNumber = Policy,
        string? benefitPlanId = PlanId)
    {
        var admit = new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc);
        var claim = new AdapterClaim
        {
            ClaimVersionId = "cv-1",
            ClaimNumber = "CLM-1",
            MemberId = "MEM-1",
            BenefitPlanId = benefitPlanId,
            LineOfBusiness = LineOfBusiness.Medicare,
            ClaimType = ClaimType.Institutional,
            ClaimFrequencyCode = "1",
            ServiceDateFrom = admit,
            ServiceDateTo = admit.AddDays(4),
            DiagnosisCodes =
            [
                new AdapterDiagnosisCode { Code = "E1165", CodeQualifier = "ABK", PresentOnAdmission = "Y" },
            ],
            Institutional = new InstitutionalClaimDetails
            {
                FacilityTypeCode = "11",
                AdmissionDate = admit,
                DischargeDate = admit.AddDays(4),
                StatementFromDate = admit,
                StatementToDate = admit.AddDays(4),
                DrgCode = "470",
                PatientStatusCode = "01",
            },
            ClaimLines =
            [
                new()
                {
                    LineNumber = 1, ProcedureCode = "99213", ChargeAmount = 200m, Units = 1m,
                    ServiceDateFrom = admit,
                },
            ],
        };

        return new ClaimAdjudicationContext
        {
            TenantId = Tenant,
            ClaimVersionId = claim.ClaimVersionId,
            Claim = claim,
            ResolvedCoverage = groupNumber is null && policyNumber is null
                ? null
                : new CoverageResolution(PlanId, groupNumber, policyNumber),
        };
    }

    private async Task<bool> Grouped(ClaimAdjudicationContext context)
    {
        _grouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MsDrgGroupingResult
            {
                Status = GroupingStatus.Grouped,
                Source = DrgSource.ChoGrouper,
                Drg = "469",
                GrouperVersion = "V44",
                BilledDrg = "470",
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);
        return context.InpatientGrouping!.Grouped;
    }

    [Fact]
    public async Task A_value_on_the_sponsors_policy_decides_the_claim()
    {
        // The level a payer configuring one employer's stay pricing reaches
        // for, and the one that could not be seen at all before this change.
        Set(SettingLevel.SponsorPolicy, $"{Tenant}|{Group}|{Policy}", "true");

        Assert.True(await Grouped(Context()));
    }

    [Fact]
    public async Task A_value_on_the_sponsor_decides_the_claim()
    {
        Set(SettingLevel.Sponsor, Group, "true");

        Assert.True(await Grouped(Context()));
    }

    [Fact]
    public async Task A_value_on_the_benefit_plan_decides_the_claim()
    {
        // The level this setting's catalog entry deferred until a scope could
        // carry a plan id.
        Set(SettingLevel.BenefitPlan, PlanId, "true");

        Assert.True(await Grouped(Context()));
    }

    [Fact]
    public async Task The_policy_beats_the_sponsor_and_the_sponsor_beats_the_line_of_business()
    {
        // Specific wins. Without it a sponsor-wide "on" would override the one
        // policy a payer had deliberately turned off.
        Set(SettingLevel.LineOfBusiness, "Medicare", "true");
        Set(SettingLevel.Sponsor, Group, "true");
        Set(SettingLevel.SponsorPolicy, $"{Tenant}|{Group}|{Policy}", "false");

        Assert.False(await Grouped(Context()));
    }

    [Fact]
    public async Task The_sponsor_beats_the_line_of_business()
    {
        Set(SettingLevel.LineOfBusiness, "Medicare", "true");
        Set(SettingLevel.Sponsor, Group, "false");

        Assert.False(await Grouped(Context()));
    }

    [Fact]
    public async Task A_policy_value_is_named_the_way_sponsor_service_names_the_node()
    {
        // The bare policy number is NOT the node id. If the scope were built
        // from it, this value would never be found and the claim would fall
        // back to the tenant default -- with nothing logged, because a
        // fallback is a legitimate answer.
        Set(SettingLevel.SponsorPolicy, Policy, "true");

        Assert.False(await Grouped(Context()));
    }

    [Fact]
    public async Task A_claim_whose_coverage_named_no_sponsor_still_resolves_at_the_levels_it_has()
    {
        // The two on-ramps that carry their own benefit plan make no coverage
        // call, so a stage must not depend on a sponsor being known.
        Set(SettingLevel.BenefitPlan, PlanId, "true");

        Assert.True(await Grouped(Context(groupNumber: null, policyNumber: null)));
    }

    [Fact]
    public void A_policy_without_a_sponsor_is_dropped_rather_than_sent_incoherent()
    {
        // SettingScope.Incoherent refuses a policy with no sponsor and
        // tenant-service answers 422, which would turn a settings read into a
        // failed claim. Resolving a level shallower is the right failure.
        var context = Context(groupNumber: null, policyNumber: Policy);

        Assert.Null(context.SettingScopeForClaim().SponsorPolicy);
        Assert.Empty(context.SettingScopeForClaim().Incoherent());
    }

    [Fact]
    public void The_scope_is_coherent_for_every_shape_a_claim_can_arrive_in()
    {
        // Each of these would be answered 422 rather than resolved if the
        // levels did not line up.
        Assert.Empty(Context().SettingScopeForClaim().Incoherent());
        Assert.Empty(Context(policyNumber: null).SettingScopeForClaim().Incoherent());
        Assert.Empty(Context(groupNumber: null, policyNumber: null).SettingScopeForClaim().Incoherent());
        Assert.Empty(Context(benefitPlanId: null).SettingScopeForClaim().Incoherent());
    }

    [Fact]
    public void The_line_of_business_is_still_the_claims_own()
    {
        // Unchanged from before, and worth holding: the enum's own name is
        // what LinesOfBusiness.Normalize knows.
        Assert.Equal("Medicare", Context().SettingScopeForClaim().LineOfBusiness);
    }
}
