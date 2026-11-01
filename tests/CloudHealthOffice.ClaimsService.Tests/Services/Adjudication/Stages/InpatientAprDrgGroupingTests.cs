using ClaimsService.Models;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using CloudHealthOffice.GrouperEngine.Licensed;
using CloudHealthOffice.GrouperEngine.Models;
using CloudHealthOffice.GrouperEngine.Services;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>
/// APR-DRG grouping for Medicaid lines (Texas, California): with
/// <c>pricing.inpatient.drgClassification</c> = aprDrg the stage asks the
/// plan's licensed grouper, prices by the "DRG-SOI" key, and pends on any
/// answer short of a validated group. The MS-DRG grouper is never asked.
/// </summary>
public class InpatientAprDrgGroupingTests
{
    private const string Tenant = "tenant-1";

    private readonly IMsDrgGrouper _msGrouper = Substitute.For<IMsDrgGrouper>();
    private readonly IAprDrgGrouper _aprGrouper = Substitute.For<IAprDrgGrouper>();
    private readonly InMemoryPlanSettings _settings = new();

    private InpatientGroupingStage Stage(IAprDrgGrouper? apr = null, bool useDefault = true) => new(
        _msGrouper,
        _settings,
        NullLogger<InpatientGroupingStage>.Instance,
        releases: null,
        aprGrouper: useDefault ? _aprGrouper : apr);

    private static ClaimAdjudicationContext MedicaidStay()
    {
        var context = InpatientGroupingStageTests.InpatientContext();
        context.Claim.LineOfBusiness = LineOfBusiness.Medicaid;
        context.Claim.Institutional!.DrgCode = "139";
        return context;
    }

    private void GroupByAprDrg(int version = 40)
    {
        _settings.Set(Tenant, InpatientGroupingStage.SettingKey, SettingLevel.LineOfBusiness, "Medicaid", "true");
        _settings.Set(Tenant, InpatientGroupingStage.ClassificationSettingKey, SettingLevel.LineOfBusiness, "Medicaid", "\"aprDrg\"");
        _settings.Set(Tenant, InpatientGroupingStage.AprDrgVersionSettingKey, SettingLevel.LineOfBusiness, "Medicaid", version.ToString());
    }

    private static AprDrgGroupingResult Grouped(string drg, int soi) => new()
    {
        Status = LicensedGroupingStatus.Grouped,
        AprDrg = drg,
        SeverityOfIllness = soi,
        RiskOfMortality = 2,
        GrouperVersion = "40.0",
        BilledDrg = "139",
    };

