using AuthorizationService.Backends;
using AuthorizationService.Models;
using AuthorizationService.Repositories;
using AuthorizationService.Services.StatePrograms;
using HttpStatusCode = System.Net.HttpStatusCode;
using CloudHealthOffice.Infrastructure.Programs.MediCal;
using CloudHealthOffice.Infrastructure.Security;
using CloudHealthOffice.PlanConfiguration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace CloudHealthOffice.AuthorizationService.Tests;

/// <summary>DHCS APL 26-009 applied to dental prior authorization requests.</summary>
public class StateOnlyAdultDentalPaTests
{
    private const string CarveOut =
        """{"mode":"CarveOut","stateOnlyAidCodes":["S1"]}""";

    private const string EmergencyOnly =
        """{"mode":"EmergencyOnly","stateOnlyAidCodes":["S1","SP"],"exceptionAidCodes":["SP"],"emergencyProcedureCodes":["D0140","D9110"]}""";

    private static readonly DateTime July1 = new(2027, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AdultDob = new(1990, 3, 15);

    private readonly IPlanSettings _settings = Substitute.For<IPlanSettings>();
    private readonly IStateDentalMemberLookup _members = Substitute.For<IStateDentalMemberLookup>();

    public StateOnlyAdultDentalPaTests()
    {
        Setting("\"off\"");
        Coverage(new ActiveCoveragePlan("COV-1", "DENTAL-PLAN"));
        Member("S1");
    }

    private void Setting(string value, bool fallback = false) =>
        _settings.ResolveAsync(default!, default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new ResolvedSetting
            {
                Key = StateOnlyAdultDentalPaCheck.SettingKey,
                Value = value,
                SourceLevel = SettingLevel.BenefitPlan,
                IsFallback = fallback,
            }));

