using ClaimsService.Models;
using ClaimsService.Models.Adjudication;
using ClaimsService.Services.Adjudication;
using ClaimsService.Services.Adjudication.Stages;
using ClaimsService.Services.Resolution;
using CloudHealthOffice.Infrastructure.Programs.MediCal;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CloudHealthOffice.ClaimsService.Tests.Services.Adjudication.Stages;

/// <summary>DHCS APL 26-009 on claims: adults 19+ not eligible for federally funded full-scope Medi-Cal, from July 1, 2027.</summary>
public class StateOnlyAdultDentalStageTests
{
    private const string CarveOut =
        """{"mode":"CarveOut","stateOnlyAidCodes":["S1","SP"]}""";

    private const string EmergencyOnly =
        """{"mode":"EmergencyOnly","stateOnlyAidCodes":["S1","SP"],"exceptionAidCodes":["SP"],"emergencyProcedureCodes":["D0140","D9110"]}""";

    private static readonly DateTime July1 = new(2027, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly IPlanSettings _settings = Substitute.For<IPlanSettings>();

    private StateOnlyAdultDentalStage Stage(string? value, bool fallback = false)
    {
        _settings.ResolveAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new ResolvedSetting
            {
                Key = StateOnlyAdultDentalStage.SettingKey,
                Value = value ?? "\"off\"",
                SourceLevel = SettingLevel.BenefitPlan,
                IsFallback = fallback,
            }));
        return new StateOnlyAdultDentalStage(_settings, NullLogger<StateOnlyAdultDentalStage>.Instance);
    }

    private static ClaimAdjudicationContext Context(
        DateTime dos, string aidCode = "S1", DateTime? dob = null, params string[] codes)
    {
        codes = codes.Length == 0 ? ["D1110"] : codes;
        var claim = new AdapterClaim
        {
            TenantId = "t1",
            Id = "c1",
            ClaimVersionId = "c1",
            MemberId = "M1",
            BenefitPlanId = "DENTAL-PLAN",
            ClaimType = ClaimType.Dental,
            ServiceDateFrom = dos,
            ServiceDateTo = dos,
            ClaimLines = codes.Select((c, i) => new AdapterClaimLine
            {
                LineNumber = i + 1, ProcedureCode = c, ChargeAmount = 100m, ServiceDateFrom = dos,
            }).ToList(),
        };
        return new ClaimAdjudicationContext
        {
            TenantId = "t1",
            ClaimVersionId = "c1",
            Claim = claim,
            MemberLookup = MemberLookupOutcome.Found,
            ResolvedMember = new ResolvedMember
            {
                MemberId = "M1",
                DateOfBirth = dob ?? new DateTime(1990, 5, 20),
                AidCodes = aidCode.Length == 0 ? [] : [new AidCodeSpan(aidCode, new DateTime(2024, 1, 1))],
            },
        };
    }

    [Fact]
    public async Task BeforeJuly2027_PassesWithoutReadingTheSetting()
    {
        var stage = Stage(CarveOut);
        var result = await stage.ExecuteAsync(Context(July1.AddDays(-1)), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
        await _settings.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task Off_Passes()
    {
        var result = await Stage(null).ExecuteAsync(Context(July1), CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CarveOut_DeniesCo109_BillDentalFfs()
    {
        var context = Context(July1);
        var result = await Stage(CarveOut).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Deny, result.Outcome);
        Assert.False(result.Continue);
        Assert.Equal("109", context.AdjudicationResult.DenialReasonCode);
        Assert.Contains("Medi-Cal Dental fee-for-service", context.AdjudicationResult.DenialReason);
    }

    [Fact]
    public async Task CarveOut_FederallyFundedMember_Passes()
    {
        var result = await Stage(CarveOut).ExecuteAsync(Context(July1, aidCode: "1H"), CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task CarveOut_MemberStill18_Passes()
    {
        var result = await Stage(CarveOut).ExecuteAsync(
            Context(July1, dob: new DateTime(2008, 7, 2)), CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task NoAidCodeOnFile_PendsAidCode()
    {
        var context = Context(July1, aidCode: "");
        var result = await Stage(CarveOut).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(StateOnlyAdultDentalStage.AidCodePendCode, context.PendDetails!.PendCode);
    }

    [Fact]
    public async Task EmergencyOnly_NonEmergencyLines_PendNamingThem()
    {
        var context = Context(July1, "S1", null, "D0140", "D2740");
        var result = await Stage(EmergencyOnly).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.Equal(StateOnlyAdultDentalStage.ScopePendCode, context.PendDetails!.PendCode);
        Assert.StartsWith("Lines 2:", context.PendDetails.PendReason);
        Assert.Contains("D2740", context.PendDetails.PendReason);
    }

    [Fact]
    public async Task EmergencyOnly_EmergencyLinesAndExceptions_Pass()
    {
        var emergency = await Stage(EmergencyOnly).ExecuteAsync(Context(July1, "S1", null, "D0140", "D9110"), CancellationToken.None);
        var pregnant = await Stage(EmergencyOnly).ExecuteAsync(Context(July1, "SP", null, "D2740"), CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pass, emergency.Outcome);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, pregnant.Outcome);
        Assert.Contains(pregnant.Notes, n => n.Contains("exception"));
    }

    [Fact]
    public async Task SettingUnavailable_PendsAsACheckThatCouldNotRun()
    {
        var context = Context(July1);
        var result = await Stage(null, fallback: true).ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(ClaimAdjudicationOutcome.Pend, result.Outcome);
        Assert.True(result.CheckUnavailable);
        Assert.Equal(InpatientGroupingStage.SettingUnavailablePendCode, context.PendDetails!.PendCode);
    }

    [Fact]
    public async Task MedicalClaim_WithoutCdtLines_Passes()
    {
        var result = await Stage(CarveOut).ExecuteAsync(Context(July1, "S1", null, "99213"), CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public async Task UnknownMember_IsLeftToBenefitCalculation()
    {
        var context = Context(July1);
        context.MemberLookup = MemberLookupOutcome.NotFound;
        var result = await Stage(CarveOut).ExecuteAsync(context, CancellationToken.None);
        Assert.Equal(ClaimAdjudicationOutcome.Pass, result.Outcome);
    }

    [Fact]
    public void Stage_RunsAfterProviderIntegrity_BeforeNetwork()
    {
        var stage = Stage(null);
        Assert.InRange(stage.Order, 151, 199);
        Assert.False(stage.IsRequired);
    }
}
