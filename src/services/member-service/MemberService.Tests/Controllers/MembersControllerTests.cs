using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MemberService.Tests.Controllers;

public class MembersControllerTests
{
    private const string Tenant = "tenant-test";

    private static (MembersController ctl,
                    InMemoryMemberRepository repo,
                    InMemoryMemberEventRepository events,
                    Mock<ICoverageServiceClient> coverage,
                    Mock<IEnrollmentImportServiceClient> enrollment,
                    Mock<IAccumulatorServiceClient> acc,
                    InMemoryMemberAlertRepository alerts) Build()
    {
        var repo = new InMemoryMemberRepository();
        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var projector = new FhirPatientProjector();
        var enc = new NoOpIdentifierEncryptor();
        var coverage = new Mock<ICoverageServiceClient>();
        var enrollment = new Mock<IEnrollmentImportServiceClient>();
        var acc = new Mock<IAccumulatorServiceClient>();
        var alerts = new InMemoryMemberAlertRepository();
        var guard = new MemberAlertGuard(alerts);

        var ctl = new MembersController(repo, publisher, events, projector, enc,
            coverage.Object, enrollment.Object, acc.Object,
            relationshipShim: null, familyRelationships: null, alertGuard: guard);

        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, repo, events, coverage, enrollment, acc, alerts);
    }

    private static CreateMemberRequest CreateReq(string memberId = "M-001") => new()
    {
        MemberId = memberId,
        GroupNumber = "GRP",
        IsSubscriber = true,
        FirstName = "Alice",
        LastName = "Example",
        DateOfBirth = new DateTime(1985, 6, 15),
        EffectiveDate = new DateTime(2024, 1, 1),
        Gender = "F",
        Address = "1 Main",
        City = "Austin",
        State = "TX",
        ZipCode = "78701",
        SSN = "123-45-6789"
    };

    [Fact]
    public async Task CreateMember_Persists_AndEmitsMemberCreatedWithSnapshot()
    {
        var (ctl, repo, events, _, _, _, _) = Build();
        var resp = await ctl.CreateMember(CreateReq(), CancellationToken.None);

        resp.Should().BeOfType<CreatedAtActionResult>();
        repo.Members.Should().ContainSingle();
        var member = repo.Members[0];
        member.Status.Should().Be(EnrollmentStatus.Pending);
        member.Identifiers.Should().ContainSingle(i => i.Type == MemberIdentifierType.SSN);

        events.All.Should().ContainSingle();
        var created = events.All[0];
        created.EventType.Should().Be(MemberEventType.MemberCreated);
        created.Version.Should().Be(1);
        created.Payload.Should().NotBeNull();
        created.Payload!["memberId"]!.ToString().Should().Be("M-001");
        // Genesis event carries full snapshot, not just a diff.
        created.Payload!["firstName"]!.ToString().Should().Be("Alice");
        created.Payload!["lineOfBusiness"].Should().NotBeNull();
    }

    [Fact]
    public async Task CreateMember_DuplicateMemberId_Returns409()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);
        var second = await ctl.CreateMember(CreateReq(), CancellationToken.None);
        second.Should().BeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task CreateMember_DependentWithUnknownSubscriber_ReturnsBadRequest()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        var req = CreateReq("D-001");
        req.IsSubscriber = false;
        req.SubscriberMemberId = "DOES-NOT-EXIST";
        var resp = await ctl.CreateMember(req, CancellationToken.None);
        resp.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task UpdateMember_AddressChange_EmitsAddressChangedAndMemberUpdated()
    {
        var (ctl, _, events, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        var resp = await ctl.UpdateMember("M-001",
            new UpdateMemberRequest { Address = "500 New St", City = "Dallas" },
            CancellationToken.None);
        resp.Should().BeOfType<OkObjectResult>();

        events.All.Select(e => e.EventType).Should()
            .Contain(MemberEventType.MemberCreated)
            .And.Contain(MemberEventType.MemberUpdated)
            .And.Contain(MemberEventType.AddressChanged);
    }

    [Fact]
    public async Task UpdateMember_NoChanges_IsNoOp()
    {
        var (ctl, repo, events, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);
        var baselineCount = events.All.Count;

        var resp = await ctl.UpdateMember("M-001", new UpdateMemberRequest(), CancellationToken.None);
        resp.Should().BeOfType<OkObjectResult>();
        events.All.Count.Should().Be(baselineCount); // no new events
    }

    [Fact]
    public async Task UpdateMember_NotFound_Returns404()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        var resp = await ctl.UpdateMember("MISSING", new UpdateMemberRequest { City = "X" }, CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task TerminateMember_Delete_MarksTerminatedAndEmits()
    {
        var (ctl, repo, events, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        var resp = await ctl.TerminateMember("M-001",
            terminationDate: new DateTime(2024, 12, 31),
            reasonCode: "25",
            eventId: null,
            ct: CancellationToken.None);
        resp.Should().BeOfType<NoContentResult>();

        repo.Members[0].Status.Should().Be(EnrollmentStatus.Terminated);
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberTerminated);
    }

    [Fact]
    public async Task CheckEligibility_ActiveMember_ReturnsEligible()
    {
        var (ctl, repo, _, _, _, _, _) = Build();
        var req = CreateReq();
        req.EffectiveDate = DateTime.UtcNow.AddMonths(-1);
        await ctl.CreateMember(req, CancellationToken.None);
        repo.Members[0].Status = EnrollmentStatus.Active;

        var resp = await ctl.CheckEligibility("M-001", null);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<EligibilityCheckResponse>().Subject;
        body.IsEligible.Should().BeTrue();
    }

    [Fact]
    public async Task CheckEligibility_Terminated_ReturnsNotEligible()
    {
        var (ctl, repo, _, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);
        repo.Members[0].Status = EnrollmentStatus.Terminated;

        var resp = await ctl.CheckEligibility("M-001", null);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<EligibilityCheckResponse>().Subject;
        body.IsEligible.Should().BeFalse();
    }

    [Fact]
    public async Task CheckEligibility_Missing_Returns404()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        (await ctl.CheckEligibility("nope", null)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetFhirPatient_ReturnsFhirContentType_And_PatientResource()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        var resp = await ctl.GetFhirPatient("M-001");
        var content = resp.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"Patient\"");
    }

    [Fact]
    public async Task GetEvents_ReturnsOrderedStream()
    {
        var (ctl, _, _, _, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);
        await ctl.UpdateMember("M-001", new UpdateMemberRequest { City = "Houston" }, CancellationToken.None);

        var resp = await ctl.GetEvents("M-001", CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var events = (IReadOnlyList<MemberEvent>)ok.Value!;
        events.Count.Should().BeGreaterThanOrEqualTo(2);
        events.Select(e => e.Version).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task GetMemberPcp_WhenCoverageUnavailable_Returns503ProblemDetails()
    {
        var (ctl, _, _, coverage, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        coverage.Setup(c => c.GetPcpAsync(Tenant, "M-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamUnavailableException("coverage-service", "no base url"));

        var resp = await ctl.GetMemberPcp("M-001", CancellationToken.None);
        var status = resp.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        var problem = status.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(503);
        problem.Extensions["service"].Should().Be("coverage-service");
    }

    [Fact]
    public async Task AssignPcp_EmitsPcpChangedEvent()
    {
        var (ctl, _, events, coverage, _, _, _) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);
        coverage.Setup(c => c.AssignPcpAsync(Tenant, "M-001", It.IsAny<AssignPcpRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AssignPcpOutcome { Pcp = new MemberPcpResponse { ProviderId = "prov-1" } });

        var resp = await ctl.AssignPcp("M-001",
            new AssignPcpRequest { ProviderId = "prov-1", EffectiveDate = DateTime.UtcNow },
            CancellationToken.None);
        resp.Should().BeOfType<OkObjectResult>();
        events.All.Should().Contain(e => e.EventType == MemberEventType.PcpChanged);
    }

    [Fact]
    public async Task GetCoverageHistory_WhenUnavailable_Returns503()
    {
        var (ctl, _, _, coverage, _, _, _) = Build();
        coverage.Setup(c => c.GetCoverageHistoryAsync(Tenant, "M-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamUnavailableException("coverage-service"));

        var resp = await ctl.GetCoverageHistory("M-001", CancellationToken.None);
        ((ObjectResult)resp).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task Get834Transactions_WhenUnavailable_Returns503()
    {
        var (ctl, _, _, _, enrollment, _, _) = Build();
        enrollment.Setup(e => e.Get834TransactionsAsync(Tenant, "M-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamUnavailableException("enrollment-import-service"));

        var resp = await ctl.Get834Transactions("M-001", CancellationToken.None);
        ((ObjectResult)resp).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task GetEnrollmentEvents_ProxiesToDownstream()
    {
        var (ctl, _, _, _, enrollment, _, _) = Build();
        enrollment.Setup(e => e.GetEnrollmentEventsAsync(
                Tenant, "M-001", null, null, null, 50, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnrollmentEventListResponse
            {
                Items = new()
                {
                    new EnrollmentEventRecord { EventId = "e1", EventType = "Enrolled", Version = 1 }
                }
            });

        var resp = await ctl.GetEnrollmentEvents("M-001", null, null, null, 50, null, CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var page = ok.Value.Should().BeOfType<EnrollmentEventListResponse>().Subject;
        page.Items.Should().ContainSingle().Which.EventId.Should().Be("e1");
    }

    [Fact]
    public async Task GetEnrollmentEvents_WhenDownstreamDown_Returns503()
    {
        var (ctl, _, _, _, enrollment, _, _) = Build();
        enrollment.Setup(e => e.GetEnrollmentEventsAsync(
                Tenant, "M-001", null, null, null, 50, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamUnavailableException("enrollment-import-service"));

        var resp = await ctl.GetEnrollmentEvents("M-001", null, null, null, 50, null, CancellationToken.None);
        ((ObjectResult)resp).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task GetAccumulators_WhenUnavailable_Returns503()
    {
        var (ctl, _, _, _, _, accu, _) = Build();
        accu.Setup(a => a.GetAccumulatorsAsync(Tenant, "M-001", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new DownstreamUnavailableException("accumulator-service"));

        var resp = await ctl.GetAccumulators("M-001", CancellationToken.None);
        ((ObjectResult)resp).StatusCode.Should().Be(503);
    }

    [Fact]
    public async Task TerminateMember_Delete_BlockedByActiveLitigationHold_Returns409()
    {
        var (ctl, _, _, _, _, _, alerts) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        alerts.Alerts.Add(new MemberAlert
        {
            TenantId = Tenant, MemberId = "M-001", Id = Guid.NewGuid().ToString(),
            AlertType = MemberAlertType.LitigationHold,
            Severity = MemberAlertSeverity.Critical,
            StartDate = DateTime.UtcNow.AddDays(-1),
            Reason = "Outside counsel notice 2024-04",
            RequiredAction = "Route through legal",
            CreatedBy = "legal-ops"
        });

        var resp = await ctl.TerminateMember("M-001",
            terminationDate: DateTime.UtcNow,
            reasonCode: "25",
            eventId: null,
            ct: CancellationToken.None);

        var status = resp.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        var problem = status.Value.Should().BeOfType<ProblemDetails>().Subject;
        problem.Status.Should().Be(409);
        problem.Extensions["alertType"].Should().Be("LitigationHold");
        problem.Extensions["action"].Should().Be("Terminate");
    }

    [Fact]
    public async Task TerminateMember_Body_BlockedByActiveLitigationHold_Returns409()
    {
        var (ctl, _, events, _, _, _, alerts) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        alerts.Alerts.Add(new MemberAlert
        {
            TenantId = Tenant, MemberId = "M-001", Id = Guid.NewGuid().ToString(),
            AlertType = MemberAlertType.LitigationHold,
            Severity = MemberAlertSeverity.Critical,
            StartDate = DateTime.UtcNow.AddDays(-1),
            Reason = "Hold",
            CreatedBy = "legal-ops"
        });
        var preTerminateEvents = events.All.Count(e => e.EventType == MemberEventType.MemberTerminated);

        var resp = await ctl.TerminateMember("M-001", new TerminateMemberRequest
        {
            MemberId = "M-001",
            TerminationDate = DateTime.UtcNow,
            ReasonCode = "25"
        }, CancellationToken.None);

        ((ObjectResult)resp).StatusCode.Should().Be(StatusCodes.Status409Conflict);
        events.All.Count(e => e.EventType == MemberEventType.MemberTerminated)
            .Should().Be(preTerminateEvents, "termination must not have been recorded");
    }

    [Fact]
    public async Task TerminateMember_EndedLitigationHold_AllowsTermination()
    {
        var (ctl, repo, _, _, _, _, alerts) = Build();
        await ctl.CreateMember(CreateReq(), CancellationToken.None);

        alerts.Alerts.Add(new MemberAlert
        {
            TenantId = Tenant, MemberId = "M-001", Id = Guid.NewGuid().ToString(),
            AlertType = MemberAlertType.LitigationHold,
            Severity = MemberAlertSeverity.Critical,
            StartDate = DateTime.UtcNow.AddDays(-10),
            EndDate = DateTime.UtcNow.AddMinutes(-1),  // already ended
            Reason = "Hold",
            CreatedBy = "legal-ops"
        });

        var resp = await ctl.TerminateMember("M-001",
            terminationDate: DateTime.UtcNow,
            reasonCode: "25",
            eventId: null,
            ct: CancellationToken.None);
        resp.Should().BeOfType<NoContentResult>();
        repo.Members[0].Status.Should().Be(EnrollmentStatus.Terminated);
    }

    [Fact]
    public async Task GetDependents_ReturnsOnlyNonSubscribersLinked()
    {
        var (ctl, repo, _, _, _, _, _) = Build();
        var sub = CreateReq("SUB-1");
        await ctl.CreateMember(sub, CancellationToken.None);

        var dep = CreateReq("DEP-1");
        dep.IsSubscriber = false;
        dep.SubscriberMemberId = "SUB-1";
        await ctl.CreateMember(dep, CancellationToken.None);

        var resp = await ctl.GetDependents("SUB-1");
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var deps = ok.Value.Should().BeAssignableTo<IEnumerable<Member>>().Subject;
        deps.Should().ContainSingle().Which.MemberId.Should().Be("DEP-1");
    }

}