    private void Coverage(params ActiveCoveragePlan[] plans) =>
        _members.ActiveCoverageAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<ActiveCoveragePlan>>(plans));

    private void Member(string? aidCode, DateTime? dob = null) =>
        _members.MemberAsync(default!, default!, default)
            .ReturnsForAnyArgs(Task.FromResult<StateDentalMember?>(new StateDentalMember(
                dob ?? AdultDob,
                aidCode is null ? [] : [new AidCodeSpan(aidCode, new DateTime(2027, 1, 1))])));

    private StateOnlyAdultDentalPaCheck Check() =>
        new(_settings, _members, NullLogger<StateOnlyAdultDentalPaCheck>.Instance);

    private static Authorization Request(DateTime? dos = null, DateTime? dob = null, params string[] codes)
    {
        codes = codes.Length == 0 ? ["D2740"] : codes;
        return new Authorization
        {
            TenantId = "t1",
            AuthorizationNumber = "PA-1",
            MemberId = "M1",
            CoverageId = "COV-1",
            LineOfBusiness = LineOfBusiness.Medicaid,
            PatientDateOfBirth = dob ?? AdultDob,
            RequestedServiceDateFrom = dos ?? July1,
            Status = AuthorizationStatus.Submitted,
            RequestedServices = codes.Select((c, i) => new RequestedService
            {
                LineNumber = i + 1, ProcedureCode = c, RequestedUnits = 1,
            }).ToList(),
        };
    }

    private static IAuthorizationRepository Repository()
    {
        var repo = Substitute.For<IAuthorizationRepository>();
        repo.CreateAsync(Arg.Any<Authorization>()).Returns(c => c.Arg<Authorization>());
        return repo;
    }

    [Fact]
    public async Task MedicalRequest_IsNotTouched_AndNothingIsLookedUp()
    {
        Setting(CarveOut);
        Assert.Null(await Check().EvaluateAsync(Request(codes: "72148")));
        await _members.DidNotReceiveWithAnyArgs().ActiveCoverageAsync(default!, default!, default, default);
        await _settings.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task BeforeJuly2027_IsNotTouched()
    {
        Setting(CarveOut);
        Assert.Null(await Check().EvaluateAsync(Request(new DateTime(2027, 6, 30))));
        await _settings.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task UnderNineteenByTheRequest_IsNotTouched_WithoutLookups()
    {
        Setting(CarveOut);
        Assert.Null(await Check().EvaluateAsync(Request(dob: new DateTime(2010, 5, 2))));
        await _members.DidNotReceiveWithAnyArgs().MemberAsync(default!, default!, default);
    }

    [Fact]
    public async Task SettingOff_IsNotTouched_AndTheMemberIsNotRead()
    {
        Assert.Null(await Check().EvaluateAsync(Request()));
        await _members.DidNotReceiveWithAnyArgs().MemberAsync(default!, default!, default);
    }

    [Fact]
    public async Task CarveOut_AllDental_Denies()
    {
        Setting(CarveOut);
        var result = await Check().EvaluateAsync(Request());

        Assert.NotNull(result);
        Assert.Equal(StateDentalPaOutcome.Deny, result!.Outcome);
        Assert.Equal(StateOnlyAdultDentalPaCheck.CarvedOutReasonCode, result.DenialReasonCode);
        Assert.Contains("fee-for-service", result.Reason);
    }

    [Fact]
    public async Task CarveOut_DentalAndMedicalCodes_Pends()
    {
        Setting(CarveOut);
        var result = await Check().EvaluateAsync(Request(codes: ["D2740", "41899"]));
        Assert.Equal(StateDentalPaOutcome.Pend, result!.Outcome);
    }

    [Fact]
    public async Task CarveOut_FederallyFundedAidCode_IsNotTouched()
    {
        Setting(CarveOut);
        Member("1H");
        Assert.Null(await Check().EvaluateAsync(Request()));
    }

    [Fact]
    public async Task EmergencyOnly_NonEmergencyProcedure_PendsNamingIt()
    {
        Setting(EmergencyOnly);
        var result = await Check().EvaluateAsync(Request(codes: ["D9110", "D2740"]));

        Assert.Equal(StateDentalPaOutcome.Pend, result!.Outcome);
        Assert.Contains("D2740", result.Reason);
        Assert.DoesNotContain("D9110", result.Reason);
        Assert.Contains("emergency", result.Reason);
    }

    [Fact]
    public async Task EmergencyOnly_EmergencyOrException_IsNotTouched()
    {
        Setting(EmergencyOnly);
        Assert.Null(await Check().EvaluateAsync(Request(codes: "D9110")));
        Member("SP");
        Assert.Null(await Check().EvaluateAsync(Request(codes: "D2740")));
    }

    [Fact]
    public async Task NoAidCodeOnFile_Pends()
    {
        Setting(CarveOut);
        Member(null);
        var result = await Check().EvaluateAsync(Request());
        Assert.Equal(StateDentalPaOutcome.Pend, result!.Outcome);
        Assert.Contains("no aid code", result.Reason);
    }

    [Fact]
    public async Task SettingUnavailable_Pends()
    {
        Setting("\"off\"", fallback: true);
        var result = await Check().EvaluateAsync(Request());
        Assert.Equal(StateDentalPaOutcome.Pend, result!.Outcome);
    }

    [Fact]
    public async Task UnreadableSetting_Pends()
    {
        Setting("""{"mode":"Sideways"}""");
        var result = await Check().EvaluateAsync(Request());
        Assert.Equal(StateDentalPaOutcome.Pend, result!.Outcome);
        Assert.Contains("not valid", result.Reason);
    }

    [Fact]
    public async Task CoverageOrMemberServiceDown_Pends()
    {
        Setting(CarveOut);
        _members.ActiveCoverageAsync(default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new HttpRequestException("down"));
        Assert.Equal(StateDentalPaOutcome.Pend, (await Check().EvaluateAsync(Request()))!.Outcome);

        Coverage(new ActiveCoveragePlan("COV-1", "DENTAL-PLAN"));
        _members.MemberAsync(default!, default!, default).ThrowsAsyncForAnyArgs(new HttpRequestException("down"));
        Assert.Equal(StateDentalPaOutcome.Pend, (await Check().EvaluateAsync(Request()))!.Outcome);
    }

    [Fact]
    public async Task Setting_IsReadAtTheRequestsLineOfBusinessAndTheMembersPlan()
    {
        Setting(CarveOut);
        await Check().EvaluateAsync(Request());
        await _settings.Received(1).ResolveAsync(
            "t1", StateOnlyAdultDentalPaCheck.SettingKey,
            Arg.Is<SettingScope>(s => s.LineOfBusiness == "Medicaid" && s.BenefitPlan == "DENTAL-PLAN"),
            July1.Date, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void BenefitPlan_IsTheNamedCoveragesPlan_ElseTheOnlyPlan()
    {
        ActiveCoveragePlan[] two = [new("COV-1", "A"), new("COV-2", "B")];
        Assert.Equal("B", StateOnlyAdultDentalPaCheck.BenefitPlanFor(two, "COV-2"));
        Assert.Null(StateOnlyAdultDentalPaCheck.BenefitPlanFor(two, null));
        Assert.Equal("A", StateOnlyAdultDentalPaCheck.BenefitPlanFor([new("COV-9", "A")], "COV-2"));
        Assert.Null(StateOnlyAdultDentalPaCheck.BenefitPlanFor([], "COV-2"));
    }

    [Fact]
    public async Task Backend_Create_DeniesACarvedOutMembersDentalRequest()
    {
        Setting(CarveOut);
        var repo = Repository();
        var stored = await new ChoAuthorizationBackend(repo, stateDentalCheck: Check()).CreateAsync(Request());

        Assert.Equal(AuthorizationStatus.Denied, stored.Status);
        Assert.Equal("A3", stored.ReviewDecision);
        Assert.Equal(StateOnlyAdultDentalPaCheck.CarvedOutReasonCode, stored.DenialReasonCode);
        Assert.All(stored.RequestedServices, s => Assert.Equal(StateOnlyAdultDentalPaCheck.CarvedOutReasonCode, s.DenialReasonCode));
        Assert.Equal(AuthorizationStatus.Denied, stored.StatusHistory[^1].Status);
        await repo.Received(1).CreateAsync(Arg.Any<Authorization>());
    }

    [Fact]
    public async Task Backend_Create_PendsANonEmergencyRequest()
    {
        Setting(EmergencyOnly);
        var stored = await new ChoAuthorizationBackend(Repository(), stateDentalCheck: Check()).CreateAsync(Request());

        Assert.Equal(AuthorizationStatus.Pended, stored.Status);
        Assert.Equal("A4", stored.ReviewDecision);
        Assert.Contains("D2740", stored.PendReason);
    }

    [Fact]
    public async Task Backend_Create_RuleNotApplicable_StaysSubmitted()
    {
        var stored = await new ChoAuthorizationBackend(Repository(), stateDentalCheck: Check()).CreateAsync(Request());
        Assert.Equal(AuthorizationStatus.Submitted, stored.Status);
    }

    [Fact]
    public async Task Backend_Automated_PendOutranksAnApproval_ButNotADenial()
    {
        Setting(EmergencyOnly);
        var backend = new ChoAuthorizationBackend(Repository(), stateDentalCheck: Check());

        var approved = await backend.RecordAutomatedDecisionAsync(Request(),
            new AutomatedDecision { Outcome = AutomatedDecisionOutcome.Approved, ApprovedServiceDateFrom = July1, ApprovedServiceDateTo = July1.AddDays(30) });
        Assert.Equal(AuthorizationStatus.Pended, approved.Status);
        Assert.Equal(ChoAuthorizationBackend.StateDentalSource, approved.StatusHistory[^1].Source);

        var denied = await backend.RecordAutomatedDecisionAsync(Request(),
            new AutomatedDecision { Outcome = AutomatedDecisionOutcome.Denied, DenialReasonCode = "NOTMEDNEC", DenialReason = "Not necessary." });
        Assert.Equal(AuthorizationStatus.Denied, denied.Status);
        Assert.Equal("NOTMEDNEC", denied.DenialReasonCode);
    }

    [Fact]
    public async Task Backend_Automated_CarveOutDenialOutranksAnApproval()
    {
        Setting(CarveOut);
        var stored = await new ChoAuthorizationBackend(Repository(), stateDentalCheck: Check()).RecordAutomatedDecisionAsync(Request(),
            new AutomatedDecision { Outcome = AutomatedDecisionOutcome.Approved, ApprovedServiceDateFrom = July1, ApprovedServiceDateTo = July1.AddDays(30) });

        Assert.Equal(AuthorizationStatus.Denied, stored.Status);
        Assert.Equal(StateOnlyAdultDentalPaCheck.CarvedOutReasonCode, stored.DenialReasonCode);
        Assert.Null(stored.ApprovedServiceDateFrom);
    }

    [Fact]
    public async Task Lookups_UseTheServiceIdentity_AndReadAidCodes()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("/coverage/") => """[{"id":"COV-1","planId":"DENTAL-PLAN"},{"id":"COV-2"}]""",
            var p when p.Contains("/members/") =>
                """{"memberId":"M1","dateOfBirth":"1990-03-15T00:00:00Z","aidCodes":[{"code":" S1 ","effectiveDate":"2027-01-01T00:00:00Z"},{"code":"","effectiveDate":"2027-01-01T00:00:00Z"}]}""",
            _ => """{"key":"eligibility.mediCal.stateOnlyAdultDental","value":"\"off\""}""",
        });
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, false) { BaseAddress = new Uri("http://svc/") });
        var lookup = new HttpStateDentalMemberLookup(factory);

        var coverage = await lookup.ActiveCoverageAsync("t1", "M1", July1, CancellationToken.None);
        var member = await lookup.MemberAsync("t1", "M1", CancellationToken.None);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        await new HttpPlanSettings(factory, cache, new PlanSettingsOptions { UseServiceIdentity = true }, TimeProvider.System,
                NullLogger<HttpPlanSettings>.Instance)
            .ResolveAsync("t1", StateOnlyAdultDentalPaCheck.SettingKey, new SettingScope(), July1);

        Assert.Equal(new ActiveCoveragePlan("COV-1", "DENTAL-PLAN"), Assert.Single(coverage));
        Assert.Equal("S1", Assert.Single(member!.AidCodes).Code);
        Assert.Equal(AdultDob, member.DateOfBirth);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.True(r.Options.TryGetValue(ChoOutboundTokenHandler.ServiceIdentityRequired, out var required) && required);
            Assert.Equal("t1", r.Headers.GetValues("X-Tenant-ID").Single());
        });
    }

    [Fact]
    public async Task MemberLookup_NotFound_IsNull_AndAnOutageThrows()
    {
        var status = HttpStatusCode.NotFound;
        var handler = new StubHandler(_ => "{}", () => status);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, false) { BaseAddress = new Uri("http://svc/") });
        var lookup = new HttpStateDentalMemberLookup(factory);

        Assert.Null(await lookup.MemberAsync("t1", "M1", CancellationToken.None));
        Assert.Empty(await lookup.ActiveCoverageAsync("t1", "M1", July1, CancellationToken.None));
        status = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.MemberAsync("t1", "M1", CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> body, Func<HttpStatusCode>? status = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(status?.Invoke() ?? HttpStatusCode.OK)
            {
                Content = new StringContent(body(request), System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
