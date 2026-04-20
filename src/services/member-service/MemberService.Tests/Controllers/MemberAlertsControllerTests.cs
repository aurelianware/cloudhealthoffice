using MemberService.Controllers;
using MemberService.Models;
using MemberService.Services;
using MemberService.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace MemberService.Tests.Controllers;

public class MemberAlertsControllerTests
{
    private const string Tenant = "tenant-test";
    private const string MemberId = "M-001";

    private static (MemberAlertsController ctl,
                    InMemoryMemberRepository members,
                    InMemoryMemberAlertRepository alerts,
                    InMemoryMemberEventRepository events) Build()
    {
        var members = new InMemoryMemberRepository();
        members.Members.Add(new Member
        {
            TenantId = Tenant,
            MemberId = MemberId,
            FirstName = "Alice",
            LastName = "Example",
            DateOfBirth = new DateTime(1985, 6, 15),
            EffectiveDate = new DateTime(2024, 1, 1),
            GroupNumber = "GRP",
            IsSubscriber = true
        });
        var alerts = new InMemoryMemberAlertRepository();
        var events = new InMemoryMemberEventRepository();
        var publisher = new CosmosMemberEventPublisher(events, NullLogger<CosmosMemberEventPublisher>.Instance);
        var projector = new FhirFlagProjector();

        var ctl = new MemberAlertsController(members, alerts, publisher, projector);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };
        return (ctl, members, alerts, events);
    }

    private static CreateMemberAlertRequest Req(
        MemberAlertType type = MemberAlertType.LitigationHold,
        MemberAlertSeverity severity = MemberAlertSeverity.Critical,
        DateTime? endDate = null) => new()
        {
            AlertType = type,
            Severity = severity,
            Reason = "Outside counsel filing pending",
            RequiredAction = "Route through legal",
            EndDate = endDate
        };

    [Fact]
    public async Task CreateAlert_PersistsAndEmitsAuditEvent()
    {
        var (ctl, _, alerts, events) = Build();

        var resp = await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);
        resp.Should().BeOfType<CreatedAtActionResult>();

        alerts.Alerts.Should().ContainSingle();
        var stored = alerts.Alerts[0];
        stored.AlertType.Should().Be(MemberAlertType.LitigationHold);
        stored.Severity.Should().Be(MemberAlertSeverity.Critical);
        stored.EndDate.Should().BeNull();
        stored.CreatedBy.Should().NotBeNullOrEmpty();

        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberAlertCreated);
        var created = events.All.First(e => e.EventType == MemberEventType.MemberAlertCreated);
        created.Payload!["alertType"]!.ToString().Should().Be("LitigationHold");
    }

    [Fact]
    public async Task CreateAlert_UnknownMember_Returns404()
    {
        var (ctl, _, _, _) = Build();
        var resp = await ctl.CreateAlert("NOPE", Req(), CancellationToken.None);
        resp.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task ListAlerts_StatusActive_ExcludesEndDated()
    {
        var (ctl, _, _, _) = Build();
        await ctl.CreateAlert(MemberId, Req(MemberAlertType.LitigationHold), CancellationToken.None);
        await ctl.CreateAlert(MemberId, Req(MemberAlertType.VIP, MemberAlertSeverity.Info,
            endDate: DateTime.UtcNow.AddDays(-1)), CancellationToken.None);

        var resp = await ctl.ListAlerts(MemberId, status: "active", CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberAlertListResponse>().Subject;
        body.Items.Should().ContainSingle(a => a.AlertType == MemberAlertType.LitigationHold);
        body.Items.Should().NotContain(a => a.AlertType == MemberAlertType.VIP);
    }

    [Fact]
    public async Task ListAlerts_NoStatus_ReturnsAll()
    {
        var (ctl, _, _, _) = Build();
        await ctl.CreateAlert(MemberId, Req(MemberAlertType.LitigationHold), CancellationToken.None);
        await ctl.CreateAlert(MemberId, Req(MemberAlertType.VIP, MemberAlertSeverity.Info,
            endDate: DateTime.UtcNow.AddDays(-1)), CancellationToken.None);

        var resp = await ctl.ListAlerts(MemberId, status: null, CancellationToken.None);
        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberAlertListResponse>().Subject;
        body.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListAlerts_EmitsViewedAuditEvent()
    {
        var (ctl, _, _, events) = Build();
        await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);
        var baseline = events.All.Count;

        await ctl.ListAlerts(MemberId, status: "active", CancellationToken.None);

        events.All.Count.Should().BeGreaterThan(baseline);
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberAlertViewed);
    }

    [Fact]
    public async Task GetAlert_EmitsViewedAuditEvent()
    {
        var (ctl, _, _, events) = Build();
        var created = (CreatedAtActionResult)await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);
        var alert = (MemberAlert)created.Value!;

        await ctl.GetAlert(MemberId, alert.Id, CancellationToken.None);
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberAlertViewed);
    }

    [Fact]
    public async Task EndAlert_SetsEndDate_AndEmitsAuditEvent()
    {
        var (ctl, _, alerts, events) = Build();
        var created = (CreatedAtActionResult)await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);
        var alert = (MemberAlert)created.Value!;

        var resp = await ctl.EndAlert(MemberId, alert.Id,
            new EndMemberAlertRequest(), CancellationToken.None);
        resp.Should().BeOfType<OkObjectResult>();

        alerts.Alerts[0].EndDate.Should().NotBeNull();
        alerts.Alerts[0].IsActive().Should().BeFalse();
        events.All.Should().Contain(e => e.EventType == MemberEventType.MemberAlertEnded);
    }

    [Fact]
    public async Task EndAlert_AlreadyEnded_IsIdempotent()
    {
        var (ctl, _, alerts, events) = Build();
        var created = (CreatedAtActionResult)await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);
        var alert = (MemberAlert)created.Value!;
        await ctl.EndAlert(MemberId, alert.Id, new EndMemberAlertRequest(), CancellationToken.None);
        var endedEventsBefore = events.All.Count(e => e.EventType == MemberEventType.MemberAlertEnded);

        var resp = await ctl.EndAlert(MemberId, alert.Id, new EndMemberAlertRequest(), CancellationToken.None);
        resp.Should().BeOfType<OkObjectResult>();
        events.All.Count(e => e.EventType == MemberEventType.MemberAlertEnded)
            .Should().Be(endedEventsBefore, "second end is a no-op");
    }

    [Fact]
    public async Task ListAlerts_AuditPublisherFailure_DoesNotFailRead()
    {
        // View audit is best-effort. Simulate publisher throwing and verify
        // the list endpoint still returns 200 with the persisted alerts.
        var members = new InMemoryMemberRepository();
        members.Members.Add(new Member
        {
            TenantId = Tenant, MemberId = MemberId,
            FirstName = "A", LastName = "B",
            DateOfBirth = DateTime.UtcNow.AddYears(-30),
            EffectiveDate = DateTime.UtcNow.AddYears(-1),
            GroupNumber = "GRP", IsSubscriber = true
        });
        var alerts = new InMemoryMemberAlertRepository();
        alerts.Alerts.Add(new MemberAlert
        {
            TenantId = Tenant, MemberId = MemberId, Id = "a1",
            AlertType = MemberAlertType.VIP, Severity = MemberAlertSeverity.Info,
            StartDate = DateTime.UtcNow.AddDays(-1), Reason = "r", CreatedBy = "csr"
        });

        var throwingPublisher = new Mock<MemberService.Services.IMemberEventPublisher>();
        throwingPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MemberEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("downstream audit store unavailable"));

        var ctl = new MemberAlertsController(members, alerts, throwingPublisher.Object,
            new FhirFlagProjector(), NullLogger<MemberAlertsController>.Instance);
        var http = new DefaultHttpContext();
        http.Items["TenantId"] = Tenant;
        ctl.ControllerContext = new ControllerContext { HttpContext = http };

        var resp = await ctl.ListAlerts(MemberId, status: "active", CancellationToken.None);

        var ok = resp.Should().BeOfType<OkObjectResult>().Subject;
        var body = ok.Value.Should().BeOfType<MemberAlertListResponse>().Subject;
        body.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task GetFhirFlags_ReturnsBundleWithFhirContentType()
    {
        var (ctl, _, _, _) = Build();
        await ctl.CreateAlert(MemberId, Req(), CancellationToken.None);

        var resp = await ctl.GetFhirFlags(MemberId, status: "active", CancellationToken.None);
        var content = resp.Should().BeOfType<ContentResult>().Subject;
        content.ContentType.Should().Be("application/fhir+json");
        content.Content.Should().Contain("\"resourceType\":\"Bundle\"");
        content.Content.Should().Contain("\"resourceType\":\"Flag\"");
        content.Content.Should().Contain("LitigationHold");
    }
}