    [Fact]
    public async Task The_drg_soi_key_is_what_pricing_sees()
    {
        var context = MedicaidStay();
        GroupByAprDrg();
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("139", 3));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal("139-3", context.InpatientGrouping!.PricedDrg);
        Assert.True(context.InpatientGrouping.Grouped);
        Assert.Equal(3, context.InpatientGrouping.AprDrgResult!.SeverityOfIllness);
        Assert.Null(context.PendDetails);
        Assert.All(PricingStage.BuildRequests(context), r => Assert.Equal("139-3", r.DrgCode));
        await _msGrouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    [Fact]
    public async Task The_facility_billing_the_same_base_group_is_not_a_disagreement()
    {
        var context = MedicaidStay();
        GroupByAprDrg();
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("139", 4));

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.False(context.InpatientGrouping!.DisagreesWithFacility);
    }

    [Fact]
    public async Task A_different_base_group_is_a_disagreement()
    {
        var context = MedicaidStay();
        GroupByAprDrg();
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(Grouped("720", 2));

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.True(context.InpatientGrouping!.DisagreesWithFacility);
    }

    [Fact]
    public async Task The_request_carries_the_states_version_and_the_admitting_diagnosis_separately()
    {
        var context = MedicaidStay();
        context.Claim.DiagnosisCodes =
        [
            new AdapterDiagnosisCode { Code = "j18.9", CodeQualifier = "ABK", PresentOnAdmission = "Y" },
            new AdapterDiagnosisCode { Code = "r06.02", CodeQualifier = "ABJ" },
            new AdapterDiagnosisCode { Code = "n17.9", CodeQualifier = "ABF", PresentOnAdmission = "N" },
        ];
        GroupByAprDrg(version: 38);
        AprDrgGroupingRequest? captured = null;
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<AprDrgGroupingRequest>();
                return Task.FromResult(Grouped("139", 2));
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("38", captured!.RequestedVersion);
        Assert.Equal("J189", captured.PrincipalDiagnosis.Code);
        Assert.Equal("R0602", captured.AdmittingDiagnosis);
        Assert.Equal(["N179"], captured.SecondaryDiagnoses.Select(d => d.Code));
        Assert.Equal(new DateOnly(2026, 2, 10), captured.AdmissionDate);
        Assert.Equal(new DateOnly(2026, 2, 14), captured.DischargeDate);
        Assert.Equal("claim-1", captured.ClaimReference);
        Assert.Equal("139", captured.BilledDrg);
    }

    [Fact]
    public async Task Version_zero_leaves_the_version_to_the_grouper()
    {
        var context = MedicaidStay();
        GroupByAprDrg(version: 0);
        AprDrgGroupingRequest? captured = null;
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<AprDrgGroupingRequest>();
                return Task.FromResult(Grouped("139", 2));
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Null(captured!.RequestedVersion);
    }

    [Theory]
    [InlineData(LicensedGroupingStatus.RejectedByGrouper, InpatientGroupingStage.GroupingPendCode)]
    [InlineData(LicensedGroupingStatus.InvalidResponse, InpatientGroupingStage.GroupingPendCode)]
    [InlineData(LicensedGroupingStatus.Unavailable, InpatientGroupingStage.GrouperUnavailablePendCode)]
    [InlineData(LicensedGroupingStatus.NotConfigured, InpatientGroupingStage.GrouperUnavailablePendCode)]
    public async Task Anything_short_of_a_group_pends_and_never_prices_the_billed_drg(
        LicensedGroupingStatus status, string pendCode)
    {
        var context = MedicaidStay();
        GroupByAprDrg();
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(AprDrgGroupingResult.NotGrouped(status, "139", "grouper said no"));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(pendCode, context.PendDetails!.PendCode);
        Assert.Contains("grouper said no", context.PendDetails.PendReason);
        Assert.Null(context.InpatientGrouping!.PricedDrg);
        Assert.All(PricingStage.BuildRequests(context), r => Assert.Null(r.DrgCode));
    }

    [Fact]
    public async Task A_deployment_without_a_licensed_grouper_pends()
    {
        var context = MedicaidStay();
        GroupByAprDrg();

        var result = await Stage(new NotConfiguredLicensedGrouper(), useDefault: false)
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GrouperUnavailablePendCode, context.PendDetails!.PendCode);
    }

    [Fact]
    public async Task A_grouper_that_throws_pends()
    {
        var context = MedicaidStay();
        GroupByAprDrg();
        _aprGrouper.GroupAsync(Arg.Any<AprDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GrouperUnavailablePendCode, context.PendDetails!.PendCode);
    }

    [Fact]
    public async Task A_stay_without_a_principal_diagnosis_pends_before_the_grouper_is_asked()
    {
        var context = MedicaidStay();
        context.Claim.DiagnosisCodes = [];
        GroupByAprDrg();

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.GroupingPendCode, context.PendDetails!.PendCode);
        await _aprGrouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }

    [Fact]
    public async Task When_settings_are_unreachable_on_a_host_with_a_licensed_grouper_the_claim_pends()
    {
        var context = MedicaidStay();
        _settings.Unavailable = true;

        var result = await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(InpatientGroupingStage.SettingUnavailablePendCode, context.PendDetails!.PendCode);
    }

    [Fact]
    public async Task Without_any_grouper_an_unreachable_setting_still_prices_as_before()
    {
        var context = MedicaidStay();
        _settings.Unavailable = true;

        var result = await Stage(new NotConfiguredLicensedGrouper(), useDefault: false)
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        Assert.Equal("139", context.InpatientGrouping!.PricedDrg);
    }

    [Fact]
    public async Task The_ms_drg_classification_is_still_the_default()
    {
        var context = MedicaidStay();
        _settings.Set(Tenant, InpatientGroupingStage.SettingKey, SettingLevel.LineOfBusiness, "Medicaid", "true");
        _msGrouper.GroupAsync(Arg.Any<MsDrgGroupingRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MsDrgGroupingResult
            {
                Status = CloudHealthOffice.GrouperEngine.Domain.GroupingStatus.Grouped,
                Source = CloudHealthOffice.GrouperEngine.Domain.DrgSource.ChoGrouper,
                Drg = "193",
            });

        await Stage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal("193", context.InpatientGrouping!.PricedDrg);
        await _aprGrouper.DidNotReceiveWithAnyArgs().GroupAsync(default!, default);
    }
}
